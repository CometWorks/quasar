using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using Quasar.ClusterDeployment;

namespace Quasar.Host;

internal sealed record HostConnectionConfig(string QuasarUrl, string TokenEnvironmentVariable);

internal static class HostConnection
{
    internal static async Task RunAsync(HostExecutorConfig config, CancellationToken token)
    {
        var connection = config.Connection!;
        var origin = new Uri(connection.QuasarUrl);
        if (config.Command is null || origin.UserInfo.Length != 0 || origin.Query.Length != 0 || origin.Fragment.Length != 0
            || origin.AbsolutePath != "/" || origin.Scheme != "https" && !(origin.Scheme == "http" && origin.IsLoopback))
            throw new InvalidDataException("Remote enrollment requires an HTTPS Quasar URL.");
        string credential = Environment.GetEnvironmentVariable(connection.TokenEnvironmentVariable)
            ?? throw new InvalidDataException("Enrollment credential is unavailable.");
        var endpoint = new Uri(config.Command!.Url);
        var tunnels = new List<Task>();
        try
        {
        while (!token.IsCancellationRequested)
        {
            try
            {
                using var control = CreateSocket(credential, control: true);
                await control.ConnectAsync(Url(origin, config.HostId, "connect"), token);
                byte[] bytes = new byte[64];
                while (control.State == WebSocketState.Open)
                {
                    var message = await control.ReceiveAsync(bytes.AsMemory(), token);
                    if (message.MessageType == WebSocketMessageType.Close) break;
                    if (!message.EndOfMessage || message.MessageType != WebSocketMessageType.Text
                        || !Guid.TryParseExact(Encoding.UTF8.GetString(bytes, 0, message.Count), "N", out var id))
                        throw new IOException("Invalid Host tunnel request.");
                    tunnels.RemoveAll(t => t.IsCompleted);
                    if (tunnels.Count >= 16) throw new IOException("Too many Host tunnels.");
                    tunnels.Add(BridgeAsync(origin, config.HostId, id, credential, endpoint.Port, token));
                }
            }
            catch (Exception error) when (!token.IsCancellationRequested && error is WebSocketException or HttpRequestException or IOException)
            { Console.Error.WriteLine($"Quasar connection interrupted ({error.GetType().Name}); retrying."); }
            if (!token.IsCancellationRequested) await Task.Delay(TimeSpan.FromSeconds(5), token);
        }
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
        finally { await Task.WhenAll(tunnels); }
    }

    private static async Task BridgeAsync(Uri origin, string host, Guid id, string credential, int port, CancellationToken token)
    {
        try
        {
            using var socket = CreateSocket(credential);
            using var tcp = new TcpClient();
            await tcp.ConnectAsync("127.0.0.1", port, token);
            await socket.ConnectAsync(Url(origin, host, "tunnels/" + id.ToString("N")), token);
            using var tunnel = new WebSocketTunnelStream(socket);
            using var stop = CancellationTokenSource.CreateLinkedTokenSource(token);
            var send = tcp.GetStream().CopyToAsync(tunnel, stop.Token);
            var receive = tunnel.CopyToAsync(tcp.GetStream(), stop.Token);
            await Task.WhenAny(send, receive);
            await stop.CancelAsync();
            try { await Task.WhenAll(send, receive); } catch (OperationCanceledException) { }
        }
        catch (Exception error) when (error is IOException or WebSocketException or SocketException or OperationCanceledException)
        { /* The HTTP caller observes a closed connection and can retry its idempotent operation. */ }
    }

    private static ClientWebSocket CreateSocket(string token, bool control = false)
    {
        var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer " + token);
        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
        if (control) socket.Options.KeepAliveTimeout = TimeSpan.FromSeconds(20);
        return socket;
    }
    private static Uri Url(Uri origin, string host, string route) => new UriBuilder(new Uri(origin,
        "/api/v1/hosts/" + Uri.EscapeDataString(host) + "/" + route)) { Scheme = origin.Scheme == "https" ? "wss" : "ws" }.Uri;
}
