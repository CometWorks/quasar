using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Quasar.Models;
using Quasar.Services.PluginSdk;

namespace Quasar.Services;

public sealed class AgentSocketHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AgentRegistry _registry;
    private readonly PluginConfigService _pluginConfigService;
    private readonly DedicatedServerSupervisor _supervisor;
    private readonly PluginLogStream _pluginLogStream;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AgentSocketHandler> _logger;

    public AgentSocketHandler(
        AgentRegistry registry,
        PluginConfigService pluginConfigService,
        DedicatedServerSupervisor supervisor,
        PluginLogStream pluginLogStream,
        IHostApplicationLifetime lifetime,
        ILogger<AgentSocketHandler> logger)
    {
        _registry = registry;
        _pluginConfigService = pluginConfigService;
        _supervisor = supervisor;
        _pluginLogStream = pluginLogStream;
        _lifetime = lifetime;
        _logger = logger;
    }

    public async Task HandleAsync(HttpContext context)
    {
        if (!context.WebSockets.IsWebSocketRequest)
        {
            context.Response.StatusCode = StatusCodes.Status400BadRequest;
            await context.Response.WriteAsync("WebSocket request required.");
            return;
        }

        using var socket = await context.WebSockets.AcceptWebSocketAsync("quasar.agent.v1");
        var connectionId = Guid.NewGuid().ToString("N");

        _logger.LogInformation("Agent socket connected: {ConnectionId}", connectionId);

        // End the blocking read loop promptly when the host starts shutting down.
        // RequestAborted alone is not tripped until the host's shutdown timeout, so
        // the in-flight ReceiveAsync would otherwise stall graceful shutdown for ~30s.
        using var loopCts = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted, _lifetime.ApplicationStopping);
        var loopToken = loopCts.Token;

        try
        {
            while (socket.State == WebSocketState.Open && !loopToken.IsCancellationRequested)
            {
                var message = await ReceiveAsync(socket, loopToken);
                if (message is null)
                    break;

                await ProcessMessageAsync(message, connectionId, socket, loopToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (WebSocketException exception)
        {
            _logger.LogWarning(exception, "Agent socket closed with transport error: {ConnectionId}", connectionId);
        }
        finally
        {
            _registry.MarkDisconnected(connectionId);
            _logger.LogInformation("Agent socket disconnected: {ConnectionId}", connectionId);

            if (socket.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "closing", CancellationToken.None);
        }
    }

    // NOTE on cancellation: `cancellationToken` here is the socket's
    // RequestAborted. It is correct for reads and replies (they are meaningless
    // once the agent is gone), but must NOT gate persistent state mutations: an
    // agent disconnects the instant it finishes sending (its process is exiting),
    // which would cancel the write mid-flight and silently drop the change.
    // Handlers that mutate supervisor/catalog state use `_lifetime.ApplicationStopping`
    // instead, so the change completes regardless of the socket.
    private async Task ProcessMessageAsync(
        AgentWireMessage message,
        string connectionId,
        WebSocket socket,
        CancellationToken cancellationToken)
    {
        switch (message.Kind)
        {
            case WireMessageKind.Hello when message.Hello is not null:
                _registry.UpsertHello(message.Hello, connectionId, (wireMessage, token) => SendAsync(socket, wireMessage, token));
                break;

            case WireMessageKind.Snapshot when message.Snapshot is not null:
                _registry.UpdateSnapshot(message.Snapshot, connectionId);
                break;

            case WireMessageKind.CommandResult when message.CommandResult is not null:
                _registry.UpdateCommandResult(message.CommandResult);
                break;

            case WireMessageKind.PluginConfigSnapshot when message.PluginConfigSnapshot is not null:
                _pluginConfigService.IngestSnapshot(message.PluginConfigSnapshot);
                break;

            case WireMessageKind.PluginLogs when message.PluginLogs is not null:
                IngestPluginLogs(message.PluginLogs, connectionId);
                break;

            case WireMessageKind.AdminStop:
                if (_registry.TryGetUniqueName(connectionId, out var stoppedUniqueName))
                {
                    _logger.LogInformation(
                        "Admin stopped server {UniqueName} in-game; setting goal state to Off.",
                        stoppedUniqueName);

                    // State mutations use the app-lifetime token, NOT the
                    // request-aborted one (see note in ProcessMessageAsync): the
                    // agent closes this socket the instant it has sent the signal
                    // (its process is shutting down), which would otherwise cancel
                    // the goal-state write mid-flight and let the exit be treated
                    // as a crash and restarted. This intent must persist
                    // regardless of the socket.
                    await _supervisor.SetGoalStateAsync(
                        stoppedUniqueName,
                        DedicatedServerGoalState.Off,
                        _lifetime.ApplicationStopping);
                }
                else
                {
                    _logger.LogWarning(
                        "Received admin-stop signal for unknown connection {ConnectionId}.",
                        connectionId);
                }

                break;

            case WireMessageKind.Ping:
                await SendAsync(socket, new AgentWireMessage
                {
                    Kind = WireMessageKind.Pong,
                    Message = "pong",
                }, cancellationToken);
                break;

            default:
                _logger.LogDebug("Ignoring unsupported wire message kind '{Kind}'.", message.Kind);
                break;
        }
    }

    // Plugin log lines arrive over the agent channel (rather than via the
    // supervisor's stdout capture) so they keep flowing after Quasar restarts and
    // reconnects to a detached server daemon. The unique name is resolved from the
    // connection's Hello, the same way other agent messages are routed; each line
    // is parsed by the shared sink-line parser and appended to the live buffer.
    private void IngestPluginLogs(PluginLogBatch batch, string connectionId)
    {
        if (!_registry.TryGetUniqueName(connectionId, out var uniqueName))
        {
            _logger.LogDebug("Received plugin logs for unknown connection {ConnectionId}.", connectionId);
            return;
        }

        foreach (var line in batch.Lines)
        {
            if (PluginLogStream.TryParseSinkLine(uniqueName, line, out var entry) && entry is not null)
                _pluginLogStream.Append(entry);
        }
    }

    private static async Task<AgentWireMessage?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            stream.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
                break;
        }

        var json = Encoding.UTF8.GetString(stream.ToArray());
        return JsonSerializer.Deserialize<AgentWireMessage>(json, JsonOptions);
    }

    private static async Task SendAsync(WebSocket socket, AgentWireMessage message, CancellationToken cancellationToken)
    {
        var json = JsonSerializer.Serialize(message, JsonOptions);
        var payload = Encoding.UTF8.GetBytes(json);

        await socket.SendAsync(
            new ArraySegment<byte>(payload),
            WebSocketMessageType.Text,
            endOfMessage: true,
            cancellationToken);
    }
}
