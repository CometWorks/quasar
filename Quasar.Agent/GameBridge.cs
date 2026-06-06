using System;
using System.Collections;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Magnetar.Protocol.Bridge;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using PluginSdk.Config;
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
        private readonly string _hostName = Environment.MachineName;
        private readonly string _hostId;
        private readonly string _uniqueName;
        private readonly string _pluginVersion;
        private readonly ConcurrentQueue<DeathEventSnapshot> _deathQueue = new ConcurrentQueue<DeathEventSnapshot>();
        private long _lastWorkingSetBytes;
        private DateTime _lastSnapshotUtc = DateTime.MinValue;
        private AgentHello _latestHello;
        private AgentSnapshot _latestSnapshot;
        private volatile bool _quasarRequestedStop;

        /// <summary>
        /// True once Quasar itself asked this server to stop (via a
        /// <see cref="ServerCommandType.StopServer"/> command). Used to tell an
        /// admin-issued in-game stop apart from a Quasar-initiated one when the
        /// Magnetar host raises its termination event.
        /// </summary>
        public bool QuasarRequestedStop => _quasarRequestedStop;

        public GameBridge(object gameServer)
        {
            _hostId = (Environment.GetEnvironmentVariable("MAGNETAR_HOST_ID") ?? _hostName)
                .Trim()
                .ToLowerInvariant();
            _uniqueName = (Environment.GetEnvironmentVariable("QUASAR_UNIQUE_NAME")
                    ?? $"unmanaged-{_hostId}-{_processId}")
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
                    _quasarRequestedStop = true;
                    MySandboxGame.ExitThreadSafe();
                    return Task.FromResult(CreateResult(command, true, "Server shutdown requested."));
                }

                return Task.FromResult(CreateResult(command, false, "Game server not available."));
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
                HostId = _hostId,
                HostName = _hostName,
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
                HostId = hello.HostId,
                HostName = hello.HostName,
                ServerId = hello.ServerId,
                ServerName = hello.ServerName,
                WorldName = hello.WorldName,
                IsRunning = session != null && session.Ready,
                CapturedAtUtc = DateTimeOffset.UtcNow,
                Metrics = BuildMetrics(session),
                Players = GetPlayers(session),
                KickedPlayers = GetKickedPlayers(session),
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
                string displayName;
                string json;
                try
                {
                    pluginId = provider.PluginId ?? string.Empty;
                    displayName = provider.DisplayName ?? provider.PluginId ?? string.Empty;
                    json = provider.GetConfigJson();
                }
                catch (Exception exception)
                {
                    Console.Error.WriteLine($"[Quasar.Agent] Failed reading plugin config from {provider.DisplayName}: {exception.Message}");
                    continue;
                }

                if (string.IsNullOrWhiteSpace(json))
                    continue;

                snapshot.Plugins.Add(new PluginConfigData
                {
                    PluginId = pluginId,
                    DisplayName = displayName,
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

            ConfigProviderAdapter provider = null;
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

        private static IEnumerable<ConfigProviderAdapter> EnumerateConfigProviders()
        {
            foreach (var loaded in EnumeratePlugins())
            {
                var plugin = loaded.Plugin;
                if (plugin is IQuasarConfigProvider provider)
                {
                    yield return ConfigProviderAdapter.ForExplicit(loaded, provider);
                    continue;
                }

                var sdkProvider = ConfigProviderAdapter.TryCreateForSdkConfig(loaded);
                if (sdkProvider != null)
                    yield return sdkProvider;
            }
        }

        private static IEnumerable<LoadedPlugin> EnumeratePlugins()
        {
            List<IPlugin> roots;
            try
            {
                roots = MyPlugins.Plugins.Where(plugin => plugin != null).ToList();
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[Quasar.Agent] Failed enumerating plugins: {exception.Message}");
                yield break;
            }

            var seen = new HashSet<IPlugin>();
            foreach (var plugin in roots)
            {
                if (seen.Add(plugin))
                    yield return LoadedPlugin.FromPlugin(plugin);

                foreach (var child in EnumerateChildPlugins(plugin))
                {
                    if (child.Plugin != null && seen.Add(child.Plugin))
                        yield return child;
                }
            }
        }

        private static IEnumerable<LoadedPlugin> EnumerateChildPlugins(IPlugin plugin)
        {
            var pluginType = plugin.GetType();
            if (!string.Equals(pluginType.FullName, "Pulsar.Legacy.Loader.PluginLoader", StringComparison.Ordinal))
                yield break;

            IEnumerable servers = null;
            try
            {
                servers = pluginType
                    .GetProperty("Plugins", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(plugin) as IEnumerable;
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[Quasar.Agent] Failed reading Pulsar plugin list: {exception.Message}");
            }

            if (servers == null)
                yield break;

            foreach (var server in servers)
            {
                var child = TryCreateLoadedPluginFromServer(server);
                if (child != null)
                    yield return child;
            }
        }

        private static LoadedPlugin TryCreateLoadedPluginFromServer(object server)
        {
            if (server == null)
                return null;

            try
            {
                var serverType = server.GetType();
                var plugin = serverType
                    .GetField("plugin", BindingFlags.NonPublic | BindingFlags.Instance)
                    ?.GetValue(server) as IPlugin;
                if (plugin == null)
                    return null;

                var pluginId = serverType
                    .GetProperty("Id", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(server) as string;
                var displayName = serverType
                    .GetProperty("FriendlyName", BindingFlags.Public | BindingFlags.Instance)
                    ?.GetValue(server) as string;

                return LoadedPlugin.FromPlugin(plugin, pluginId, displayName);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[Quasar.Agent] Failed reading Pulsar plugin server: {exception.Message}");
                return null;
            }
        }

        private sealed class LoadedPlugin
        {
            public IPlugin Plugin { get; private set; }

            public string PluginId { get; private set; }

            public string DisplayName { get; private set; }

            public static LoadedPlugin FromPlugin(IPlugin plugin, string pluginId = null, string displayName = null)
            {
                var assemblyName = plugin.GetType().Assembly.GetName().Name ?? plugin.GetType().Name;
                return new LoadedPlugin
                {
                    Plugin = plugin,
                    PluginId = string.IsNullOrWhiteSpace(pluginId) ? assemblyName : pluginId.Trim(),
                    DisplayName = string.IsNullOrWhiteSpace(displayName) ? assemblyName : displayName.Trim(),
                };
            }
        }

        private sealed class ConfigProviderAdapter
        {
            private static readonly MethodInfo SaveJsonMethod = typeof(ConfigStorage)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == nameof(ConfigStorage.SaveJson)
                                  && method.IsGenericMethodDefinition);

            private static readonly MethodInfo LoadJsonMethod = typeof(ConfigStorage)
                .GetMethods(BindingFlags.Public | BindingFlags.Static)
                .Single(method => method.Name == nameof(ConfigStorage.LoadJson)
                                  && method.IsGenericMethodDefinition);

            private readonly IQuasarConfigProvider _explicitProvider;
            private readonly PluginConfig _sdkConfig;

            private ConfigProviderAdapter(
                string pluginId,
                string displayName,
                IQuasarConfigProvider explicitProvider,
                PluginConfig sdkConfig)
            {
                PluginId = pluginId;
                DisplayName = displayName;
                _explicitProvider = explicitProvider;
                _sdkConfig = sdkConfig;
            }

            public string PluginId { get; }
            public string DisplayName { get; }

            public static ConfigProviderAdapter ForExplicit(LoadedPlugin loaded, IQuasarConfigProvider provider)
            {
                return new ConfigProviderAdapter(
                    string.IsNullOrWhiteSpace(provider.PluginId) ? loaded.PluginId : provider.PluginId,
                    loaded.DisplayName,
                    provider,
                    null);
            }

            public static ConfigProviderAdapter TryCreateForSdkConfig(LoadedPlugin loaded)
            {
                var config = GetSdkConfig(loaded.Plugin);
                if (config == null)
                    return null;

                return new ConfigProviderAdapter(
                    loaded.PluginId,
                    loaded.DisplayName,
                    null,
                    config);
            }

            public string GetConfigJson()
            {
                if (_explicitProvider != null)
                    return _explicitProvider.GetConfigJson();

                return (string)SaveJsonMethod
                    .MakeGenericMethod(_sdkConfig.GetType())
                    .Invoke(null, new object[] { _sdkConfig });
            }

            public void ApplyConfigJson(string json)
            {
                if (_explicitProvider != null)
                {
                    _explicitProvider.ApplyConfigJson(json);
                    return;
                }

                var updated = (PluginConfig)LoadJsonMethod
                    .MakeGenericMethod(_sdkConfig.GetType())
                    .Invoke(null, new object[] { json ?? string.Empty });

                foreach (var property in GetOptionProperties(_sdkConfig.GetType()))
                    property.SetValue(_sdkConfig, property.GetValue(updated));
            }

            private static PluginConfig GetSdkConfig(IPlugin plugin)
            {
                return plugin.GetType()
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(property => property.CanRead
                                       && property.GetIndexParameters().Length == 0
                                       && typeof(PluginConfig).IsAssignableFrom(property.PropertyType))
                    .OrderByDescending(property => string.Equals(property.Name, "PluginConfig", StringComparison.OrdinalIgnoreCase))
                    .ThenBy(property => property.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(property => property.GetValue(plugin) as PluginConfig)
                    .FirstOrDefault(config => config != null);
            }

            private static IEnumerable<PropertyInfo> GetOptionProperties(Type configType)
            {
                return configType
                    .GetProperties(BindingFlags.Public | BindingFlags.Instance)
                    .Where(property => property.CanRead
                                       && property.CanWrite
                                       && property.GetCustomAttribute<ConfigOptionAttribute>(inherit: true) != null);
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

        // Mirrors the game's own kick-cooldown bookkeeping (see MyKickedPlayersController in
        // VRage.Dedicated): the server keeps kicked SteamIds in MyMultiplayer.Static.KickedClients
        // mapped to the game-time they were kicked, and clears them KICK_TIMEOUT_MS later.
        private List<KickedPlayerSnapshot> GetKickedPlayers(MySession session)
        {
            var result = new List<KickedPlayerSnapshot>();
            var multiplayer = MyMultiplayer.Static;
            if (session == null || !session.Ready || multiplayer == null)
                return result;

            try
            {
                var now = MySandboxGame.TotalTimeInMilliseconds;
                foreach (var kicked in multiplayer.KickedClients)
                {
                    var remaining = kicked.Value + MyMultiplayerBase.KICK_TIMEOUT_MS - now;
                    if (remaining <= 0)
                        continue;

                    result.Add(new KickedPlayerSnapshot
                    {
                        SteamId = (long)kicked.Key,
                        DisplayName = session.Players.TryGetIdentityNameFromSteamId(kicked.Key) ?? string.Empty,
                        RemainingCooldownMs = remaining,
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
                    _quasarRequestedStop = true;
                    SaveWorldIfReady();
                    MySandboxGame.ExitThreadSafe();
                    return CreateResult(command, true, "World save and server shutdown requested.");

                case ServerCommandType.KickPlayer:
                    MyMultiplayer.Static?.KickClient((ulong)(command.SteamId ?? 0));
                    return CreateResult(command, true, $"Kick requested for {command.SteamId}.");

                case ServerCommandType.ClearKickCooldown:
                    MyMultiplayer.Static?.KickClient((ulong)(command.SteamId ?? 0), kicked: false);
                    return CreateResult(command, true, $"Kick cooldown cleared for {command.SteamId}.");

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
