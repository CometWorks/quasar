using System.Text.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Net.Http.Headers;
using Quasar.Plugin.Abstractions.Operations;

namespace Quasar.Services.Plugins;

public sealed class QuasarHttpOperations(QuasarOperationService operations, QuasarTransferRegistry transfers,
    IAntiforgery antiforgery, QuasarBrowserSessions sessions) : IQuasarHttpOperations
{
    private QuasarOperationService Operations => operations;
    private QuasarTransferRegistry Transfers => transfers;
    private QuasarBrowserSessions Sessions => sessions;
    public async Task<IResult> ExecuteAsync(HttpContext http, string pluginId, string operationId, JsonElement? request = null)
    {
        http.Response.Headers.CacheControl = "private, no-store";
        try
        {
            var operation = operations.Find(pluginId, operationId) ?? throw new QuasarOperationException(404, "SDK operation not found.");
            var denied = await operations.CheckAccessAsync(http.User, pluginId, operationId, operation.Kind);
            if (denied is not null) return denied.ToHttpResult();
            if (!HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method))
                await antiforgery.ValidateRequestAsync(http);
            using var body = request is null && !HttpMethods.IsGet(http.Request.Method) && !HttpMethods.IsHead(http.Request.Method)
                ? await QuasarSdkEndpoints.ReadJsonAsync(http) : null;
            if (request is null && !HttpMethods.IsGet(http.Request.Method) && body is null)
                return Results.Problem(statusCode: 413, detail: "SDK control requests are limited to 64 KiB.");
            var input = request ?? body?.RootElement ?? JsonSerializer.SerializeToElement(new { });
            if (operation.Kind == QuasarOperationKind.Json)
                return (await operations.DispatchAsync(new(http.User, http.RequestServices), pluginId, operationId, input, http.RequestAborted)).ToHttpResult();
            if (operation.Kind == QuasarOperationKind.Upload) throw new QuasarOperationException(400, "Use SDK upload preparation.");
            var transfer = transfers.Create(await sessions.BindHttpSessionAsync(http), pluginId, operationId, operation.Kind, input);
            return new TransferResult(this, transfer, claim: true);
        }
        catch (QuasarOperationException error) { return Results.Problem(statusCode: error.StatusCode, detail: error.Message); }
        catch (AntiforgeryValidationException) { return Results.Problem(statusCode: 400, detail: "Invalid antiforgery token."); }
        catch (JsonException) { return Results.Problem(statusCode: 400, detail: "Invalid operation JSON."); }
    }

    public IResult Result(QuasarTransferRegistry.Transfer transfer) => new TransferResult(this, transfer, claim: false);

    private sealed class TransferResult(QuasarHttpOperations owner, QuasarTransferRegistry.Transfer transfer, bool claim) : IResult
    {
        public async Task ExecuteAsync(HttpContext http)
        {
            if (claim)
            {
                try { owner.Transfers.Get(await owner.Sessions.BindHttpSessionAsync(http), transfer.Id, claim: true); }
                catch (QuasarOperationException error)
                {
                    owner.Transfers.Remove(transfer);
                    await Results.Problem(statusCode: error.StatusCode, detail: error.Message).ExecuteAsync(http);
                    return;
                }
            }
            http.Response.Headers.CacheControl = "private, no-store";
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(transfer.Token, http.RequestAborted);
            Func<CancellationToken, Task>? featureCheck = null;
            async Task Check(CancellationToken token)
            {
                token.ThrowIfCancellationRequested();
                owner.Sessions.Verify(transfer.Binding, token);
                var denied = await owner.Operations.CheckAccessAsync(http.User, transfer.Plugin, transfer.Operation, transfer.Kind);
                if (denied is not null) throw new QuasarOperationException(denied.StatusCode, denied.Error!);
                if (featureCheck is not null) await featureCheck(token);
            }
            var ticket = await http.AuthenticateAsync();
            if (ticket.Properties?.ExpiresUtc is { } expiry)
                stop.CancelAfter(expiry <= DateTimeOffset.UtcNow ? TimeSpan.Zero : expiry - DateTimeOffset.UtcNow);
            using var abort = stop.Token.Register(http.Abort);
            var monitor = QuasarResourceLifetime.WatchAuthorizationAsync(Check, stop);
            try
            {
                await Check(stop.Token);
                var context = new QuasarOperationContext(http.User, http.RequestServices) { Kind = transfer.Kind, VerifyAccess = Check };
                if (transfer.Kind == QuasarOperationKind.Upload)
                {
                    var maximum = owner.Operations.Find(transfer.Plugin, transfer.Operation)!.MaximumUploadBytes;
                    await using var staged = await QuasarUploadStaging.StageAsync(http.Request.Body, maximum, stop.Token);
                    await Check(stop.Token);
                    staged.Position = 0;
                    var result = await owner.Operations.DispatchAsync(context with { Upload = staged }, transfer.Plugin, transfer.Operation, transfer.Request, stop.Token);
                    await result.ToHttpResult().ExecuteAsync(http);
                    return;
                }
                var response = await owner.Operations.DispatchAsync(context, transfer.Plugin, transfer.Operation, transfer.Request, stop.Token);
                if (response.Error is not null) { await response.ToHttpResult().ExecuteAsync(http); return; }
                if (response.Value is QuasarRead read)
                {
                    featureCheck = read.ValidateAsync;
                    await Check(stop.Token);
                    if (!read.EnableRanges && http.Request.Headers.ContainsKey("Range"))
                        throw new QuasarOperationException(416, "This resource does not support ranges.");
                    await using var source = await read.OpenAsync(stop.Token);
                    await using var guarded = new GuardedReadStream(source, Check, stop.Token);
                    if (!source.CanSeek && read.Length is { } length) http.Response.ContentLength = length;
                    var tag = read.EntityTag is null ? null : EntityTagHeaderValue.Parse(read.EntityTag);
                    await Results.Stream(guarded, read.ContentType, read.FileName, read.LastModified, tag, read.EnableRanges).ExecuteAsync(http);
                }
                else if (response.Value is QuasarSubscription subscription)
                {
                    http.Response.ContentType = "application/x-ndjson";
                    await foreach (var item in subscription.Items.WithCancellation(stop.Token))
                    {
                        await Check(stop.Token);
                        using var buffer = new MemoryStream();
                        await using (var bounded = new LimitedWriteStream(buffer, QuasarSdkEndpoints.MaximumJsonBytes))
                            await JsonSerializer.SerializeAsync(bounded, item, new JsonSerializerOptions(JsonSerializerDefaults.Web) { MaxDepth = QuasarSdkLimits.MaximumJsonDepth }, stop.Token);
                        await http.Response.Body.WriteAsync(buffer.GetBuffer().AsMemory(0, (int)buffer.Length), stop.Token);
                        await http.Response.Body.WriteAsync("\n"u8.ToArray(), stop.Token);
                        await http.Response.Body.FlushAsync(stop.Token);
                    }
                }
                else throw new InvalidOperationException("The registered SDK resource returned the wrong result type.");
            }
            catch (QuasarOperationException error)
            {
                if (!http.Response.HasStarted) await Results.Problem(statusCode: error.StatusCode, detail: error.Message).ExecuteAsync(http);
                else http.Abort();
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { http.Abort(); }
            finally
            {
                abort.Dispose();
                stop.Cancel();
                try { await monitor; }
                finally { owner.Transfers.Remove(transfer); }
            }
        }
    }

    private sealed class GuardedReadStream(Stream inner, Func<CancellationToken, Task> check, CancellationToken lifetime) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override int Read(byte[] buffer, int offset, int count) { lifetime.ThrowIfCancellationRequested(); return inner.Read(buffer, offset, count); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { await check(lifetime); return await inner.ReadAsync(buffer, lifetime); }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override void Flush() => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private sealed class LimitedWriteStream(Stream inner, int limit) : Stream
    {
        private int _written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _written;
        public override long Position { get => _written; set => throw new NotSupportedException(); }
        public override void Write(byte[] buffer, int offset, int count)
        { if (_written + count > limit) throw new QuasarOperationException(413, "Subscription item exceeds 64 KiB."); inner.Write(buffer, offset, count); _written += count; }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        { Write(buffer.ToArray(), 0, buffer.Length); return ValueTask.CompletedTask; }
        public override void Flush() => inner.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
