using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Quasar.Host.Contract.V1;
using Quasar.Models;
using Quasar.Services.PluginSdk;

namespace Quasar.Services;

public sealed class AgentSocketHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    // An agent sends its Hello right after connecting and a snapshot every two seconds.
    private static readonly TimeSpan HelloTimeout = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan StaleConnectionAge = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan MissingTokenLogInterval = TimeSpan.FromMinutes(10);

    private readonly AgentRegistry _registry;
    private readonly AgentSocketAuthenticator _authenticator;
    private readonly Dictionary<string, DateTimeOffset> _missingTokenLogged = new();
    private readonly PluginConfigService _pluginConfigService;
    private readonly DedicatedServerSupervisor _supervisor;
    private readonly PluginLogStream _pluginLogStream;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<AgentSocketHandler> _logger;

    public AgentSocketHandler(
        AgentRegistry registry,
        AgentSocketAuthenticator authenticator,
        PluginConfigService pluginConfigService,
        DedicatedServerSupervisor supervisor,
        PluginLogStream pluginLogStream,
        IHostApplicationLifetime lifetime,
        ILogger<AgentSocketHandler> logger)
    {
        _registry = registry;
        _authenticator = authenticator;
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

        var token = context.Request.Headers[AgentSocketToken.HeaderName].ToString();
        if (token.Length == 0)
        {
            LogMissingToken(context.Connection.RemoteIpAddress?.ToString() ?? "unknown");
            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
            await context.Response.WriteAsync(
                "Agent token required. Restart the server from Quasar so it loads the current Quasar.Agent.");
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
            string? refusal;
            using (var helloTimeout = CancellationTokenSource.CreateLinkedTokenSource(loopToken))
            {
                helloTimeout.CancelAfter(HelloTimeout);
                refusal = await AuthenticateAsync(socket, token, connectionId, context, helloTimeout.Token);
            }

            if (refusal is not null)
            {
                // Don't wait for the peer's close frame: a refused client gets no more of our time.
                await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, refusal, CancellationToken.None);
                return;
            }

            while (socket.State == WebSocketState.Open && !loopToken.IsCancellationRequested)
            {
                var message = await ReceiveAsync(socket, loopToken);
                if (message is null)
                    break;

                if (message.Kind != WireMessageKind.Hello)
                    await ProcessMessageAsync(message, connectionId, socket, loopToken);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (AgentMessageException exception)
        {
            // These reasons are constructed by ReceiveAsync from protocol metadata, never payloads.
            _logger.LogWarning("Rejected agent message on connection {ConnectionId}: {Reason}",
                connectionId, exception.Message);
            await socket.CloseOutputAsync(exception.CloseStatus, exception.Message, CancellationToken.None);
        }
        catch (JsonException exception)
        {
            // JsonException.Message and Path can include peer-controlled data or credentials.
            _logger.LogWarning("Rejected malformed agent JSON on connection {ConnectionId} at line {Line}, byte {Byte}.",
                connectionId, exception.LineNumber, exception.BytePositionInLine);
            await socket.CloseOutputAsync(WebSocketCloseStatus.InvalidPayloadData, "Invalid agent JSON.", CancellationToken.None);
        }
        catch (InvalidDataException)
        {
            _logger.LogWarning("Rejected invalid agent data on connection {ConnectionId}.", connectionId);
            await socket.CloseOutputAsync(WebSocketCloseStatus.PolicyViolation, "Invalid agent message.", CancellationToken.None);
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

    // The first message must be a Hello whose identity the token was issued for. Returns the close
    // reason when the connection is refused, null when the agent is registered or the socket closed.
    private async Task<string?> AuthenticateAsync(
        WebSocket socket,
        string token,
        string connectionId,
        HttpContext context,
        CancellationToken cancellationToken)
    {
        var message = await ReceiveAsync(socket, cancellationToken, AgentMessageLimits.HelloBytes);
        if (message is null)
            return null;

        var remote = context.Connection.RemoteIpAddress?.ToString() ?? "unknown";
        if (message.Kind != WireMessageKind.Hello || message.Hello is not { } hello)
        {
            _logger.LogWarning("Refused agent connection {ConnectionId} from {Remote}: the first message was not a Hello.",
                connectionId, remote);
            return "Hello expected.";
        }

        if (!_authenticator.IsValid(hello, token))
        {
            _logger.LogWarning(
                "Refused agent connection {ConnectionId} from {Remote}: the agent token does not match server {UniqueName} (cluster: {ClusterMode}, host: {HostId}).",
                connectionId, remote, hello.UniqueName, hello.ClusterMode, hello.HostId);
            return "Agent token rejected.";
        }

        if (!_registry.TryUpsertHello(hello, connectionId, (wireMessage, ct) => SendAsync(socket, wireMessage, ct),
                DateTimeOffset.UtcNow - StaleConnectionAge))
        {
            _logger.LogWarning(
                "Refused agent connection {ConnectionId} from {Remote}: server {UniqueName} (agent {AgentId}) already has a connected agent.",
                connectionId, remote, hello.UniqueName, hello.AgentId);
            return "This server already has a connected agent.";
        }

        return null;
    }

    // Old agents retry every ten seconds, so log the refusal once per address and interval.
    private void LogMissingToken(string remote)
    {
        var now = DateTimeOffset.UtcNow;
        lock (_missingTokenLogged)
        {
            if (_missingTokenLogged.TryGetValue(remote, out var last) && now - last < MissingTokenLogInterval)
                return;
            if (_missingTokenLogged.Count >= 1024)
                _missingTokenLogged.Clear();
            _missingTokenLogged[remote] = now;
        }

        _logger.LogWarning(
            "Refused an agent connection from {Remote} without an agent token. A server started by an older Quasar runs an agent that cannot authenticate; restart it from Quasar to load the current agent.",
            remote);
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
            case WireMessageKind.Snapshot when message.Snapshot is not null:
                _registry.UpdateSnapshot(message.Snapshot, connectionId);
                break;

            case WireMessageKind.CommandResult when message.CommandResult is not null:
                _registry.UpdateCommandResult(message.CommandResult, connectionId);
                break;

            case WireMessageKind.PluginConfigSnapshot when message.PluginConfigSnapshot is not null:
                _pluginConfigService.IngestSnapshot(message.PluginConfigSnapshot, connectionId);
                _registry.TouchConnection(connectionId);
                break;

            case WireMessageKind.PluginLogs when message.PluginLogs is not null:
                IngestPluginLogs(message.PluginLogs, connectionId);
                _registry.TouchConnection(connectionId);
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

            case WireMessageKind.AdminRestart:
                if (_registry.TryGetUniqueName(connectionId, out var restartedUniqueName))
                {
                    _logger.LogInformation(
                        "Admin restarted server {UniqueName} in-game; keeping goal state On and tracking restart.",
                        restartedUniqueName);

                    await _supervisor.BeginAdminRestartAsync(
                        restartedUniqueName,
                        _lifetime.ApplicationStopping);
                }
                else
                {
                    _logger.LogWarning(
                        "Received admin-restart signal for unknown connection {ConnectionId}.",
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
        if (!_registry.TryGetTelemetryKey(connectionId, out var uniqueName))
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

    private sealed class AgentMessageException(WebSocketCloseStatus closeStatus, string reason) : Exception(reason)
    {
        public WebSocketCloseStatus CloseStatus { get; } = closeStatus;
    }

    private static async Task<AgentWireMessage?> ReceiveAsync(WebSocket socket, CancellationToken cancellationToken, int maximumBytes = AgentMessageLimits.MessageBytes)
    {
        var buffer = new byte[16 * 1024];
        using var stream = new MemoryStream();

        while (true)
        {
            var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);

            if (result.MessageType == WebSocketMessageType.Close)
                return null;

            if (result.MessageType != WebSocketMessageType.Text)
                throw new AgentMessageException(WebSocketCloseStatus.InvalidMessageType, "Agent message must use text frames.");
            if (stream.Length + result.Count > maximumBytes)
                throw new AgentMessageException(WebSocketCloseStatus.MessageTooBig,
                    $"Agent message exceeded {maximumBytes}-byte limit ({stream.Length + result.Count} bytes received).");
            stream.Write(buffer, 0, result.Count);

            if (result.EndOfMessage)
                break;
        }

        var json = Encoding.UTF8.GetString(stream.ToArray());
        return JsonSerializer.Deserialize<AgentWireMessage>(json, JsonOptions)
            ?? throw new JsonException("Agent message must be an object.");
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
