using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Magnetar.Protocol.Bridge;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using Sandbox;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Plugins;

namespace Quasar.Agent
{
    public class GameBridge
    {
        private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(1);
        private static readonly MyPromoteLevel[] PromoteLevels =
        {
            MyPromoteLevel.None,
            MyPromoteLevel.Scripter,
            MyPromoteLevel.Moderator,
            MyPromoteLevel.SpaceMaster,
            MyPromoteLevel.Admin,
        };

        private static readonly JsonSerializerSettings PayloadJsonSettings = new JsonSerializerSettings
        {
            ContractResolver = new CamelCasePropertyNamesContractResolver(),
            NullValueHandling = NullValueHandling.Ignore,
        };

        private readonly Stopwatch _uptime = Stopwatch.StartNew();
        private readonly object _sync = new object();
        private readonly int _processId = Process.GetCurrentProcess().Id;
        private readonly string _processName = Process.GetCurrentProcess().ProcessName;
        private readonly string _nodeName = Environment.MachineName;
        private readonly string _nodeId;
        private readonly string _uniqueName;
        private readonly string _pluginVersion;
        private readonly ConcurrentQueue<DeathEventSnapshot> _deathQueue = new ConcurrentQueue<DeathEventSnapshot>();
        private long _lastWorkingSetBytes;
        private DateTime _lastSnapshotUtc = DateTime.MinValue;
        private AgentHello _latestHello;
        private AgentSnapshot _latestSnapshot;

        public GameBridge(object gameInstance)
        {
            _nodeId = (Environment.GetEnvironmentVariable("MAGNETAR_NODE_ID") ?? _nodeName)
                .Trim()
                .ToLowerInvariant();
            _uniqueName = (Environment.GetEnvironmentVariable("QUASAR_UNIQUE_NAME")
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

            var game = MySandboxGame.Static;
            if (game == null)
            {
                if (command.CommandType == ServerCommandType.StopServer)
                {
                    MySandboxGame.ExitThreadSafe();
                    return Task.FromResult(CreateResult(command, true, "Server shutdown requested."));
                }

                return Task.FromResult(CreateResult(command, false, "Game instance not available."));
            }

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
            var serverId = _uniqueName;
            var agentId = $"{serverId}:{_processId}";

            return new AgentHello
            {
                UniqueName = _uniqueName,
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
                UniqueName = hello.UniqueName,
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
                RecentDeaths = GetRecentDeaths(),
                Plugins = GetPlugins(),
            };
        }

        public void RecordDeath(DeathEventSnapshot death)
        {
            if (death == null)
                return;

            _deathQueue.Enqueue(death);
            while (_deathQueue.Count > 50)
                _deathQueue.TryDequeue(out _);
        }

        /// <summary>
        /// Collects the configuration of every loaded plugin that implements
        /// <see cref="IQuasarConfigProvider"/>. Each entry carries the full
        /// <c>SaveJson</c> envelope (schema + defaults + values). Reading is a
        /// pure serialization of the config POCO, so it runs off the game
        /// thread for responsiveness.
        /// </summary>
        public PluginConfigSnapshot GetPluginConfigs()
        {
            var snapshot = new PluginConfigSnapshot { AgentId = GetHello().AgentId };

            foreach (var provider in EnumerateConfigProviders())
            {
                string pluginId;
                string json;
                try
                {
                    pluginId = provider.PluginId ?? string.Empty;
                    json = provider.GetConfigJson();
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[Quasar.Agent] Failed reading plugin config from {provider.GetType().Name}: {exception.Message}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(json))
                    continue;

                snapshot.Plugins.Add(new PluginConfigData
                {
                    PluginId = pluginId,
                    DisplayName = provider.GetType().Name,
                    ConfigJson = json,
                });
            }

            return snapshot;
        }

        /// <summary>
        /// Applies a values document to the plugin identified by
        /// <paramref name="pluginId"/>. The apply is marshalled onto the game
        /// thread because it mutates live config state observed by game logic.
        /// </summary>
        public Task ApplyPluginConfigAsync(string pluginId, string valuesJson)
        {
            if (string.IsNullOrWhiteSpace(pluginId))
                return Task.CompletedTask;

            IQuasarConfigProvider provider = null;
            foreach (var candidate in EnumerateConfigProviders())
            {
                if (string.Equals(candidate.PluginId, pluginId, StringComparison.OrdinalIgnoreCase))
                {
                    provider = candidate;
                    break;
                }
            }

            if (provider == null)
            {
                Console.Error.WriteLine($"[Quasar.Agent] No plugin config provider matched id '{pluginId}'.");
                return Task.CompletedTask;
            }

            var completion = new TaskCompletionSource<bool>();

            void Apply()
            {
                try
                {
                    provider.ApplyConfigJson(valuesJson ?? string.Empty);
                    completion.TrySetResult(true);
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[Quasar.Agent] Failed applying plugin config to '{pluginId}': {exception.Message}");
                    completion.TrySetResult(false);
                }
            }

            var game = MySandboxGame.Static;
            if (game == null)
                Apply();
            else
                game.Invoke(Apply, "Quasar.Agent:ApplyPluginConfig");

            return completion.Task;
        }

        private static IEnumerable<IQuasarConfigProvider> EnumerateConfigProviders()
        {
            foreach (var plugin in EnumeratePlugins())
            {
                if (plugin is IQuasarConfigProvider provider)
                    yield return provider;
            }
        }

        private static IEnumerable<IPlugin> EnumeratePlugins()
        {
            try
            {
                // MyPlugins.Plugins is the live ListReader<IPlugin> aggregate of
                // every loaded plugin (game, user and console). Boxing it to
                // IEnumerable<IPlugin> keeps a live view without copying.
                return MyPlugins.Plugins;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[Quasar.Agent] Failed enumerating plugins: {exception.Message}");
                return Array.Empty<IPlugin>();
            }
        }

        private ServerMetrics BuildMetrics(MySession session)
        {
            var process = Process.GetCurrentProcess();
            _lastWorkingSetBytes = process.WorkingSet64;

            if (session == null)
            {
                return new ServerMetrics
                {
                    MemoryWorkingSetMb = _lastWorkingSetBytes >> 20,
                    UptimeSeconds = (int)_uptime.Elapsed.TotalSeconds,
                    PluginsLoaded = GetPlugins().Count,
                };
            }

            var usedPcu = 0;
            if (session.BlockLimitsEnabled != MyBlockLimitsEnabledEnum.GLOBALLY)
                usedPcu = session.SessionBlockLimits?.PCUBuilt ?? 0;
            else
                usedPcu = session.GlobalBlockLimits?.PCUBuilt ?? 0;

            int? activeGridCount = null;
            int? activeEntityCount = null;
            var gridPcu = 0;

            if (session.Ready)
            {
                try
                {
                    var entities = MyEntities.GetEntities();
                    activeEntityCount = entities?.Count ?? 0;
                    if (entities != null)
                    {
                        var grids = entities.OfType<MyCubeGrid>().ToList();
                        activeGridCount = grids.Count;
                        gridPcu = grids.Sum(grid => Math.Max(0, grid.BlocksPCU));
                    }
                    else
                    {
                        activeGridCount = 0;
                    }
                }
                catch
                {
                    activeGridCount = null;
                    activeEntityCount = null;
                }
            }

            return new ServerMetrics
            {
                PlayersOnline = GetOnlinePlayerCount(session),
                MaxPlayers = session.Settings.MaxPlayers,
                SimulationFrameCounter = MySandboxGame.Static?.SimulationFrameCounter ?? 0,
                SimSpeed = Sync.ServerSimulationRatio,
                SimCpuLoadPercent = (float)Math.Round(Sync.ServerCPULoad, 1),
                ServerCpuLoadPercent = (float)Math.Round(Sync.ServerCPULoad, 1),
                IsSaveInProgress = session.IsSaveInProgress || MyAsyncSaving.InProgress,
                UsedPcu = usedPcu > 0 ? usedPcu : gridPcu,
                TotalPcu = session.Settings.TotalPCU,
                MemoryWorkingSetMb = _lastWorkingSetBytes >> 20,
                ActiveGridCount = activeGridCount,
                ActiveEntityCount = activeEntityCount,
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
                    var steamId = (long)player.Id.SteamId;

                    result.Add(new PlayerSnapshot
                    {
                        SteamId = steamId,
                        IdentityId = player.Identity?.IdentityId ?? 0,
                        SerialId = player.Id.SerialId,
                        DisplayName = player.DisplayName ?? string.Empty,
                        PlatformDisplayName = player.PlatformDisplayName ?? string.Empty,
                        PlatformIcon = player.PlatformIcon ?? string.Empty,
                        GameAcronym = player.GameAcronym ?? string.Empty,
                        ServiceName = GetPlayerServiceName(player.Id.SteamId),
                        FactionTag = GetPlayerFaction(session, player.Identity?.IdentityId ?? 0),
                        PromoteLevel = session.GetUserPromoteLevel(player.Id.SteamId).ToString(),
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

        private List<DeathEventSnapshot> GetRecentDeaths()
        {
            var result = new List<DeathEventSnapshot>();
            while (_deathQueue.TryDequeue(out var death))
                result.Add(death);

            return result;
        }

        private ServerCommandResult ExecuteCommandOnGameThread(ServerCommandEnvelope command)
        {
            if (command.CommandType != ServerCommandType.Refresh &&
                command.CommandType != ServerCommandType.StopServer &&
                (MySession.Static == null || !MySession.Static.Ready))
                return CreateResult(command, false, "Session not ready.");

            switch (command.CommandType)
            {
                case ServerCommandType.Refresh:
                    RefreshSnapshotOnGameThread();
                    return CreateResult(command, true, "Snapshot refreshed.");

                case ServerCommandType.SendChat:
                    return SendChat(command);

                case ServerCommandType.SaveWorld:
                    SaveWorldIfReady();
                    return CreateResult(command, true, "World save requested.");

                case ServerCommandType.StopServer:
                    SaveWorldIfReady();
                    MySandboxGame.ExitThreadSafe();
                    return CreateResult(command, true, "World save and server shutdown requested.");

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
                    var promotedLevel = GetAdjacentPromoteLevel((ulong)(command.SteamId ?? 0), 1);
                    MySession.Static.SetUserPromoteLevel((ulong)(command.SteamId ?? 0), promotedLevel);
                    return CreateResult(command, true, $"Promote requested for {command.SteamId} to {promotedLevel}.");

                case ServerCommandType.DemotePlayer:
                    var demotedLevel = GetAdjacentPromoteLevel((ulong)(command.SteamId ?? 0), -1);
                    MySession.Static.SetUserPromoteLevel((ulong)(command.SteamId ?? 0), demotedLevel);
                    return CreateResult(command, true, $"Demote requested for {command.SteamId} to {demotedLevel}.");

                case ServerCommandType.SetPlayerPromoteLevel:
                    if (!TryParsePromoteLevel(command.Text, out var targetLevel))
                        return CreateResult(command, false, $"Unknown promote level '{command.Text}'.");

                    MySession.Static.SetUserPromoteLevel((ulong)(command.SteamId ?? 0), targetLevel);
                    return CreateResult(command, true, $"Promote level set to {targetLevel} for {command.SteamId}.");

                case ServerCommandType.ListEntities:
                    return ListEntities(command);

                case ServerCommandType.DeleteEntity:
                    return DeleteEntity(command);

                default:
                    return CreateResult(command, false, $"Unsupported command '{command.CommandType}'.");
            }
        }

        private static void SaveWorldIfReady()
        {
            var session = MySession.Static;
            if (session == null || !session.Ready)
                return;

            session.Save(null);
        }

        private ServerCommandResult SendChat(ServerCommandEnvelope command)
        {
            var text = (command.Text ?? string.Empty).Trim();
            if (string.IsNullOrWhiteSpace(text))
                return CreateResult(command, false, "Chat message is empty.");

            MyMultiplayer.Static?.SendChatMessage(text, ChatChannel.Global, 0L);

            return CreateResult(command, true, "Chat message sent.");
        }

        private ServerCommandResult ListEntities(ServerCommandEnvelope command)
        {
            var filter = DeserializePayload<EntityListFilter>(command.Payload) ?? new EntityListFilter();
            var result = EntityInspector.Query(filter);
            var message = $"Returned {result.Entities.Count} of {result.TotalCount} matching ({result.TotalEntityCount} total).";
            return CreateResult(command, true, message, SerializePayload(result));
        }

        private ServerCommandResult DeleteEntity(ServerCommandEnvelope command)
        {
            var request = DeserializePayload<EntityDeleteRequest>(command.Payload);
            if (request == null || request.EntityId == 0)
                return CreateResult(command, false, "Delete request is missing an entity id.");

            var success = EntityInspector.TryDelete(request.EntityId, out var message);
            return CreateResult(command, success, message);
        }

        private static string SerializePayload(object value)
        {
            return JsonConvert.SerializeObject(value, PayloadJsonSettings);
        }

        private static T DeserializePayload<T>(string payload) where T : class
        {
            return string.IsNullOrWhiteSpace(payload)
                ? null
                : JsonConvert.DeserializeObject<T>(payload, PayloadJsonSettings);
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

        private string GetPlayerServiceName(ulong steamId)
        {
            if (steamId == 0)
                return string.Empty;

            try
            {
                return MyMultiplayer.Static?.GetMemberServiceName(steamId) ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static MyPromoteLevel GetAdjacentPromoteLevel(ulong steamId, int direction)
        {
            var current = MySession.Static.GetUserPromoteLevel(steamId);
            var index = Array.IndexOf(PromoteLevels, current);
            if (index < 0)
                index = 0;

            return PromoteLevels[Math.Max(0, Math.Min(PromoteLevels.Length - 1, index + direction))];
        }

        private static bool TryParsePromoteLevel(string value, out MyPromoteLevel promoteLevel)
        {
            if (Enum.TryParse(value?.Trim() ?? string.Empty, ignoreCase: true, out promoteLevel) &&
                Array.IndexOf(PromoteLevels, promoteLevel) >= 0)
            {
                return true;
            }

            promoteLevel = MyPromoteLevel.None;
            return false;
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

        private static ServerCommandResult CreateResult(ServerCommandEnvelope command, bool success, string message, string payload = null)
        {
            return new ServerCommandResult
            {
                CommandId = command.CommandId,
                UniqueName = command.UniqueName,
                AgentId = command.AgentId,
                ServerId = command.ServerId,
                Success = success,
                Message = message,
                Payload = payload ?? string.Empty,
                CompletedAtUtc = DateTimeOffset.UtcNow,
            };
        }
    }
}
