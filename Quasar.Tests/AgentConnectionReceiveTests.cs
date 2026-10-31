using System.Net.WebSockets;
using System.Text;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Quasar.Agent;
using Xunit;

namespace Quasar.Tests;

public sealed class AgentConnectionReceiveTests
{
    [Fact]
    public async Task FragmentedTextMessageIsDeserialized()
    {
        var payload = AgentConnection.SerializeMessage(new AgentWireMessage { Kind = WireMessageKind.Ping });
        using var socket = new ScriptedWebSocket(
            new(payload[..5], WebSocketMessageType.Text, false),
            new(payload[5..], WebSocketMessageType.Text, true));

        var message = await AgentConnection.ReceiveAsync(socket, default, 1024);

        Assert.Equal(WireMessageKind.Ping, message.Kind);
        Assert.Null(socket.CloseStatus);
    }

    [Fact]
    public async Task FragmentedMessageOverLimitIsClosed()
    {
        using var socket = new ScriptedWebSocket(
            new(Encoding.UTF8.GetBytes("1234567890"), WebSocketMessageType.Text, false),
            new(Encoding.UTF8.GetBytes("1234567"), WebSocketMessageType.Text, true));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AgentConnection.ReceiveAsync(socket, default, 16));

        Assert.Equal(WebSocketCloseStatus.MessageTooBig, socket.CloseStatus);
        Assert.Equal("Quasar message exceeds 16-byte limit.", socket.CloseStatusDescription);
    }

    [Fact]
    public async Task BinaryMessageIsClosed()
    {
        using var socket = new ScriptedWebSocket(
            new Frame(Encoding.UTF8.GetBytes("{}"), WebSocketMessageType.Binary, true));

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            AgentConnection.ReceiveAsync(socket, default, 16));

        Assert.Equal(WebSocketCloseStatus.InvalidMessageType, socket.CloseStatus);
        Assert.Equal("Quasar messages must use text frames.", socket.CloseStatusDescription);
    }

    private sealed record Frame(byte[] Payload, WebSocketMessageType MessageType, bool EndOfMessage);

    private sealed class ScriptedWebSocket(params Frame[] frames) : WebSocket
    {
        private readonly Queue<Frame> _frames = new(frames);
        private WebSocketState _state = WebSocketState.Open;
        private WebSocketCloseStatus? _closeStatus;
        private string? _closeStatusDescription;

        public override WebSocketCloseStatus? CloseStatus => _closeStatus;
        public override string? CloseStatusDescription => _closeStatusDescription;
        public override WebSocketState State => _state;
        public override string? SubProtocol => null;

        public override void Abort() => _state = WebSocketState.Aborted;

        public override Task CloseAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.Closed;
            return Task.CompletedTask;
        }

        public override Task CloseOutputAsync(
            WebSocketCloseStatus closeStatus,
            string? statusDescription,
            CancellationToken cancellationToken)
        {
            _closeStatus = closeStatus;
            _closeStatusDescription = statusDescription;
            _state = WebSocketState.CloseSent;
            return Task.CompletedTask;
        }

        public override void Dispose() => _state = WebSocketState.Closed;

        public override Task<WebSocketReceiveResult> ReceiveAsync(
            ArraySegment<byte> buffer,
            CancellationToken cancellationToken)
        {
            var frame = _frames.Dequeue();
            Array.Copy(frame.Payload, 0, buffer.Array!, buffer.Offset, frame.Payload.Length);
            return Task.FromResult(new WebSocketReceiveResult(
                frame.Payload.Length,
                frame.MessageType,
                frame.EndOfMessage));
        }

        public override Task SendAsync(
            ArraySegment<byte> buffer,
            WebSocketMessageType messageType,
            bool endOfMessage,
            CancellationToken cancellationToken) => Task.CompletedTask;
    }
}
