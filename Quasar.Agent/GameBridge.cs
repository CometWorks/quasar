using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Gui;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;

namespace Quasar.Agent
{
    public class GameBridge
    {
        private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(1);
        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private readonly object _sync = new object();
        private readonly int _processId = Process.GetCurrentProcess().Id;
        private readonly string _processName = Process.GetCurrentProcess().ProcessName;
        private readonly string _nodeName = Environment.MachineName;
        private readonly string _nodeId;
        private readonly string _instanceId;
        private readonly string _pluginVersion;
        private DateTime _lastSnapshotUtc = DateTime.MinValue;
        private AgentHello _latestHello;
        private AgentSnapshot _latestSnapshot;

        public GameBridge(object gameInstance)
        {
            _nodeId = (Environment.GetEnvironmentVariable("MAGNETAR_NODE_ID") ?? _nodeName)
                .Trim()
                .ToLowerInvariant();
            _instanceId = (Environment.GetEnvironmentVariable("QUASAR_INSTANCE_ID")
                    ?? $"unmanaged-{_nodeId}-{_processId}")
                .Trim();
            _pluginVersion = typeof(AdminPlugin).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        }

        public void Update()
        {
            if ((DateTime.UtcNow - _lastSnapshotUtc) < SnapshotInterval)
                return;

            _lastSnapshotUtc = DateTime.UtcNow;
            RefreshSnapshotOnGameThread();
        }

        public AgentHello GetHello()
        {
            lock (_sync)
            {
                return _latestHello ?? BuildHello();
            }
        }

        public AgentSnapshot GetSnapshot()
        {
            lock (_sync)
            {
                return _latestSnapshot ?? BuildSnapshot();
            }
        }

        public Task<ServerCommandResult> ExecuteCommandAsync(ServerCommandEnvelope command, CancellationToken cancellationToken)
        {
            if (command == null)
                throw new ArgumentNullException(nameof(command));

            if (command.CommandType == ServerCommandType.StopServer)
            {
                MySandboxGame.ExitThreadSafe();
                return Task.FromResult(CreateResult(command, true, "Server shutdown requested."));
            }

            var game = MySandboxGame.Static;
            if (game == null)
                return Task.FromResult(CreateResult(command, false, "Game instance not available."));

            var completion = new TaskCompletionSource<ServerCommandResult>();

            using (cancellationToken.Register(() => completion.TrySetCanceled()))
            {
                game.Invoke(() =>
                {
                    try
                    {
                        completion.TrySetResult(ExecuteCommandOnGameThread(command));
                    }
                    catch (Exception exception)
                    {
                        completion.TrySetResult(CreateResult(command, false, exception.Message));
                    }
                }, $"Quasar.Agent:{command.CommandType}");

                return completion.Task;
            }
        }

        private void RefreshSnapshotOnGameThread()
        {
            lock (_sync)
            {
                _latestHello = BuildHello();
                _latestSnapshot = BuildSnapshot(_latestHello);
            }
        }

        private AgentHello BuildHello()
        {
            var session = MySession.Static;
            var serverName = GetServerName(session);
            var worldName = GetWorldName(session);
            var serverId = _instanceId;
            var agentId = $"{serverId}:{_processId}";

            return new AgentHello
            {
                InstanceId = _instanceId,
                AgentId = agentId,
                NodeId = _nodeId,
                NodeName = _nodeName,
                ServerId = serverId,
                ServerName = serverName,
                WorldName = worldName,
                PluginId = "quasar-agent",
                PluginVersion = _pluginVersion,
                ProcessId = _processId,
                ProcessName = _processName,
                GameVersion = session?.AppVersionFromSave.ToString() ?? string.Empty,
                ConnectedAtUtc = DateTimeOffset.UtcNow,
            };
        }

        private AgentSnapshot BuildSnapshot(AgentHello hello = null)
        {
            hello ??= BuildHello();
            var session = MySession.Static;

            return new AgentSnapshot
            {
                InstanceId = hello.InstanceId,
                AgentId = hello.AgentId,
                NodeId = hello.NodeId,
                NodeName = hello.NodeName,
                ServerId = hello.ServerId,
                ServerName = hello.ServerName,
                WorldName = hello.WorldName,
                IsRunning = session != null && session.Ready,
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Metrics = BuildMetrics(session),
                Players = GetPlayers(session),
                RecentChat = GetRecentChat(),
                Plugins = GetPlugins(),
            };
        }

        private ServerMetrics BuildMetrics(MySession session)
        {
            if (session == null)
            {
                return new ServerMetrics
                {
                    UptimeSeconds = (int)_uptime.Elapsed.TotalSeconds,
                    PluginsLoaded = GetPlugins().Count,
                };
            }

            var usedPcu = 0;
            if (session.BlockLimitsEnabled != MyBlockLimitsEnabledEnum.GLOBALLY)
                usedPcu = session.SessionBlockLimits?.PCUBuilt ?? 0;
            else
                usedPcu = session.GlobalBlockLimits?.PCUBuilt ?? 0;

            return new ServerMetrics
            {
                PlayersOnline = GetOnlinePlayerCount(session),
                MaxPlayers = session.Settings.MaxPlayers,
                SimSpeed = Sync.ServerSimulationRatio,
                SimCpuLoadPercent = (float)Math.Round(Sync.ServerCPULoad, 1),
                ServerCpuLoadPercent = (float)Math.Round(Sync.ServerCPULoad, 1),
                UsedPcu = usedPcu,
                TotalPcu = session.Settings.TotalPCU,
                UptimeSeconds = (int)_uptime.Elapsed.TotalSeconds,
                ModsLoaded = session.Mods?.Count ?? 0,
                PluginsLoaded = GetPlugins().Count,
            };
        }

        private List<PlayerSnapshot> GetPlayers(MySession session)
        {
            var result = new List<PlayerSnapshot>();
            if (session == null || !session.Ready)
                return result;

            try
            {
                foreach (var player in session.Players.GetOnlinePlayers())
                {
                    result.Add(new PlayerSnapshot
                    {
                        SteamId = (long)player.Id.SteamId,
                        DisplayName = player.DisplayName ?? string.Empty,
                        FactionTag = GetPlayerFaction(session, player.Identity?.IdentityId ?? 0),
                        IsAdmin = session.IsUserAdmin(player.Id.SteamId),
                        PingMs = 0,
                    });
                }
            }
            catch
            {
            }

            return result;
        }

        private List<ChatMessageSnapshot> GetRecentChat()
        {
            var result = new List<ChatMessageSnapshot>();
            if (!(MyMultiplayer.Static is MyDedicatedServer dedicatedServer))
                return result;

            try
            {
                foreach (var message in dedicatedServer.GlobalChatHistory.Skip(Math.Max(0, dedicatedServer.GlobalChatHistory.Count - 100)))
                {
                    result.Add(new ChatMessageSnapshot
                    {
                        SteamId = (long)message.SteamId,
                        AuthorName = message.AuthorName ?? string.Empty,
                        Content = message.Text ?? string.Empty,
                        TimestampTicksUtc = message.Timestamp.Ticks,
                    });
                }
            }
            catch
            {
            }

            return result;
        }

        private List<PluginRuntimeInfo> GetPlugins()
        {
            var result = new List<PluginRuntimeInfo>();
            var pluginPaths = MySandboxGame.ConfigDedicated?.Plugins;
            if (pluginPaths == null)
                return result;

            foreach (var pluginPath in pluginPaths)
            {
                result.Add(new PluginRuntimeInfo
                {
                    PluginId = pluginPath ?? string.Empty,
                    DisplayName = Path.GetFileNameWithoutExtension(pluginPath ?? string.Empty),
                    Version = string.Empty,
                    IsLoaded = true,
                });
            }

            return result;
        }

        private ServerCommandResult ExecuteCommandOnGameThread(ServerCommandEnvelope command)
        {
            if (command.CommandType != ServerCommandType.Refresh && (MySession.Static == null || !MySession.Static.Ready))
                return CreateResult(command, false, "Session not ready.");

            switch (command.CommandType)
            {
                case ServerCommandType.Refresh:
                    RefreshSnapshotOnGameThread();
                    return CreateResult(command, true, "Snapshot refreshed.");

                case ServerCommandType.SendChat:
                    return SendChat(command);

                case ServerCommandType.SaveWorld:
                    MySession.Static.Save(null);
                    return CreateResult(command, true, "World save requested.");

                case ServerCommandType.KickPlayer:
                    MyMultiplayer.Static?.KickClient((ulong)(command.SteamId ?? 0));
                    return CreateResult(command, true, $"Kick requested for {command.SteamId}.");

                case ServerCommandType.BanPlayer:
                    MyMultiplayer.Static?.BanClient((ulong)(command.SteamId ?? 0), true);
                    return CreateResult(command, true, $"Ban requested for {command.SteamId}.");

                case ServerCommandType.UnbanPlayer:
                    MyMultiplayer.Static?.BanClient((ulong)(command.SteamId ?? 0), false);
                    return CreateResult(command, true, $"Unban requested for {command.SteamId}.");

                case ServerCommandType.PromotePlayer:
                    MySession.Static.SetUserPromoteLevel((ulong)(command.SteamId ?? 0), MyPromoteLevel.Admin);
                    return CreateResult(command, true, $"Promote requested for {command.SteamId}.");

                case ServerCommandType.DemotePlayer:
                    MySession.Static.SetUserPromoteLevel((ulong)(command.SteamId ?? 0), MyPromoteLevel.None);
                    return CreateResult(command, true, $"Demote requested for {command.SteamId}.");

                default:
                    return CreateResult(command, false, $"Unsupported command '{command.CommandType}'.");
            }
        }

        private ServerCommandResult SendChat(ServerCommandEnvelope command)
        {
            var text = (command.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return CreateResult(command, false, "Chat message is empty.");

            MyMultiplayer.Static?.SendChatMessage(text, ChatChannel.Global, 0L);

            return CreateResult(command, true, "Chat message sent.");
        }

        private int GetOnlinePlayerCount(MySession session)
        {
            try
            {
                return session?.Players?.GetOnlinePlayers()?.Count ?? 0;
            }
            catch
            {
                return 0;
            }
        }

        private string GetPlayerFaction(MySession session, long identityId)
        {
            if (session == null || identityId == 0)
                return string.Empty;

            try
            {
                return session.Factions?.GetPlayerFaction(identityId)?.Tag ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private string GetServerName(MySession session)
        {
            return session?.Name
                   ?? MySandboxGame.ConfigDedicated?.ServerName
                   ?? $"Space Engineers {_processId}";
        }

        private string GetWorldName(MySession session)
        {
            return session?.Name
                   ?? MySandboxGame.ConfigDedicated?.WorldName
                   ?? "Unknown World";
        }

        private static ServerCommandResult CreateResult(ServerCommandEnvelope command, bool success, string message)
        {
            return new ServerCommandResult
            {
                CommandId = command.CommandId,
                InstanceId = command.InstanceId,
                AgentId = command.AgentId,
                ServerId = command.ServerId,
                Success = success,
                Message = message,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            };
        }
    }
}
