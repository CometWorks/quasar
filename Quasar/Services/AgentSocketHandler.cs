using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Magnetar.Protocol.Transport;

namespace Quasar.Services;

public sealed class AgentSocketHandler
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly AgentRegistry _registry;
    private readonly ILogger<AgentSocketHandler> _logger;

    public AgentSocketHandler(AgentRegistry registry, ILogger<AgentSocketHandler> logger)
    {
        _registry = registry;
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

        try
        {
            while (socket.State == WebSocketState.Open && !context.RequestAborted.IsCancellationRequested)
            {
                var message = await ReceiveAsync(socket, context.RequestAborted);
                if (message is null)
                    break;

                await ProcessMessageAsync(message, connectionId, socket, context.RequestAborted);
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
