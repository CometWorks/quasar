using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Magnetar.Protocol.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;

namespace Quasar.Agent
{
    public class AgentConnection
    {
        private static readonly JsonSerializerSettings JsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
        };

        private readonly GameBridge _bridge;
        private readonly WebServiceLocator _locator;
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _cancellation;
        private Task _runTask;

        public AgentConnection(GameBridge bridge, WebServiceLocator locator)
        {
            _bridge = bridge;
            _locator = locator;
        }

        public void Start()
        {
            if (_runTask != null)
                return;

            _cancellation = new CancellationTokenSource();
            _runTask = Task.Run(() => RunAsync(_cancellation.Token));
            Log("Agent connection loop started.");
        }

        public void Stop()
        {
            if (_cancellation == null)
                return;

            try
            {
                _cancellation.Cancel();
                _runTask?.Wait(TimeSpan.FromSeconds(5));
            }
            catch
            {
            }
            finally
            {
                _cancellation.Dispose();
                _cancellation = null;
                _runTask = null;
            }

            Log("Agent connection loop stopped.");
        }

        private async Task RunAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var baseUri = await _locator.EnsureWebServiceAsync(cancellationToken).ConfigureAwait(false);
                    if (baseUri == null)
                    {
                        await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
                        continue;
                    }

                    using (var socket = new ClientWebSocket())
                    {
                        socket.Options.KeepAliveInterval = TimeSpan.FromSeconds(20);
                        socket.Options.AddSubProtocol("quasar.agent.v1");

                        var socketUri = new UriBuilder(baseUri)
                        {
                            Scheme = baseUri.Scheme == "https" ? "wss" : "ws",
                            Path = "/ws/agent",
                        }.Uri;

                        Log($"Connecting to {socketUri}");
                        await socket.ConnectAsync(socketUri, cancellationToken).ConfigureAwait(false);

                        await SendAsync(socket, new AgentWireMessage
                        {
                            Kind = WireMessageKind.Hello,
                            Hello = _bridge.GetHello(),
                        }, cancellationToken).ConfigureAwait(false);

                        var snapshotTask = SnapshotLoopAsync(socket, cancellationToken);
                        var receiveTask = ReceiveLoopAsync(socket, cancellationToken);
                        await Task.WhenAny(snapshotTask, receiveTask).ConfigureAwait(false);
                    }
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Log($"Connection error: {exception.Message}");
                }

                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task SnapshotLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                await SendAsync(socket, new AgentWireMessage
                {
                    Kind = WireMessageKind.Snapshot,
                    Snapshot = _bridge.GetSnapshot(),
                }, cancellationToken).ConfigureAwait(false);

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }

        private async Task ReceiveLoopAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var message = await ReceiveAsync(socket, cancellationToken).ConfigureAwait(false);
                if (message == null)
                    break;

                if (message.Kind == WireMessageKind.Command && message.Command != null)
                {
                    var result = await _bridge.ExecuteCommandAsync(message.Command, cancellationToken).ConfigureAwait(false);
                    await SendAsync(socket, new AgentWireMessage
                    {
                        Kind = WireMessageKind.CommandResult,
                        CommandResult = result,
                    }, cancellationToken).ConfigureAwait(false);
                }
                else if (message.Kind == WireMessageKind.Ping)
                {
                    await SendAsync(socket, new AgentWireMessage
                    {
                        Kind = WireMessageKind.Pong,
                        Message = "pong",
                    }, cancellationToken).ConfigureAwait(false);
                }
            }
        }

        private async Task SendAsync(ClientWebSocket socket, AgentWireMessage message, CancellationToken cancellationToken)
        {
            var json = JsonConvert.SerializeObject(message, JsonSettings);
            var payload = Encoding.UTF8.GetBytes(json);

            await _sendLock.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                await socket.SendAsync(
                    new ArraySegment<byte>(payload),
                    WebSocketMessageType.Text,
                    true,
                    cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _sendLock.Release();
            }
        }

        private static async Task<AgentWireMessage> ReceiveAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            var buffer = new byte[16 * 1024];
            using (var stream = new MemoryStream())
            {
                while (true)
                {
                    var result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken).ConfigureAwait(false);

                    if (result.MessageType == WebSocketMessageType.Close)
                        return null;

                    stream.Write(buffer, 0, result.Count);
                    if (result.EndOfMessage)
                        break;
                }

                var json = Encoding.UTF8.GetString(stream.ToArray());
                return JsonConvert.DeserializeObject<AgentWireMessage>(json, JsonSettings);
            }
        }

        private static void Log(string message)
        {
            Console.WriteLine($"[Quasar.Agent] {message}");
        }
    }
}
