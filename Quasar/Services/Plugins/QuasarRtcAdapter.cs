using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.IO.Pipelines;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;
using Microsoft.Net.Http.Headers;
using Quasar.Plugin.Abstractions.Operations;

namespace Quasar.Services.Plugins;

/// <summary>The peer carries registered SDK operations, never local URLs or caller claims.</summary>
public sealed class QuasarRtcAdapter(QuasarPeerProcess native, QuasarBrowserSessions sessions,
    QuasarTransferRegistry transfers, IServiceScopeFactory scopes, ILogger<QuasarRtcAdapter> logger)
{
    public async Task RunAsync(QuasarRtcSessions.Peer peer)
    {
        using var control = await native.ConnectChannelAsync(peer.Id, "control", peer.Token);
        using var bulk = await native.ConnectChannelAsync(peer.Id, "bulk", peer.Token);
        var connection = new Connection(peer, control, bulk, sessions, transfers, scopes, logger);
        await connection.RunAsync();
    }

    private sealed class Connection(QuasarRtcSessions.Peer peer, ClientWebSocket control, ClientWebSocket bulk,
        QuasarBrowserSessions sessions, QuasarTransferRegistry transfers, IServiceScopeFactory scopes, ILogger logger)
    {
        private readonly SemaphoreSlim controlWrite = new(1, 1);
        private readonly SemaphoreSlim bulkWrite = new(1, 1);
        private readonly ConcurrentDictionary<uint, Call> calls = new();
        private readonly ConcurrentDictionary<string, QuasarTransferRegistry.Transfer> owned = new(StringComparer.Ordinal);
        private uint lastId;
        private int circuit;

        public async Task RunAsync()
        {
            using var abort = peer.Token.Register(() => { control.Abort(); bulk.Abort(); });
            var commands = CommandsAsync();
            var incoming = UploadBytesAsync();
            peer.Ready.TrySetResult();
            try { await await Task.WhenAny(commands, incoming); }
            finally
            {
                peer.Cancel(); control.Abort(); bulk.Abort();
                foreach (var transfer in owned.Values) transfers.Cancel(transfer);
                foreach (var call in calls.Values) call.Cancel();
                try { await Task.WhenAll(commands, incoming); }
                catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException or QuasarOperationException) { }
                await Task.WhenAll(calls.Values.Select(call => call.Done.Task));
            }
        }

        private async Task CommandsAsync()
        {
            var bytes = new byte[QuasarRtcWire.ControlBytes + 1];
            while (true)
            {
                var count = await QuasarRtcWire.ReceiveAsync(control, bytes, WebSocketMessageType.Text, peer.Token);
                if (count > QuasarRtcWire.ControlBytes) throw new QuasarOperationException(413, "Peer control frame is too large.");
                sessions.Verify(peer.Binding, peer.Token);
                using var document = JsonDocument.Parse(bytes.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 34 });
                var body = document.RootElement;
                if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("v", out var version) || !version.TryGetInt32(out var v) || v != 1
                    || !body.TryGetProperty("type", out var typeValue) || typeValue.ValueKind != JsonValueKind.String
                    || !body.TryGetProperty("id", out var number) || !number.TryGetUInt32(out var id) || id == 0)
                    throw new QuasarOperationException(400, "Invalid SDK peer envelope.");
                var type = typeValue.GetString();
                if (type is "credit" or "cancel" or "upload.end")
                {
                    if (!calls.TryGetValue(id, out var target))
                    { if (id > lastId) throw new QuasarOperationException(400, "Unknown SDK request."); continue; }
                    if (type == "cancel") target.Cancel();
                    else if (type == "credit") target.Credit.Acknowledge(Integer(body, "bytes"));
                    else
                    {
                        try { target.FinishUpload(Integer(body, "bytes")); }
                        catch (QuasarOperationException error) { target.Cancel(); await ErrorAsync(id, error.StatusCode, error.Message); }
                    }
                    continue;
                }
                if (type is not ("request" or "reserve" or "start" or "release" or "circuit") || id <= lastId)
                    throw new QuasarOperationException(409, "SDK request IDs are single-use and ordered; writes cannot replay.");
                lastId = id;
                if (type == "release")
                {
                    var resource = Text(body, "resource");
                    if (owned.TryGetValue(resource, out var transfer))
                    { transfers.Cancel(transfer); if (!transfer.Claimed) owned.TryRemove(resource, out _); }
                    await SendControlAsync(new { v = 1, type = "released", id }, peer.Token);
                    continue;
                }
                if (calls.Count >= 8) { await ErrorAsync(id, 429, "Too many concurrent SDK requests."); continue; }
                var request = body.Clone();
                var call = new Call(id, peer.Token, type == "circuit");
                if (!calls.TryAdd(id, call)) throw new QuasarOperationException(409, "Duplicate SDK request.");
                // Feature handlers cannot block the control reader's cancellation and credit path.
                _ = Task.Run(() => InvokeAsync(call, type!, request));
            }
        }

        private async Task UploadBytesAsync()
        {
            var frame = new byte[QuasarRtcWire.ChunkBytes + 8 + 1];
            while (true)
            {
                var count = await QuasarRtcWire.ReceiveAsync(bulk, frame, WebSocketMessageType.Binary, peer.Token);
                sessions.Verify(peer.Binding, peer.Token);
                if (count <= 8 || count > QuasarRtcWire.ChunkBytes + 8) throw new QuasarOperationException(400, "Invalid SDK binary frame.");
                var id = BinaryPrimitives.ReadUInt32LittleEndian(frame);
                var sequence = BinaryPrimitives.ReadUInt32LittleEndian(frame.AsSpan(4));
                if (!calls.TryGetValue(id, out var target))
                { if (id > lastId) throw new QuasarOperationException(400, "Unknown SDK binary request."); continue; }
                try
                {
                    await target.WriteUploadAsync(sequence, frame.AsMemory(8, count - 8));
                    await SendControlAsync(new { v = 1, type = "ack", id, bytes = target.UploadReceived }, peer.Token);
                }
                catch (QuasarOperationException error) { target.Cancel(); await ErrorAsync(id, error.StatusCode, error.Message); }
                catch (OperationCanceledException) when (target.Token.IsCancellationRequested) { }
            }
        }

        private async Task InvokeAsync(Call call, string type, JsonElement request)
        {
            try
            {
                if (type == "circuit") await StartCircuitAsync(call, request);
                else if (type == "start") await StartResourceAsync(call, request);
                else
                {
                    var plugin = Identifier(request, "plugin");
                    var operation = Identifier(request, "operation");
                    var payload = request.TryGetProperty("payload", out var input) ? input : JsonSerializer.SerializeToElement(new { });
                    var raw = payload.GetRawText();
                    if (Encoding.UTF8.GetByteCount(raw) > QuasarSdkLimits.MaximumJsonBytes)
                        throw new QuasarOperationException(413, "SDK requests are limited to 64 KiB.");
                    using var bounded = JsonDocument.Parse(raw, new JsonDocumentOptions { MaxDepth = QuasarSdkLimits.MaximumJsonDepth });
                    await using var scope = scopes.CreateAsyncScope();
                    var operations = scope.ServiceProvider.GetRequiredService<QuasarOperationService>();
                    sessions.Verify(peer.Binding, call.Token);
                    if (type == "request")
                    {
                        async Task Check(CancellationToken token)
                        {
                            sessions.Verify(peer.Binding, token);
                            var denied = await operations.CheckAccessAsync(peer.Binding.User, plugin, operation, QuasarOperationKind.Json);
                            if (denied is not null) throw new QuasarOperationException(denied.StatusCode, denied.Error!);
                        }
                        var watch = QuasarResourceLifetime.WatchAuthorizationAsync(Check, call.Stop);
                        try
                        {
                            sessions.Verify(peer.Binding, call.Token);
                            var refused = await operations.CheckCallAsync(peer.Binding.User, plugin, operation, QuasarOperationKind.Json);
                            if (refused is not null) throw new QuasarOperationException(refused.StatusCode, refused.Error!);
                            var response = await operations.DispatchAsync(new(peer.Binding.User, scope.ServiceProvider), plugin, operation, bounded.RootElement, call.Token);
                            await ResultAsync(call, response, scope.ServiceProvider);
                        }
                        finally { call.Cancel(); await watch; }
                    }
                    else
                    {
                        var kind = Text(request, "kind") switch
                        { "read" => QuasarOperationKind.Read, "upload" => QuasarOperationKind.Upload, "subscription" => QuasarOperationKind.Subscription,
                            _ => throw new QuasarOperationException(400, "Invalid SDK resource kind.") };
                        var denied = await operations.CheckCallAsync(peer.Binding.User, plugin, operation, kind);
                        if (denied is not null) throw new QuasarOperationException(denied.StatusCode, denied.Error!);
                        foreach (var stale in owned.Where(pair => pair.Value.Token.IsCancellationRequested)) owned.TryRemove(stale.Key, out _);
                        var resource = transfers.Create(peer.Binding, plugin, operation, kind, bounded.RootElement);
                        owned[resource.Id] = resource;
                        await SendControlAsync(new { v = 1, type = "reserved", id = call.Id, resource = resource.Id, kind = kind.ToString().ToLowerInvariant() }, call.Token);
                    }
                }
            }
            catch (QuasarOperationException error) { await ErrorAsync(call.Id, error.StatusCode, error.Message); }
            catch (Exception error) when (error is JsonException or FormatException or ArgumentException or OverflowException)
            { await ErrorAsync(call.Id, 400, "Invalid SDK peer request."); }
            catch (OperationCanceledException) when (call.Token.IsCancellationRequested)
            { await ErrorAsync(call.Id, 499, "SDK work was cancelled; a write outcome may be unknown."); }
            catch (Exception error)
            { logger.LogDebug("Direct SDK operation failed ({Reason}).", error.GetType().Name); await ErrorAsync(call.Id, 500, "SDK operation failed."); }
            finally
            {
                call.Cancel();
                try { await call.DisposeUploadAsync(); }
                finally { calls.TryRemove(call.Id, out _); call.Stop.Dispose(); call.Done.TrySetResult(); }
            }
        }

        private async Task StartCircuitAsync(Call call, JsonElement request)
        {
            if (Interlocked.CompareExchange(ref circuit, 1, 0) != 0) throw new QuasarOperationException(429, "This peer already carries a circuit.");
            try
            {
                call.BeginUpload(long.MaxValue);
                await using var output = Output(call);
                using var socket = new QuasarRtcCircuitSocket(call.UploadPipe!, output, output.EndAsync,
                    (kind, bytes) => SendControlAsync(new { v = 1, type = "circuit.message", id = call.Id, kind, bytes }, call.Token), call.Cancel, peer.Cancel, call.Token);
                var service = scopes.CreateScope();
                using (service)
                {
                    var application = service.ServiceProvider.GetRequiredService<QuasarRtcCircuits>().RunAsync(peer.Binding, Text(request, "ticket"), socket, call.Token);
                    try
                    {
                        var ready = socket.Accepted.Task.WaitAsync(TimeSpan.FromSeconds(10), call.Token);
                        if (await Task.WhenAny(ready, application) == application) await application;
                        await ready;
                        await SendControlAsync(new { v = 1, type = "stream", id = call.Id, contentType = "application/octet-stream", length = (long?)null, status = 200 }, call.Token);
                        await SendControlAsync(new { v = 1, type = "upload", id = call.Id, window = 64 * 1024 }, call.Token);
                        await application;
                        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, null, call.Token);
                    }
                    finally { call.Cancel(); try { await application; } catch (OperationCanceledException) when (call.Token.IsCancellationRequested) { } }
                }
            }
            finally { Volatile.Write(ref circuit, 0); }
        }

        private async Task StartResourceAsync(Call call, JsonElement request)
        {
            var id = Text(request, "resource");
            if (!owned.TryGetValue(id, out var reserved)) throw new QuasarOperationException(404, "This peer does not own the SDK resource.");
            var transfer = transfers.Get(peer.Binding, id, claim: true);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(call.Token, transfer.Token);
            using var cancel = stop.Token.Register(call.Cancel);
            await using var scope = scopes.CreateAsyncScope();
            var operations = scope.ServiceProvider.GetRequiredService<QuasarOperationService>();
            Func<CancellationToken, Task>? feature = null;
            async Task Check(CancellationToken token)
            {
                sessions.Verify(peer.Binding, token);
                var denied = await operations.CheckAccessAsync(peer.Binding.User, transfer.Plugin, transfer.Operation, transfer.Kind);
                if (denied is not null) throw new QuasarOperationException(denied.StatusCode, denied.Error!);
                if (feature is not null) await feature(token);
            }
            var watch = QuasarResourceLifetime.WatchAuthorizationAsync(Check, stop);
            try
            {
                await Check(stop.Token);
                var context = new QuasarOperationContext(peer.Binding.User, scope.ServiceProvider) { Kind = transfer.Kind, VerifyAccess = Check };
                if (transfer.Kind == QuasarOperationKind.Upload)
                {
                    var maximum = operations.Find(transfer.Plugin, transfer.Operation)!.MaximumUploadBytes;
                    call.BeginUpload(maximum);
                    await SendControlAsync(new { v = 1, type = "upload", id = call.Id, window = 64 * 1024 }, stop.Token);
                    await using var staged = await QuasarUploadStaging.StageAsync(call.UploadReader!, maximum, stop.Token);
                    await Check(stop.Token);
                    var result = await operations.DispatchAsync(context with { Upload = staged }, transfer.Plugin, transfer.Operation, transfer.Request, stop.Token);
                    await ResultAsync(call, result, scope.ServiceProvider);
                    return;
                }
                var response = await operations.DispatchAsync(context, transfer.Plugin, transfer.Operation, transfer.Request, stop.Token);
                if (response.Error is not null) throw new QuasarOperationException(response.StatusCode, response.Error);
                await using var output = Output(call);
                if (response.Value is QuasarRead read)
                {
                    feature = read.ValidateAsync;
                    await Check(stop.Token);
                    await using var source = await read.OpenAsync(stop.Token);
                    var length = read.Length ?? (source.CanSeek ? source.Length : (long?)null);
                    long from = 0; long? remaining = length;
                    var status = 200;
                    string? contentRange = null;
                    if (request.TryGetProperty("range", out var requested) && requested.ValueKind != JsonValueKind.Null)
                    {
                        if (!read.EnableRanges || !source.CanSeek || length is null || requested.ValueKind != JsonValueKind.String
                            || !RangeHeaderValue.TryParse(requested.GetString(), out var ranges) || ranges.Unit != "bytes" || ranges.Ranges.Count != 1)
                            throw new QuasarOperationException(416, "This SDK resource does not support the requested range.");
                        var range = ranges.Ranges.Single();
                        from = range.From ?? Math.Max(0, length.Value - (range.To ?? 0));
                        var last = range.From is null ? length.Value - 1 : Math.Min(range.To ?? length.Value - 1, length.Value - 1);
                        if (from < 0 || from >= length || last < from) throw new QuasarOperationException(416, "SDK range is outside the resource.");
                        remaining = last - from + 1;
                        contentRange = $"bytes {from}-{last}/{length}";
                        status = 206; source.Seek(from, SeekOrigin.Begin);
                    }
                    await SendControlAsync(new { v = 1, type = "stream", id = call.Id, contentType = read.ContentType, fileName = read.FileName,
                        length = remaining, contentRange, status, entityTag = read.EntityTag, lastModified = read.LastModified }, stop.Token);
                    var buffer = new byte[QuasarSdkLimits.TransferBufferBytes];
                    while (remaining is null || remaining > 0)
                    {
                        await Check(stop.Token);
                        var count = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining ?? buffer.Length)), stop.Token);
                        if (count == 0) { if (remaining is > 0) throw new IOException("SDK resource ended early."); break; }
                        await output.WriteAsync(buffer.AsMemory(0, count), stop.Token);
                        if (remaining is not null) remaining -= count;
                    }
                }
                else if (response.Value is QuasarSubscription subscription)
                {
                    await SendControlAsync(new { v = 1, type = "stream", id = call.Id, contentType = "application/x-ndjson", length = (long?)null, status = 200 }, stop.Token);
                    await foreach (var item in subscription.Items.WithCancellation(stop.Token))
                    {
                        await Check(stop.Token);
                        var bytes = await QuasarSdkSerialization.BoundedAsync(item, QuasarSdkLimits.MaximumJsonBytes, QuasarSdkSerialization.Items, stop.Token);
                        await output.WriteAsync(bytes, stop.Token);
                        await output.WriteAsync("\n"u8.ToArray(), stop.Token);
                    }
                }
                else throw new InvalidOperationException("Invalid SDK resource result.");
                await output.EndAsync();
            }
            finally
            {
                stop.Cancel();
                try { await watch; }
                finally { owned.TryRemove(id, out _); transfers.Remove(transfer); }
            }
        }

        private QuasarRtcWire.Output Output(Call call) => new(call.Id, call.Credit, SendBulkAsync, SendControlAsync, call.Token);

        private async Task ResultAsync(Call call, QuasarOperationResult response, IServiceProvider services)
        {
            if (response.Error is not null) throw new QuasarOperationException(response.StatusCode, response.Error);
            await using var output = Output(call);
            await using var json = new QuasarRtcWire.JsonOutput(call.Id, output, SendControlAsync, call.Token);
            var options = services.GetRequiredService<IOptions<Microsoft.AspNetCore.Http.Json.JsonOptions>>().Value.SerializerOptions;
            await JsonSerializer.SerializeAsync(json, response.Value, options, call.Token);
            await json.CompleteAsync();
        }

        private async Task ErrorAsync(uint id, int status, string detail)
        {
            try { await SendControlAsync(new { v = 1, type = "error", id, status, detail }, peer.Token); }
            catch (Exception error) when (error is OperationCanceledException or WebSocketException or IOException or QuasarOperationException)
            { peer.Cancel(); }
        }

        private async Task SendControlAsync(object value, CancellationToken token)
        {
            var bytes = await QuasarSdkSerialization.BoundedAsync(value, QuasarRtcWire.ControlBytes, QuasarRtcWire.Json, token);
            await controlWrite.WaitAsync(token);
            try { await control.SendAsync(bytes, WebSocketMessageType.Text, true, token); }
            finally { controlWrite.Release(); }
        }
        private async Task SendBulkAsync(ReadOnlyMemory<byte> bytes, CancellationToken token)
        {
            await bulkWrite.WaitAsync(token);
            try { await bulk.SendAsync(bytes, WebSocketMessageType.Binary, true, token); }
            finally { bulkWrite.Release(); }
        }

        private static string Identifier(JsonElement request, string name)
        {
            var value = Text(request, name);
            return QuasarOperation.IsIdentifier(value) ? value : throw new QuasarOperationException(400, "Invalid SDK operation identifier.");
        }
        private static string Text(JsonElement request, string name) => request.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && value.GetString() is { Length: <= 256 } text ? text : throw new QuasarOperationException(400, "Invalid SDK request field.");
        private static long Integer(JsonElement request, string name) => request.TryGetProperty(name, out var value) && value.TryGetInt64(out var result)
            && result is >= 0 and <= 9_007_199_254_740_991 ? result : throw new QuasarOperationException(400, "Invalid SDK byte count.");
    }

    private sealed class Call
    {
        public Call(uint id, CancellationToken peer, bool persistent = false)
        {
            Id = id; Stop = CancellationTokenSource.CreateLinkedTokenSource(peer); Token = Stop.Token;
            if (!persistent) Stop.CancelAfter(TimeSpan.FromMinutes(10));
        }
        public uint Id { get; }
        public CancellationTokenSource Stop { get; }
        public CancellationToken Token { get; }
        public void Cancel() { try { Stop.Cancel(); } catch (ObjectDisposedException) { } }
        public QuasarRtcWire.Credit Credit { get; } = new();
        public TaskCompletionSource Done { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private readonly object gate = new();
        private readonly SemaphoreSlim writer = new(1, 1);
        private Pipe? upload;
        private long maximum;
        private long? expected;
        private uint sequence;
        private bool completed;
        private Task completion = Task.CompletedTask;
        public long UploadReceived { get; private set; }
        public Stream? UploadReader { get; private set; }
        public PipeReader? UploadPipe => upload?.Reader;
        public void BeginUpload(long limit)
        {
            lock (gate)
            {
                maximum = limit;
                upload = new Pipe(new PipeOptions(pauseWriterThreshold: QuasarRtcWire.WindowBytes, resumeWriterThreshold: QuasarRtcWire.WindowBytes / 2, useSynchronizationContext: false));
                UploadReader = upload.Reader.AsStream(leaveOpen: true);
            }
        }
        public async Task WriteUploadAsync(uint frame, ReadOnlyMemory<byte> bytes)
        {
            await writer.WaitAsync(Token);
            try
            {
            Pipe pipe;
            lock (gate)
            {
                pipe = upload ?? throw new QuasarOperationException(400, "This SDK request does not accept file bytes.");
                if (completed || frame != sequence || bytes.Length > maximum - UploadReceived || expected is { } end && UploadReceived + bytes.Length > end)
                    throw new QuasarOperationException(413, "Invalid or oversized SDK upload.");
                sequence = checked(sequence + 1);
            }
            await pipe.Writer.WriteAsync(bytes, Token);
            lock (gate)
            {
                UploadReceived += bytes.Length;
                if (expected is { } end && UploadReceived > end) throw new QuasarOperationException(400, "SDK upload exceeded its declared completion.");
            }
            await CompleteUploadIfReadyAsync();
            }
            finally { writer.Release(); }
        }
        public void FinishUpload(long bytes)
        {
            lock (gate)
            {
                if (bytes > maximum) throw new QuasarOperationException(413, "SDK upload exceeds its registered limit.");
                if (upload is null || expected is not null || bytes < UploadReceived)
                    throw new QuasarOperationException(400, "Invalid SDK upload completion.");
                expected = bytes;
            }
            // EOF can overtake the last binary frames on the separate control channel.
            // Do not block that channel on a stalled upload writer.
            completion = FinishWhenReadyAsync();
        }
        private async Task FinishWhenReadyAsync()
        {
            try
            {
                await writer.WaitAsync(Token);
                try { await CompleteUploadIfReadyAsync(); }
                finally { writer.Release(); }
            }
            catch { Cancel(); }
        }
        private async Task CompleteUploadIfReadyAsync()
        {
            Pipe? finishing = null;
            lock (gate) if (!completed && expected == UploadReceived) { completed = true; finishing = upload; }
            if (finishing is not null) await finishing.Writer.CompleteAsync();
        }
        public async Task DisposeUploadAsync()
        {
            await completion;
            await writer.WaitAsync();
            try
            {
            if (upload is not null)
            {
                await upload.Writer.CompleteAsync();
                await upload.Reader.CompleteAsync();
                if (UploadReader is not null) await UploadReader.DisposeAsync();
            }
            }
            finally { writer.Release(); }
        }
    }
}
