using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Quasar.ClusterDeployment;

namespace Quasar.Services;

public sealed class ClusterHostTunnels
{
    // A WebSocket keeps Kestrel's graceful shutdown waiting (up to the 30 minute ShutdownTimeout) and
    // RequestAborted does not fire for it, so every Host channel also ends when the application stops.
    private readonly CancellationToken _stopping;
    public ClusterHostTunnels(IHostApplicationLifetime lifetime) => _stopping = lifetime.ApplicationStopping;
    internal ClusterHostTunnels() { }
    private sealed record Connection(WebSocket Socket, SemaphoreSlim Send);
    private sealed record Pending(string Host, Connection Connection, TaskCompletionSource<WebSocket> Socket, TaskCompletionSource Closed);
    private readonly ConcurrentDictionary<string, Connection> _hosts = new();
    private readonly ConcurrentDictionary<Guid, Pending> _pending = new();
    public bool IsConnected(string host) => _hosts.TryGetValue(host, out var connection) && connection.Socket.State == WebSocketState.Open;

    internal async Task ConnectAsync(string host, HttpContext context)
    {
        using var socket = await context.WebSockets.AcceptWebSocketAsync(new WebSocketAcceptContext
            { KeepAliveInterval = TimeSpan.FromSeconds(20), KeepAliveTimeout = TimeSpan.FromSeconds(20) });
        var connection = new Connection(socket, new(1, 1));
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stopping);
        if (!_hosts.TryAdd(host, connection)) { await socket.CloseAsync(WebSocketCloseStatus.PolicyViolation, "Host already connected", context.RequestAborted); return; }
        try
        {
            byte[] data = new byte[1];
            while (socket.State == WebSocketState.Open)
            {
                var frame = await socket.ReceiveAsync(data.AsMemory(), lifetime.Token);
                if (frame.MessageType == WebSocketMessageType.Close) break;
                throw new IOException("Host control channel does not accept data.");
            }
        }
        catch (Exception error) when (error is IOException or WebSocketException or OperationCanceledException) { }
        finally
        {
            _hosts.TryRemove(new KeyValuePair<string, Connection>(host, connection));
            foreach (var pair in _pending.Where(p => p.Value.Connection == connection))
            {
                _pending.TryRemove(pair.Key, out _);
                if (!pair.Value.Socket.TrySetException(new IOException("Host disconnected.")) && pair.Value.Socket.Task.IsCompletedSuccessfully)
                    pair.Value.Socket.Task.Result.Abort();
                pair.Value.Closed.TrySetResult();
            }
        }
    }

    internal async Task AcceptTunnelAsync(string host, Guid id, HttpContext context)
    {
        if (!_pending.TryGetValue(id, out var pending) || pending.Host != host)
        { context.Response.StatusCode = 404; return; }
        using var socket = await context.WebSockets.AcceptWebSocketAsync();
        if (!pending.Socket.TrySetResult(socket)) return;
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted, _stopping);
        try { await pending.Closed.Task.WaitAsync(lifetime.Token); }
        catch (OperationCanceledException) { }
    }

    internal async ValueTask<Stream> OpenAsync(SocketsHttpConnectionContext context, CancellationToken token)
    {
        const string suffix = ".quasar-host.invalid";
        if (!context.DnsEndPoint.Host.EndsWith(suffix, StringComparison.Ordinal))
        {
            var tcp = new Socket(SocketType.Stream, ProtocolType.Tcp);
            try { await tcp.ConnectAsync(context.DnsEndPoint, token); return new NetworkStream(tcp, ownsSocket: true); }
            catch { tcp.Dispose(); throw; }
        }
        string host = context.DnsEndPoint.Host[..^suffix.Length];
        if (!_hosts.TryGetValue(host, out var connection)) throw new IOException("Enrolled Host is offline.");
        var id = Guid.NewGuid();
        var pending = new Pending(host, connection, new(TaskCreationOptions.RunContinuationsAsynchronously), new(TaskCreationOptions.RunContinuationsAsynchronously));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            await connection.Send.WaitAsync(deadline.Token);
            try
            {
                if (_pending.Values.Count(p => p.Connection == connection) >= 16) throw new IOException("Host tunnel limit reached.");
                _pending[id] = pending;
                await connection.Socket.SendAsync(Encoding.UTF8.GetBytes(id.ToString("N")), WebSocketMessageType.Text, true, deadline.Token);
            }
            finally { connection.Send.Release(); }
            var socket = await pending.Socket.Task.WaitAsync(deadline.Token);
            return new LeaseStream(socket, () => { _pending.TryRemove(id, out _); pending.Closed.TrySetResult(); });
        }
        catch { _pending.TryRemove(id, out _); pending.Closed.TrySetResult(); throw; }
    }
    private sealed class LeaseStream(WebSocket socket, Action closed) : WebSocketTunnelStream(socket)
    {
        protected override void Dispose(bool disposing) { base.Dispose(disposing); if (disposing) closed(); }
    }
}
