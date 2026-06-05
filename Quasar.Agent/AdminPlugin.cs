using System;
using Magnetar.Protocol.Model;
using PluginSdk;
using Sandbox.Game;
using Sandbox.Game.World;
using VRage.Plugins;

namespace Quasar.Agent
{
    public class AdminPlugin : IPlugin
    {
        private GameBridge _bridge;
        private AgentConnection _connection;
        private PluginLogOutbox _outbox;

        public void Init(object gameServer)
        {
            _bridge = new GameBridge(gameServer);

            // Start capturing plugin log lines before the connection loop so any
            // emitted during startup are buffered and shipped once connected.
            _outbox = new PluginLogOutbox();
            _outbox.Start();

            _connection = new AgentConnection(_bridge, new WebServiceLocator(), AgentOptions.FromEnvironment(), _outbox);
            _connection.Start();
            MyVisualScriptLogicProvider.PlayerDied += OnPlayerDied;
            ServerControl.Terminating += OnServerTerminating;
        }

        public void Update()
        {
            _bridge?.Update();
        }

        public void Dispose()
        {
            ServerControl.Terminating -= OnServerTerminating;
            MyVisualScriptLogicProvider.PlayerDied -= OnPlayerDied;
            _connection?.Stop();
            _connection = null;
            _outbox?.Dispose();
            _outbox = null;
            _bridge = null;
        }

        // The Magnetar host is tearing the server down. When the intent is a
        // shutdown (not a restart) and Quasar did not request it, an admin issued
        // an in-game stop (e.g. !quit). Tell Quasar so it flips the goal state to
        // Off and does not restart the server against the admin's intent. A
        // restart is left alone: the server is meant to come back.
        private void OnServerTerminating(ServerTerminationKind kind)
        {
            if (kind == ServerTerminationKind.Shutdown &&
                _bridge != null &&
                !_bridge.QuasarRequestedStop)
            {
                _connection?.TrySendAdminStop();
            }
        }

        private void OnPlayerDied(long identityId)
        {
            var session = MySession.Static;
            var identity = session?.Players?.TryGetIdentity(identityId);
            var victimName = identity?.DisplayName ?? identityId.ToString();

            _bridge?.RecordDeath(new DeathEventSnapshot
            {
                VictimName = victimName,
                KillerName = null,
                WeaponName = null,
                DeathType = "Accident",
                TimestampTicksUtc = DateTime.UtcNow.Ticks,
            });
        }
    }
}
