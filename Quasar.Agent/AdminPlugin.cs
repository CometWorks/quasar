using System;
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
        }

        public void Update()
        {
            _bridge?.Update();
        }

        public void Dispose()
        {
            _connection?.Stop();
            _connection = null;
            _bridge = null;
        }
    }
}
