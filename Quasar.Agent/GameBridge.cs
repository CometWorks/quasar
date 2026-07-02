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
using System.Xml.Linq;
using Magnetar.Protocol.Bridge;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Newtonsoft.Json;
using Newtonsoft.Json.Serialization;
using PluginSdk;
using PluginSdk.Commands;
using PluginSdk.Config;
using Sandbox;
using Sandbox.Engine.Networking;
using Sandbox.Engine.Multiplayer;
using Sandbox.Game.Entities;
using Sandbox.Game.Gui;
using Sandbox.Game.Multiplayer;
using Sandbox.Game.Screens.Helpers;
using Sandbox.Game.World;
using VRage.Game;
using VRage.Game.ModAPI;
using VRage.Plugins;
using VRage.Voxels;

namespace Quasar.Agent
{
    public class GameBridge : IDisposable
    {
        private static readonly TimeSpan SnapshotInterval = TimeSpan.FromSeconds(1);
        private const string ServerChatAuthorName = "Server";
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
        private readonly SemaphoreSlim _viewerSceneBuildGate = new SemaphoreSlim(1, 1);
        private readonly object _saveSync = new object();
        private string _worldSavePath = string.Empty;
        private bool _worldSaveStateLoaded;
        private bool _lastSaveInProgress;
        private DateTimeOffset? _lastWorldSaveUtc;
        private long? _lastWorldSaveElapsedGameTicks;
        private long _lastWorkingSetBytes;
        private TimeSpan _lastProcessCpuTime;
        private DateTime _lastProcessCpuSampleUtc = DateTime.MinValue;
        private float _lastProcessCpuLoadPercent;
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
            _pluginVersion = GetAgentVersion();
            MySession.OnSaved += OnWorldSaved;
        }

        public void Dispose()
        {
            MySession.OnSaved -= OnWorldSaved;
        }

        private static string GetAgentVersion()
        {
            var assembly = typeof(AdminPlugin).Assembly;
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                ?? string.Empty;
        }

        public void Update()
        {
            AgentProfiler.MarkGameThread();
            AgentProfiler.Update();

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
                if (command.CommandType == ServerCommandType.SetProfilerMode)
                    return Task.FromResult(SetProfilerMode(command));

                if (command.CommandType == ServerCommandType.StopServer)
                {
                    _quasarRequestedStop = true;
                    ServerControl.SaveAndQuit();
                    return Task.FromResult(CreateResult(command, true, "Server shutdown requested."));
                }

                return Task.FromResult(CreateResult(command, false, "Game server not available."));
            }

            if (command.CommandType == ServerCommandType.GetEntityRenderScene)
                return ExecuteViewerCommandOffGameThreadAsync(command, cancellationToken);

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

        private async Task<ServerCommandResult> ExecuteViewerCommandOffGameThreadAsync(ServerCommandEnvelope command, CancellationToken cancellationToken)
        {
            await _viewerSceneBuildGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                var work = await ResolveViewerSceneWorkOnGameThreadAsync(command, cancellationToken).ConfigureAwait(false);
                if (work.Result != null)
                    return work.Result;

                return await Task.Run(() =>
                {
                    try
                    {
                        return BuildResolvedViewerScene(command, work);
                    }
                    catch (Exception exception)
                    {
                        return CreateResult(command, false, exception.Message);
                    }
                }, cancellationToken).ConfigureAwait(false);
            }
            finally
            {
                _viewerSceneBuildGate.Release();
            }
        }

        private Task<ViewerSceneWork> ResolveViewerSceneWorkOnGameThreadAsync(ServerCommandEnvelope command, CancellationToken cancellationToken)
        {
            var completion = new TaskCompletionSource<ViewerSceneWork>();
            var game = MySandboxGame.Static;
            if (game == null)
                return Task.FromResult(new ViewerSceneWork { Result = CreateResult(command, false, "Game server not available.") });

            cancellationToken.Register(() => completion.TrySetCanceled());
            game.Invoke(() =>
            {
                try
                {
                    completion.TrySetResult(ResolveViewerSceneWorkOnGameThread(command));
                }
                catch (Exception exception)
                {
                    completion.TrySetResult(new ViewerSceneWork { Result = CreateResult(command, false, exception.Message) });
                }
            }, $"Quasar.Agent:{command.CommandType}:Resolve");

            return completion.Task;
        }

        private ViewerSceneWork ResolveViewerSceneWorkOnGameThread(ServerCommandEnvelope command)
        {
            if (MySession.Static == null || !MySession.Static.Ready)
                return new ViewerSceneWork { Result = CreateResult(command, false, "Session not ready.") };

            var request = DeserializePayload<EntityRenderSceneRequest>(command.Payload);
            if (request == null || request.EntityId == 0)
                return new ViewerSceneWork { Result = CreateResult(command, false, "Viewer scene request is missing an entity id.") };

            var work = new ViewerSceneWork
            {
                Request = request,
                GameVersion = MySession.Static?.AppVersionFromSave.ToString() ?? string.Empty,
            };

            if (MyEntities.TryGetEntityById<MyCubeGrid>(request.EntityId, out var grid) && grid != null && !grid.MarkedForClose && !grid.Closed)
                work.Grid = grid;
            else if (MyEntities.TryGetEntityById<MyVoxelBase>(request.EntityId, out var voxel) && voxel != null && !voxel.MarkedForClose && !voxel.Closed)
                work.Voxel = voxel;
            else
                work.Result = CreateResult(command, false, "Viewer entity not found or not loaded on this server.");

            return work;
        }

        private ServerCommandResult BuildResolvedViewerScene(ServerCommandEnvelope command, ViewerSceneWork work)
        {
            EntityRenderScene scene;
            if (work.Grid != null)
                scene = GridRenderSceneInspector.Build(work.Grid, work.GameVersion, _pluginVersion, work.Request.IncludeVoxels, work.Request.IncludeContext);
            else if (work.Voxel != null)
                scene = GridRenderSceneInspector.BuildVoxel(work.Voxel, work.GameVersion, _pluginVersion, work.Request.IncludeVoxels);
            else
                return CreateResult(command, false, "Viewer entity not found or not loaded on this server.");

            return CreateResult(command, true, "Viewer scene snapshot captured.", SerializePayload(scene));
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
                ProfilerMode = AgentProfiler.Mode.ToString(),
                Profiler = AgentProfiler.GetLatestSnapshot(),
                Players = GetPlayers(session),
                HiddenPlayerSteamIds = GetHiddenPlayerSteamIds(session),
                HiddenPlayerIdentityIds = GetHiddenPlayerIdentityIds(session),
                KickedPlayers = GetKickedPlayers(session),
                RecentChat = GetRecentChat(),
                ChatCommands = GetChatCommands(),
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
            var processCpuLoadPercent = GetProcessCpuLoadPercent(process);
            _lastWorkingSetBytes = process.WorkingSet64;

            if (session == null)
            {
                return new ServerMetrics
                {
                    ServerCpuLoadPercent = processCpuLoadPercent,
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

            var isSaveInProgress = session.IsSaveInProgress || MyAsyncSaving.InProgress;
            var worldSaveTelemetry = GetWorldSaveTelemetry(session, isSaveInProgress);
            int? activeGridCount = null;
            int? activeEntityCount = null;
            int? totalBlockCount = null;
            int? floatingObjectCount = null;
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
                        totalBlockCount = grids.Sum(grid => Math.Max(0, grid.BlocksCount));
                        gridPcu = grids.Sum(grid => Math.Max(0, grid.BlocksPCU));
                        floatingObjectCount = entities.OfType<MyFloatingObject>().Count();
                    }
                    else
                    {
                        activeGridCount = 0;
                        totalBlockCount = 0;
                        floatingObjectCount = 0;
                    }
                }
                catch
                {
                    activeGridCount = null;
                    activeEntityCount = null;
                    totalBlockCount = null;
                    floatingObjectCount = null;
                }
            }

            return new ServerMetrics
            {
                PlayersOnline = GetOnlinePlayerCount(session),
                MaxPlayers = session.Settings.MaxPlayers,
                SimulationFrameCounter = MySandboxGame.Static?.SimulationFrameCounter ?? 0,
                SimSpeed = Sync.ServerSimulationRatio,
                SimCpuLoadPercent = (float)Math.Round(Sync.ServerCPULoad, 1),
                ServerCpuLoadPercent = processCpuLoadPercent,
                IsSaveInProgress = isSaveInProgress,
                LastWorldSaveUtc = worldSaveTelemetry.LastWorldSaveUtc,
                UnsavedGameTimeSeconds = worldSaveTelemetry.UnsavedGameTimeSeconds,
                UsedPcu = usedPcu > 0 ? usedPcu : gridPcu,
                TotalPcu = session.Settings.TotalPCU,
                MemoryWorkingSetMb = _lastWorkingSetBytes >> 20,
                ActiveGridCount = activeGridCount,
                ActiveEntityCount = activeEntityCount,
                TotalBlockCount = totalBlockCount,
                FloatingObjectCount = floatingObjectCount,
                UptimeSeconds = (int)_uptime.Elapsed.TotalSeconds,
                ModsLoaded = session.Mods?.Count ?? 0,
                PluginsLoaded = GetPlugins().Count,
            };
        }

        private void OnWorldSaved(bool success, string sessionPath)
        {
            if (!success)
                return;

            var session = MySession.Static;
            var normalizedPath = NormalizeSessionPath(!string.IsNullOrWhiteSpace(sessionPath)
                ? sessionPath
                : session?.CurrentPath);
            long? elapsedGameTicks = session == null ? (long?)null : session.ElapsedGameTime.Ticks;

            lock (_saveSync)
            {
                _worldSavePath = normalizedPath;
                _worldSaveStateLoaded = true;
                _lastWorldSaveUtc = DateTimeOffset.UtcNow;
                _lastWorldSaveElapsedGameTicks = elapsedGameTicks;
            }
        }

        private WorldSaveTelemetry GetWorldSaveTelemetry(MySession session, bool isSaveInProgress)
        {
            var sessionPath = NormalizeSessionPath(session?.CurrentPath);
            ObserveSaveProgress(sessionPath, isSaveInProgress);
            EnsureWorldSaveStateLoaded(sessionPath);

            DateTimeOffset? lastWorldSaveUtc;
            long? lastWorldSaveElapsedGameTicks;
            lock (_saveSync)
            {
                lastWorldSaveUtc = _lastWorldSaveUtc;
                lastWorldSaveElapsedGameTicks = _lastWorldSaveElapsedGameTicks;
            }

            long? unsavedGameTimeSeconds = null;
            if (session != null && lastWorldSaveElapsedGameTicks.HasValue)
            {
                var unsavedTicks = session.ElapsedGameTime.Ticks - lastWorldSaveElapsedGameTicks.Value;
                unsavedGameTimeSeconds = Math.Max(0, unsavedTicks / TimeSpan.TicksPerSecond);
            }

            return new WorldSaveTelemetry
            {
                LastWorldSaveUtc = lastWorldSaveUtc,
                UnsavedGameTimeSeconds = unsavedGameTimeSeconds,
            };
        }

        private void ObserveSaveProgress(string sessionPath, bool isSaveInProgress)
        {
            lock (_saveSync)
            {
                if (_lastSaveInProgress && !isSaveInProgress && !string.IsNullOrWhiteSpace(sessionPath))
                {
                    _worldSaveStateLoaded = false;
                    _lastWorldSaveUtc = null;
                    _lastWorldSaveElapsedGameTicks = null;
                }

                _lastSaveInProgress = isSaveInProgress;
            }
        }

        private void EnsureWorldSaveStateLoaded(string sessionPath)
        {
            var shouldLoad = false;
            lock (_saveSync)
            {
                if (!string.Equals(_worldSavePath, sessionPath, StringComparison.Ordinal))
                {
                    _worldSavePath = sessionPath;
                    _worldSaveStateLoaded = false;
                    _lastWorldSaveUtc = null;
                    _lastWorldSaveElapsedGameTicks = null;
                }

                shouldLoad = !_worldSaveStateLoaded && !string.IsNullOrWhiteSpace(sessionPath);
            }

            if (!shouldLoad)
                return;

            var checkpoint = TryLoadWorldSaveCheckpoint(sessionPath);
            lock (_saveSync)
            {
                if (!string.Equals(_worldSavePath, sessionPath, StringComparison.Ordinal))
                    return;

                _worldSaveStateLoaded = true;
                _lastWorldSaveUtc = checkpoint?.LastWorldSaveUtc;
                _lastWorldSaveElapsedGameTicks = checkpoint?.LastWorldSaveElapsedGameTicks;
            }
        }

        private static WorldSaveCheckpoint TryLoadWorldSaveCheckpoint(string sessionPath)
        {
            try
            {
                var checkpoint = MyLocalCache.LoadCheckpoint(sessionPath, out _);
                var lastWorldSaveUtc = NormalizeSaveTime(checkpoint?.LastSaveTime ?? default);

                return new WorldSaveCheckpoint
                {
                    LastWorldSaveUtc = lastWorldSaveUtc,
                    LastWorldSaveElapsedGameTicks = lastWorldSaveUtc.HasValue
                        ? checkpoint?.ElapsedGameTime
                        : null,
                };
            }
            catch
            {
                return null;
            }
        }

        private static DateTimeOffset? NormalizeSaveTime(DateTime saveTime)
        {
            if (saveTime == default)
                return null;

            if (saveTime.Kind == DateTimeKind.Unspecified)
                saveTime = DateTime.SpecifyKind(saveTime, DateTimeKind.Local);

            return new DateTimeOffset(saveTime).ToUniversalTime();
        }

        private static string NormalizeSessionPath(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
                return string.Empty;

            var trimmed = path.Trim();
            if (trimmed.EndsWith(".sbc", StringComparison.OrdinalIgnoreCase))
                trimmed = Path.GetDirectoryName(trimmed) ?? string.Empty;

            if (string.IsNullOrWhiteSpace(trimmed))
                return string.Empty;

            try
            {
                return Path.GetFullPath(trimmed);
            }
            catch
            {
                return trimmed;
            }
        }

        private float GetProcessCpuLoadPercent(Process process)
        {
            var now = DateTime.UtcNow;
            var processCpuTime = process.TotalProcessorTime;

            if (_lastProcessCpuSampleUtc == DateTime.MinValue)
            {
                _lastProcessCpuSampleUtc = now;
                _lastProcessCpuTime = processCpuTime;
                return _lastProcessCpuLoadPercent;
            }

            var elapsedMs = (now - _lastProcessCpuSampleUtc).TotalMilliseconds;
            var cpuMs = (processCpuTime - _lastProcessCpuTime).TotalMilliseconds;
            _lastProcessCpuSampleUtc = now;
            _lastProcessCpuTime = processCpuTime;

            if (elapsedMs <= 0 || cpuMs < 0)
                return _lastProcessCpuLoadPercent;

            var cpuPercent = cpuMs / elapsedMs * 100d;
            if (double.IsNaN(cpuPercent) || double.IsInfinity(cpuPercent))
                return _lastProcessCpuLoadPercent;

            _lastProcessCpuLoadPercent = (float)Math.Round(Math.Max(0d, cpuPercent), 1);
            return _lastProcessCpuLoadPercent;
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
                    if (!IsHumanPlayer(session, player))
                        continue;

                    var steamId = (long)player.Id.SteamId;
                    var identityId = player.Identity?.IdentityId ?? 0;

                    result.Add(new PlayerSnapshot
                    {
                        SteamId = steamId,
                        IdentityId = identityId,
                        SerialId = player.Id.SerialId,
                        DisplayName = player.DisplayName ?? string.Empty,
                        PlatformDisplayName = player.PlatformDisplayName ?? string.Empty,
                        PlatformIcon = player.PlatformIcon ?? string.Empty,
                        GameAcronym = player.GameAcronym ?? string.Empty,
                        ServiceName = GetPlayerServiceName(player.Id.SteamId),
                        FactionTag = GetPlayerFaction(session, identityId),
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

        private static bool IsHumanPlayer(MySession session, MyPlayer player)
        {
            if (player == null || player.Id.SteamId == 0 || player.IsBot)
                return false;

            var identityId = player.Identity?.IdentityId ?? 0;
            return !IsNpcIdentity(session, identityId);
        }

        private static bool IsNpcIdentity(MySession session, long identityId)
        {
            return identityId != 0 &&
                   session?.Players != null &&
                   session.Players.IdentityIsNpc(identityId);
        }

        private static List<long> GetHiddenPlayerSteamIds(MySession session) =>
            GetHiddenPlayerIds(session, player => (long)player.Id.SteamId, id => id > 0);

        private static List<long> GetHiddenPlayerIdentityIds(MySession session) =>
            GetHiddenPlayerIds(session, player => player.Identity?.IdentityId ?? 0, id => id != 0);

        private static List<long> GetHiddenPlayerIds(MySession session, Func<MyPlayer, long> selectId, Predicate<long> shouldInclude)
        {
            var result = new HashSet<long>();
            if (session == null || !session.Ready)
                return result.ToList();

            try
            {
                foreach (var player in session.Players.GetOnlinePlayers())
                {
                    if (IsHumanPlayer(session, player))
                        continue;

                    var id = selectId(player);
                    if (shouldInclude(id))
                        result.Add(id);
                }
            }
            catch
            {
            }

            return result.ToList();
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
                    var steamId = (long)message.SteamId;
                    var authorName = message.AuthorName ?? string.Empty;
                    var isServerMessage = IsServerChatMessage(steamId, authorName);
                    result.Add(new ChatMessageSnapshot
                    {
                        SteamId = steamId,
                        AuthorName = isServerMessage ? ServerChatAuthorName : authorName,
                        Content = message.Text ?? string.Empty,
                        TimestampTicksUtc = message.Timestamp.Ticks,
                        IsServerMessage = isServerMessage,
                    });
                }
            }
            catch
            {
            }

            return result;
        }

        private static bool IsServerChatMessage(long steamId, string authorName)
        {
            if (steamId <= 0)
                return true;

            var normalizedAuthor = (authorName ?? string.Empty).Trim();
            return string.Equals(normalizedAuthor, "Good.bot", StringComparison.OrdinalIgnoreCase) ||
                   string.Equals(normalizedAuthor, ServerChatAuthorName, StringComparison.OrdinalIgnoreCase);
        }

        private List<PluginRuntimeInfo> GetPlugins()
        {
            var result = new List<PluginRuntimeInfo>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var loaded in EnumeratePlugins())
            {
                AddPluginKeys(seen, loaded.PluginId);
                AddPluginKeys(seen, loaded.DisplayName);

                result.Add(new PluginRuntimeInfo
                {
                    PluginId = loaded.PluginId,
                    DisplayName = loaded.DisplayName,
                    Version = GetPluginVersion(loaded.Plugin),
                    IsLoaded = true,
                });
            }

            var pluginPaths = MySandboxGame.ConfigDedicated?.Plugins;
            if (pluginPaths == null)
                return result;

            foreach (var pluginPath in pluginPaths)
            {
                var declared = ResolveDeclaredPlugin(pluginPath);
                if (string.IsNullOrWhiteSpace(declared.PluginId) && string.IsNullOrWhiteSpace(declared.DisplayName))
                    continue;

                if (HasAnyPluginKey(seen, declared.Keys))
                    continue;

                result.Add(new PluginRuntimeInfo
                {
                    PluginId = declared.PluginId,
                    DisplayName = declared.DisplayName,
                    Version = string.Empty,
                    IsLoaded = false,
                });
            }

            return result;
        }

        private List<ChatCommandSnapshot> GetChatCommands()
        {
            var result = new List<ChatCommandSnapshot>();

            try
            {
                object registrar = ServerCommands.Registrar;
                if (registrar == null)
                    return result;

                object registry = GetFieldValue(registrar, "registry");
                if (registry == null)
                    return result;

                foreach (object root in EnumerateCommandRoots(registry))
                {
                    var prefix = GetStringProperty(root, "Prefix");
                    var title = GetStringProperty(root, "Title");
                    var ownerId = GetStringProperty(root, "OwnerId");

                    AddChatCommandSnapshot(result, GetPropertyValue(root, "Default"), prefix, title, ownerId);

                    var enumerateCommands = root.GetType().GetMethod(
                        "EnumerateCommands",
                        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
                    if (enumerateCommands?.Invoke(root, null) is IEnumerable commands)
                    {
                        foreach (object command in commands)
                            AddChatCommandSnapshot(result, command, prefix, title, ownerId);
                    }
                }
            }
            catch
            {
            }

            return result
                .Where(command => !string.IsNullOrWhiteSpace(command.Text))
                .GroupBy(command => command.Text, StringComparer.OrdinalIgnoreCase)
                .Select(group => group.First())
                .OrderBy(command => command.Text, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private static IEnumerable<object> EnumerateCommandRoots(object registry)
        {
            object roots = GetFieldValue(registry, "roots");
            if (roots is IDictionary dictionary)
            {
                foreach (object value in dictionary.Values)
                    yield return value;
            }
        }

        private static void AddChatCommandSnapshot(
            List<ChatCommandSnapshot> result,
            object command,
            string fallbackPrefix,
            string title,
            string fallbackOwnerId)
        {
            if (command == null)
                return;

            var prefix = GetStringProperty(command, "Prefix");
            if (string.IsNullOrWhiteSpace(prefix))
                prefix = fallbackPrefix;

            if (string.IsNullOrWhiteSpace(prefix))
                return;

            var segments = GetStringListProperty(command, "Path");
            var path = string.Join(" ", segments);
            var text = "!" + prefix + (path.Length == 0 ? string.Empty : " " + path);

            result.Add(new ChatCommandSnapshot
            {
                Text = text,
                Syntax = GetStringProperty(command, "Syntax"),
                Prefix = prefix,
                Path = path,
                Description = GetStringProperty(command, "Description"),
                HelpText = GetStringProperty(command, "HelpText"),
                Title = title,
                OwnerId = FirstNonEmpty(GetStringProperty(command, "OwnerId"), fallbackOwnerId),
                MinimumPromoteLevel = GetPropertyValue(command, "MinPromoteLevel")?.ToString() ?? string.Empty,
                PathSegments = segments,
            });
        }

        private static object GetFieldValue(object instance, string fieldName)
        {
            return instance.GetType()
                .GetField(fieldName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(instance);
        }

        private static object GetPropertyValue(object instance, string propertyName)
        {
            return instance.GetType()
                .GetProperty(propertyName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic)
                ?.GetValue(instance, null);
        }

        private static string GetStringProperty(object instance, string propertyName)
        {
            return GetPropertyValue(instance, propertyName) as string ?? string.Empty;
        }

        private static List<string> GetStringListProperty(object instance, string propertyName)
        {
            var result = new List<string>();
            if (GetPropertyValue(instance, propertyName) is not IEnumerable values)
                return result;

            foreach (object value in values)
            {
                if (value is string text && !string.IsNullOrWhiteSpace(text))
                    result.Add(text);
            }

            return result;
        }

        private static string GetPluginVersion(IPlugin plugin)
        {
            try
            {
                var assembly = plugin.GetType().Assembly;
                return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                    ?? assembly.GetName().Version?.ToString()
                    ?? string.Empty;
            }
            catch
            {
                return string.Empty;
            }
        }

        private static DeclaredPlugin ResolveDeclaredPlugin(string pluginPath)
        {
            var trimmed = pluginPath == null ? string.Empty : pluginPath.Trim();
            var fileStem = GetFileNameWithoutExtension(trimmed);
            var parentName = GetParentDirectoryName(trimmed);
            var keys = new List<string>();
            AddPluginKeys(keys, trimmed);

            var manifestId = string.Empty;
            var friendlyName = string.Empty;
            if (string.Equals(GetExtension(trimmed), ".xml", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(trimmed))
            {
                try
                {
                    var root = XDocument.Load(trimmed, LoadOptions.None).Root;
                    if (root != null)
                    {
                        manifestId = GetXmlValue(root, "Id");
                        friendlyName = GetXmlValue(root, "FriendlyName");
                        AddPluginKeys(keys, manifestId);
                        AddPluginKeys(keys, friendlyName);
                    }
                }
                catch
                {
                }
            }

            return new DeclaredPlugin
            {
                PluginId = FirstNonEmpty(manifestId, trimmed),
                DisplayName = FirstNonEmpty(friendlyName, fileStem, parentName, manifestId, trimmed),
                Keys = keys
                    .Where(value => !string.IsNullOrWhiteSpace(value))
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }

        private static string GetXmlValue(XElement root, string name)
        {
            var value = root.Element(name)?.Value;
            return string.IsNullOrWhiteSpace(value) ? string.Empty : value.Trim();
        }

        private static bool HasAnyPluginKey(HashSet<string> seen, IEnumerable<string> values)
        {
            foreach (var value in values)
            {
                if (seen.Contains(value))
                    return true;
            }

            return false;
        }

        private static void AddPluginKeys(HashSet<string> seen, string value)
        {
            var keys = new List<string>();
            AddPluginKeys(keys, value);
            foreach (var key in keys)
                seen.Add(key);
        }

        private static void AddPluginKeys(List<string> keys, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return;

            var trimmed = value.Trim();
            keys.Add(trimmed);

            var fileName = GetFileNameWithoutExtension(trimmed);
            if (!string.IsNullOrWhiteSpace(fileName))
                keys.Add(fileName);

            var parentName = GetParentDirectoryName(trimmed);
            if (!string.IsNullOrWhiteSpace(parentName))
                keys.Add(parentName);
        }

        private static string GetFileNameWithoutExtension(string value)
        {
            var fileName = GetFileName(value);
            var extension = GetExtension(fileName);
            return string.IsNullOrEmpty(extension)
                ? fileName
                : fileName.Substring(0, fileName.Length - extension.Length);
        }

        private static string GetExtension(string value)
        {
            var fileName = GetFileName(value);
            return string.IsNullOrWhiteSpace(fileName) ? string.Empty : Path.GetExtension(fileName);
        }

        private static string GetFileName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Trim().Replace('\\', '/').TrimEnd('/');
            var slash = normalized.LastIndexOf('/');
            return slash >= 0 ? normalized.Substring(slash + 1) : normalized;
        }

        private static string GetParentDirectoryName(string value)
        {
            if (string.IsNullOrWhiteSpace(value))
                return string.Empty;

            var normalized = value.Trim().Replace('\\', '/').TrimEnd('/');
            var slash = normalized.LastIndexOf('/');
            if (slash <= 0)
                return string.Empty;

            var parent = normalized.Substring(0, slash).TrimEnd('/');
            var parentSlash = parent.LastIndexOf('/');
            return parentSlash >= 0 ? parent.Substring(parentSlash + 1) : parent;
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value.Trim();
            }

            return string.Empty;
        }

        private sealed class DeclaredPlugin
        {
            public string PluginId { get; set; }

            public string DisplayName { get; set; }

            public List<string> Keys { get; set; }
        }

        private sealed class ViewerSceneWork
        {
            public EntityRenderSceneRequest Request { get; set; }

            public string GameVersion { get; set; }

            public MyCubeGrid Grid { get; set; }

            public MyVoxelBase Voxel { get; set; }

            public ServerCommandResult Result { get; set; }
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
                command.CommandType != ServerCommandType.SetProfilerMode &&
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
                    return SaveWorldIfReady()
                        ? CreateResult(command, true, "World saved.")
                        : CreateResult(command, false, "Session not ready.");

                case ServerCommandType.StopServer:
                    _quasarRequestedStop = true;
                    ServerControl.SaveAndQuit();
                    return CreateResult(command, true, "World save and server shutdown requested.");

                case ServerCommandType.SetProfilerMode:
                    return SetProfilerMode(command);

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

                case ServerCommandType.GetEntityRenderScene:
                    return GetEntityRenderScene(command);

                default:
                    return CreateResult(command, false, $"Unsupported command '{command.CommandType}'.");
            }
        }

        private ServerCommandResult SetProfilerMode(ServerCommandEnvelope command)
        {
            var rawMode = !string.IsNullOrWhiteSpace(command.Text)
                ? command.Text
                : command.Payload;
            if (!AgentOptions.TryParseProfilerMode(rawMode, out var mode))
                return CreateResult(command, false, $"Unknown profiler mode '{rawMode}'.");

            AgentProfilerPatches.Reconfigure(mode);
            RefreshSnapshotOnGameThread();
            return CreateResult(command, true, $"Profiler mode set to {mode}.", mode.ToString());
        }

        private static bool SaveWorldIfReady()
        {
            var session = MySession.Static;
            if (session == null || !session.Ready)
                return false;

            return ServerControl.SaveWorld();
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

        private ServerCommandResult GetEntityRenderScene(ServerCommandEnvelope command)
        {
            var request = DeserializePayload<EntityRenderSceneRequest>(command.Payload);
            if (request == null || request.EntityId == 0)
                return CreateResult(command, false, "Viewer scene request is missing an entity id.");

            var gameVersion = MySession.Static?.AppVersionFromSave.ToString() ?? string.Empty;
            EntityRenderScene scene;
            if (MyEntities.TryGetEntityById<MyCubeGrid>(request.EntityId, out var grid) && grid != null && !grid.MarkedForClose && !grid.Closed)
                scene = GridRenderSceneInspector.Build(request.EntityId, gameVersion, _pluginVersion, request.IncludeVoxels, request.IncludeContext);
            else if (MyEntities.TryGetEntityById<MyVoxelBase>(request.EntityId, out var voxel) && voxel != null && !voxel.MarkedForClose && !voxel.Closed)
                scene = GridRenderSceneInspector.BuildVoxel(request.EntityId, gameVersion, _pluginVersion, request.IncludeVoxels);
            else
                return CreateResult(command, false, "Viewer entity not found or not loaded on this server.");
            return CreateResult(command, true, "Viewer scene snapshot captured.", SerializePayload(scene));
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
                return session?.Players?.GetOnlinePlayers()?.Count(player => IsHumanPlayer(session, player)) ?? 0;
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
            // ConfigDedicated.ServerName is the configured server name shown in the
            // server browser. Do NOT fall back to session?.Name here: that is the
            // loaded world/save name (which matches the world template), not the server.
            var serverName = MySandboxGame.ConfigDedicated?.ServerName;
            if (!string.IsNullOrWhiteSpace(serverName))
            {
                return serverName;
            }

            return $"Space Engineers {_processId}";
        }

        private string GetWorldName(MySession session)
        {
            return session?.Name
                   ?? MySandboxGame.ConfigDedicated?.WorldName
                   ?? "Unknown World";
        }

        private sealed class WorldSaveCheckpoint
        {
            public DateTimeOffset? LastWorldSaveUtc { get; set; }

            public long? LastWorldSaveElapsedGameTicks { get; set; }
        }

        private sealed class WorldSaveTelemetry
        {
            public DateTimeOffset? LastWorldSaveUtc { get; set; }

            public long? UnsavedGameTimeSeconds { get; set; }
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
