using System;
using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Magnetar.Protocol.Model;
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
        private string _lastPluginConfigJson;
        private volatile ClientWebSocket _socket;

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

                        _socket = socket;
                        _lastPluginConfigJson = null;

                        try
                        {
                            await SendAsync(socket, new AgentWireMessage
                            {
                                Kind = WireMessageKind.Hello,
                                Hello = _bridge.GetHello(),
                            }, cancellationToken).ConfigureAwait(false);

                            await SendPluginConfigsAsync(socket, force: true, cancellationToken).ConfigureAwait(false);

                            var snapshotTask = SnapshotLoopAsync(socket, cancellationToken);
                            var receiveTask = ReceiveLoopAsync(socket, cancellationToken);
                            await Task.WhenAny(snapshotTask, receiveTask).ConfigureAwait(false);
                        }
                        finally
                        {
                            _socket = null;
                        }
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

        /// <summary>
        /// Best-effort, synchronous signal sent when the server is shutting down
        /// because of an admin/console stop that Quasar did not request. Invoked
        /// on the game thread during session unload, while the socket is still
        /// open and before the process exits. Never hangs shutdown.
        /// </summary>
        public void TrySendAdminStop()
        {
            var socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open)
                return;

            try
            {
                SendAsync(socket, new AgentWireMessage
                {
                    Kind = WireMessageKind.AdminStop,
                }, CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception)
            {
                Log($"Failed sending admin-stop signal: {exception.Message}");
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

                await SendPluginConfigsAsync(socket, force: false, cancellationToken).ConfigureAwait(false);

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
                else if (message.Kind == WireMessageKind.PluginConfigUpdate && message.PluginConfigUpdateRequest != null)
                {
                    var request = message.PluginConfigUpdateRequest;
                    await _bridge.ApplyPluginConfigAsync(request.PluginId, request.ValuesJson).ConfigureAwait(false);

                    // Push the post-apply state so the editor reflects exactly
                    // what the plugin accepted (clamped / normalized values).
                    await SendPluginConfigsAsync(socket, force: true, cancellationToken).ConfigureAwait(false);
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

        private async Task SendPluginConfigsAsync(ClientWebSocket socket, bool force, CancellationToken cancellationToken)
        {
            PluginConfigSnapshot configs;
            try
            {
                configs = _bridge.GetPluginConfigs();
            }
            catch (Exception exception)
            {
                Log($"Failed collecting plugin configs: {exception.Message}");
                return;
            }

            var serialized = JsonConvert.SerializeObject(configs, JsonSettings);
            if (!force && string.Equals(serialized, _lastPluginConfigJson, StringComparison.Ordinal))
                return;

            _lastPluginConfigJson = serialized;

            await SendAsync(socket, new AgentWireMessage
            {
                Kind = WireMessageKind.PluginConfigSnapshot,
                PluginConfigSnapshot = configs,
            }, cancellationToken).ConfigureAwait(false);
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
