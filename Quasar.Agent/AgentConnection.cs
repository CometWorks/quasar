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
using PluginSdk;

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
        private readonly AgentOptions _options;
        private readonly PluginLogOutbox _outbox;
        private readonly Random _rng = new Random();
        private readonly SemaphoreSlim _sendLock = new SemaphoreSlim(1, 1);
        private CancellationTokenSource _cancellation;
        private Task _runTask;
        private string _lastPluginConfigJson;
        private volatile ClientWebSocket _socket;

        // True once the agent has connected to Quasar at least once. The
        // autonomous save-and-stop only arms after a prior connection, so a
        // standalone server that never reached Quasar is never auto-stopped.
        private bool _hasConnected;

        // When the agent first noticed it had lost Quasar, used to measure how
        // long Quasar has been unreachable. Null while connected.
        private DateTime? _disconnectedSinceUtc;

        public AgentConnection(GameBridge bridge, WebServiceLocator locator, AgentOptions options, PluginLogOutbox outbox)
        {
            _bridge = bridge;
            _locator = locator;
            _options = options ?? AgentOptions.FromEnvironment();
            _outbox = outbox;
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
                    if (baseUri != null)
                    {
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

                            // We are connected: arm the autonomous self-stop and clear
                            // any pending offline countdown from a previous outage.
                            _hasConnected = true;
                            _disconnectedSinceUtc = null;

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
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception exception)
                {
                    Log($"Connection error: {exception.Message}");
                }

                if (!await HandleDisconnectedAndDelayAsync(cancellationToken).ConfigureAwait(false))
                    break;
            }
        }

        /// <summary>
        /// Called after the connection to Quasar dropped or could not be made.
        /// Once the agent has been connected at least once, it counts how long
        /// Quasar has been unreachable; when that exceeds the configured offline
        /// window (or that window is zero/negative, meaning "stop promptly") it
        /// saves the world and stops the server autonomously — no command from
        /// Quasar required, so it also handles a crash, power loss or network
        /// partition. Otherwise it waits a jittered interval before retrying.
        /// Returns false when the loop should stop (self-stop triggered or the
        /// agent is shutting down), true to keep reconnecting.
        /// </summary>
        private async Task<bool> HandleDisconnectedAndDelayAsync(CancellationToken cancellationToken)
        {
            if (cancellationToken.IsCancellationRequested)
                return false;

            var now = DateTime.UtcNow;
            if (_disconnectedSinceUtc == null)
                _disconnectedSinceUtc = now;

            if (_hasConnected && ShouldSelfStop(now))
            {
                var offline = now - _disconnectedSinceUtc.Value;
                Log($"Quasar has been unreachable for {offline.TotalSeconds:F0} second(s); saving the world and stopping the server.");
                try
                {
                    ServerControl.SaveAndQuit();
                }
                catch (Exception exception)
                {
                    Log($"Failed to save and stop the server: {exception.Message}");
                }

                return false;
            }

            try
            {
                await Task.Delay(NextReconnectDelay(), cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            return true;
        }

        private bool ShouldSelfStop(DateTime now)
        {
            // Zero/negative means stop as soon as Quasar is gone (crash-safe
            // equivalent of an immediate shutdown), but still only after a prior
            // connection, which the caller has already checked.
            if (_options.OfflineShutdownSeconds <= 0)
                return true;

            return now - _disconnectedSinceUtc.Value >= TimeSpan.FromSeconds(_options.OfflineShutdownSeconds);
        }

        private TimeSpan NextReconnectDelay()
        {
            double seconds = _options.ReconnectIntervalSeconds;
            if (_options.ReconnectJitterSeconds > 0)
                seconds += (_rng.NextDouble() * 2.0 - 1.0) * _options.ReconnectJitterSeconds;

            if (seconds < 1.0)
                seconds = 1.0;

            return TimeSpan.FromSeconds(seconds);
        }

        /// <summary>
        /// Best-effort, synchronous signal sent when the server is shutting down
        /// because of an admin/console stop that Quasar did not request. Invoked
        /// on the game thread during session unload, while the socket is still
        /// open and before the process exits. Never hangs shutdown.
        /// </summary>
        public bool TrySendAdminStop()
        {
            return TrySendAdminSignal(WireMessageKind.AdminStop, "admin-stop");
        }

        /// <summary>
        /// Best-effort signal sent when an admin requested an in-game restart.
        /// Quasar keeps the goal state On and moves the server into Restarting
        /// before the process exits.
        /// </summary>
        public bool TrySendAdminRestart()
        {
            return TrySendAdminSignal(WireMessageKind.AdminRestart, "admin-restart");
        }

        private bool TrySendAdminSignal(string kind, string label)
        {
            var socket = _socket;
            if (socket == null || socket.State != WebSocketState.Open)
                return false;

            try
            {
                return SendAsync(socket, new AgentWireMessage
                {
                    Kind = kind,
                }, CancellationToken.None).Wait(TimeSpan.FromSeconds(2));
            }
            catch (Exception exception)
            {
                Log($"Failed sending {label} signal: {exception.Message}");
                return false;
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

                await FlushPluginLogsAsync(socket, cancellationToken).ConfigureAwait(false);

                await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            }
        }

        /// <summary>
        /// Ships any buffered plugin log lines to Quasar, draining the outbox in
        /// capped batches (so a large backlog accumulated while Quasar was down is
        /// flushed promptly on reconnect). On a send failure the batch is returned
        /// to the outbox and the error propagates so the connection is re-established.
        /// </summary>
        private async Task FlushPluginLogsAsync(ClientWebSocket socket, CancellationToken cancellationToken)
        {
            if (_outbox == null)
                return;

            while (socket.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var batch = _outbox.DrainBatch();
                if (batch.Count == 0)
                    return;

                try
                {
                    await SendAsync(socket, new AgentWireMessage
                    {
                        Kind = WireMessageKind.PluginLogs,
                        PluginLogs = new PluginLogBatch { Lines = batch },
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch
                {
                    // Keep the lines so the next connection retries them.
                    _outbox.Requeue(batch);
                    throw;
                }
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
