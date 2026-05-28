using System;
using Magnetar.Protocol.Model;
using Sandbox.Game;
using Sandbox.Game.World;
using VRage.Plugins;

namespace Quasar.Agent
{
    public class AdminPlugin : IPlugin
    {
        private GameBridge _bridge;
        private AgentConnection _connection;

        public void Init(object gameInstance)
        {
            _bridge = new GameBridge(gameInstance);
            _connection = new AgentConnection(_bridge, new WebServiceLocator());
            _connection.Start();
            MyVisualScriptLogicProvider.PlayerDied += OnPlayerDied;
        }

        public void Update()
        {
            _bridge?.Update();
        }

        public void Dispose()
        {
            MyVisualScriptLogicProvider.PlayerDied -= OnPlayerDied;
            _connection?.Stop();
            _connection = null;
            _bridge = null;
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
