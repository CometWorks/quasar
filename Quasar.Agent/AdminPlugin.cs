using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using Magnetar.Protocol.Model;
using PluginSdk;
using PluginSdk.Clustering;
using PluginSdk.Commands;
using Sandbox.Game;
using Sandbox.Game.World;
using VRage.Game.ModAPI;
using VRage.Plugins;
using VRage.Utils;

namespace Quasar.Agent
{
    public class AdminPlugin : IPlugin
    {
        private static readonly TimeSpan DeathDedupWindow = TimeSpan.FromSeconds(3);
        private static readonly TimeSpan DeathSubscriptionRefreshInterval = TimeSpan.FromSeconds(1);
        private readonly object _deathSync = new object();
        private readonly Dictionary<long, CharacterDeathSubscription> _deathSubscriptionsByIdentityId = new Dictionary<long, CharacterDeathSubscription>();
        private readonly Dictionary<long, DateTime> _recentDeathsByIdentityId = new Dictionary<long, DateTime>();
        private GameBridge _bridge;
        private AgentConnection _connection;
        private PluginLogOutbox _outbox;
        private SupervisedLifecycle _lifecycle;
        private readonly object _adminStopSync = new object();
        private readonly object _adminRestartSync = new object();
        private bool _adminStopReported;
        private bool _adminRestartRequested;
        private bool _adminRestartReported;
        private bool _clusterMode;
        private DateTime _lastDeathSubscriptionRefreshUtc = DateTime.MinValue;

        public void Init(object gameServer)
        {
            var options = AgentOptions.FromEnvironment();
            _clusterMode = options.ClusterMode;
            LogStartupVersions();
            OfflineModeNetworkGuard.Apply();
            AgentProfiler.Configure(options);
            AgentProfilerPatches.Apply(options);
            ChatCapturePatch.Apply();
            if (!_clusterMode)
                ServerCommands.Register(typeof(AdminPlugin).Assembly, typeof(StopCommand), typeof(RestartCommand), typeof(QuitCommand));
            _bridge = new GameBridge(gameServer, options);

            // Start capturing plugin log lines before the connection loop so any
            // emitted during startup are buffered and shipped once connected.
            _outbox = new PluginLogOutbox();
            _outbox.Start();

            _connection = new AgentConnection(_bridge, new WebServiceLocator(), options, _outbox);
            if (!_clusterMode)
            {
                StopCommand.AdminStopRequested = ReportAdminStop;
                QuitCommand.AdminStopRequested = ReportAdminStop;
                RestartCommand.AdminRestartRequested = () => ReportAdminRestart();
                ServerControl.Terminating += OnServerTerminating;
                _lifecycle = new SupervisedLifecycle(ReportAdminRestart, ReportRequestedStop);
                if (!ClusterLifecycle.Register(_lifecycle))
                {
                    _lifecycle = null;
                    Console.WriteLine("[Quasar.Agent] Another lifecycle provider is registered; plugin restarts stay with Magnetar.");
                }
            }
            _connection.Start();
            MyVisualScriptLogicProvider.PlayerDied += OnPlayerDied;
        }

        public void Update()
        {
            if (!_clusterMode)
                RestartCommand.UpdateScheduledRestart();
            _bridge?.Update();
            RefreshDeathSubscriptions();
        }

        public void Dispose()
        {
            if (!_clusterMode)
                ServerControl.Terminating -= OnServerTerminating;
            if (_lifecycle != null)
                ClusterLifecycle.Unregister(_lifecycle);
            _lifecycle = null;
            StopCommand.AdminStopRequested = null;
            QuitCommand.AdminStopRequested = null;
            RestartCommand.AdminRestartRequested = null;
            RestartCommand.ClearScheduledRestart();
            MyVisualScriptLogicProvider.PlayerDied -= OnPlayerDied;
            UnsubscribeDeathHandlers();
            _connection?.Stop();
            _connection = null;
            _outbox?.Dispose();
            _outbox = null;
            _bridge?.Dispose();
            _bridge = null;
            ChatCapturePatch.Dispose();
            AgentProfilerPatches.Dispose();
            OfflineModeNetworkGuard.Dispose();
        }

        // The Magnetar host is tearing the server down. When the intent is a
        // shutdown (not a restart), someone in the server asked for it: an admin
        // (!quit, the DS's /stop), a plugin (the Essentials Stop step) or the
        // DS itself. A restart is left alone: the server is meant to come back.
        private void OnServerTerminating(ServerTerminationKind kind)
        {
            if (kind == ServerTerminationKind.Shutdown)
                ReportRequestedStop();
        }

        // Tell Quasar about a stop it did not ask for, so it flips the goal
        // state to Off and does not restart the server against that intent.
        // The stop that ends an in-game restart is not one, and neither is the
        // stop before the world is ready: that is the DS giving up a world it
        // can't load, which Quasar has to see as a failed start.
        private void ReportRequestedStop()
        {
            if (_bridge != null && !_bridge.QuasarRequestedStop && MySession.Static?.Ready == true)
                ReportAdminStop();
        }

        private void ReportAdminStop()
        {
            if (IsAdminRestartRequested())
                return;

            lock (_adminStopSync)
            {
                if (_adminStopReported)
                    return;

                _adminStopReported = _connection?.TrySendAdminStop() == true;
            }
        }

        private bool ReportAdminRestart()
        {
            lock (_adminRestartSync)
            {
                _adminRestartRequested = true;
                if (!_adminRestartReported)
                    _adminRestartReported = _connection?.TrySendAdminRestart() == true;
                return _adminRestartReported;
            }
        }

        private bool IsAdminRestartRequested()
        {
            lock (_adminRestartSync)
            {
                return _adminRestartRequested;
            }
        }

        private static void LogStartupVersions()
        {
            var magnetarVersion = ResolveMagnetarVersion();
            var agentVersion = GetAssemblyVersion(typeof(AdminPlugin).Assembly);
            var message = $"Quasar.Agent startup: Magnetar={magnetarVersion}; Quasar.Agent={agentVersion}.";

            try
            {
                MyLog.Default?.WriteLineAndConsole(message);
            }
            catch
            {
            }

            try
            {
                Console.WriteLine($"[Quasar.Agent] {message}");
            }
            catch
            {
            }
        }

        private static string ResolveMagnetarVersion()
        {
            var entryAssembly = Assembly.GetEntryAssembly();
            if (entryAssembly != null && IsMagnetarAssembly(entryAssembly))
                return GetAssemblyVersion(entryAssembly);

            var loadedMagnetarAssembly = AppDomain.CurrentDomain.GetAssemblies()
                .Where(IsMagnetarAssembly)
                .OrderBy(assembly => assembly.GetName().Name, StringComparer.OrdinalIgnoreCase)
                .FirstOrDefault();
            if (loadedMagnetarAssembly != null)
                return GetAssemblyVersion(loadedMagnetarAssembly);

            return GetMagnetarProcessVersion();
        }

        private static bool IsMagnetarAssembly(Assembly assembly)
        {
            var name = assembly.GetName().Name;
            return !string.IsNullOrWhiteSpace(name) &&
                   name.StartsWith("Magnetar", StringComparison.OrdinalIgnoreCase);
        }

        private static string GetAssemblyVersion(Assembly assembly)
        {
            return assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
                   ?? assembly.GetName().Version?.ToString()
                   ?? "unknown";
        }

        private static string GetMagnetarProcessVersion()
        {
            try
            {
                using (var process = Process.GetCurrentProcess())
                {
                    var fileName = process.MainModule?.FileName;
                    if (string.IsNullOrWhiteSpace(fileName) ||
                        !Path.GetFileName(fileName).StartsWith("Magnetar", StringComparison.OrdinalIgnoreCase))
                    {
                        return "unknown";
                    }

                    var version = FileVersionInfo.GetVersionInfo(fileName);
                    return FirstNonEmpty(version.ProductVersion, version.FileVersion, "unknown");
                }
            }
            catch
            {
                return "unknown";
            }
        }

        private static string FirstNonEmpty(params string[] values)
        {
            foreach (var value in values)
            {
                if (!string.IsNullOrWhiteSpace(value))
                    return value;
            }

            return string.Empty;
        }

        private void OnPlayerDied(long identityId)
        {
            // The game raises PlayerDied for wildlife agents too (wolves, spiders):
            // their identities are not registered as NPCs, so filter like the
            // CharacterDied path does.
            var session = MySession.Static;
            if (!IsRealPlayer(session, session?.Players?.TryGetPlayer(identityId)))
                return;

            RecordDeath(identityId, ResolveVictimName(identityId, null, null));
        }

        private void RefreshDeathSubscriptions()
        {
            var now = DateTime.UtcNow;
            if (now - _lastDeathSubscriptionRefreshUtc < DeathSubscriptionRefreshInterval)
                return;

            _lastDeathSubscriptionRefreshUtc = now;

            var session = MySession.Static;
            if (session == null || !session.Ready || session.Players == null)
                return;

            try
            {
                foreach (var player in session.Players.GetOnlinePlayers())
                {
                    if (!IsRealPlayer(session, player) || player.Character == null)
                        continue;

                    HookCharacterDeath(player.Character, player.Identity.IdentityId, player.DisplayName);
                }

                CleanupDeathDedup(now);
            }
            catch
            {
                // Death subscriptions are best-effort; snapshots and command handling
                // must keep running if the session mutates while we scan players.
            }
        }

        private static bool IsRealPlayer(MySession session, MyPlayer player)
        {
            if (player == null || player.IsBot || player.Id.SteamId == 0)
                return false;

            var identityId = player.Identity?.IdentityId ?? 0;
            return identityId != 0 && !session.Players.IdentityIsNpc(identityId);
        }

        private void HookCharacterDeath(IMyCharacter character, long identityId, string playerName)
        {
            if (character == null || identityId == 0)
                return;

            lock (_deathSync)
            {
                if (_deathSubscriptionsByIdentityId.TryGetValue(identityId, out var existing))
                {
                    if (existing.Character == character && existing.EntityId == character.EntityId)
                        return;

                    existing.Unsubscribe();
                }

                Action<IMyCharacter> handler = deadCharacter => OnCharacterDied(deadCharacter, identityId, playerName);
                character.CharacterDied += handler;
                _deathSubscriptionsByIdentityId[identityId] = new CharacterDeathSubscription(character, handler);
            }
        }

        private void OnCharacterDied(IMyCharacter deadCharacter, long identityId, string playerName)
        {
            RecordDeath(identityId, ResolveVictimName(identityId, playerName, deadCharacter));
        }

        private void RecordDeath(long identityId, string victimName)
        {
            if (_clusterMode)
            {
                var provider = PluginSdk.Clustering.PluginCluster.Current;
                var players = (provider as PluginSdk.Clustering.IPluginClusterViewProvider)?.OnlinePlayers();
                if (provider?.Context.Available != true || players == null || !players.Any(p =>
                    p.IdentityId == identityId && p.Node == provider.Context.Node)) return;
            }

            if (!TryMarkDeath(identityId))
                return;

            _bridge?.RecordDeath(new DeathEventSnapshot
            {
                VictimName = victimName,
                KillerName = null,
                WeaponName = null,
                DeathType = "Accident",
                TimestampTicksUtc = DateTime.UtcNow.Ticks,
            });
        }

        private bool TryMarkDeath(long identityId)
        {
            if (identityId == 0)
                return true;

            var now = DateTime.UtcNow;
            lock (_deathSync)
            {
                if (_recentDeathsByIdentityId.TryGetValue(identityId, out var previous) &&
                    now - previous < DeathDedupWindow)
                {
                    return false;
                }

                _recentDeathsByIdentityId[identityId] = now;
                return true;
            }
        }

        private void CleanupDeathDedup(DateTime now)
        {
            lock (_deathSync)
            {
                var expired = new List<long>();
                foreach (var entry in _recentDeathsByIdentityId)
                {
                    if (now - entry.Value > DeathDedupWindow)
                        expired.Add(entry.Key);
                }

                foreach (var identityId in expired)
                    _recentDeathsByIdentityId.Remove(identityId);
            }
        }

        private string ResolveVictimName(long identityId, string fallbackName, IMyCharacter character)
        {
            if (!string.IsNullOrWhiteSpace(character?.DisplayName))
                return character.DisplayName;

            var session = MySession.Static;
            var identity = session?.Players?.TryGetIdentity(identityId);
            if (!string.IsNullOrWhiteSpace(identity?.DisplayName))
                return identity.DisplayName;

            if (!string.IsNullOrWhiteSpace(fallbackName))
                return fallbackName;

            return identityId == 0 ? "Unknown" : identityId.ToString();
        }

        private void UnsubscribeDeathHandlers()
        {
            lock (_deathSync)
            {
                foreach (var subscription in _deathSubscriptionsByIdentityId.Values)
                    subscription.Unsubscribe();

                _deathSubscriptionsByIdentityId.Clear();
                _recentDeathsByIdentityId.Clear();
            }
        }

        private sealed class CharacterDeathSubscription
        {
            public CharacterDeathSubscription(IMyCharacter character, Action<IMyCharacter> handler)
            {
                Character = character;
                Handler = handler;
                EntityId = character.EntityId;
            }

            public IMyCharacter Character { get; }

            public long EntityId { get; }

            private Action<IMyCharacter> Handler { get; }

            public void Unsubscribe()
            {
                try
                {
                    Character.CharacterDied -= Handler;
                }
                catch
                {
                }
            }
        }
    }
}
