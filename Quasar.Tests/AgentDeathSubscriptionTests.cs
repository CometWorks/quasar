using System.Reflection;
using System.Runtime.CompilerServices;
using Quasar.Agent;
using Sandbox.Game;
using Sandbox.Game.Multiplayer;
using Xunit;

namespace Quasar.Tests;

[Collection("Cluster identity environment")]
public sealed class AgentDeathSubscriptionTests
{
    [Fact]
    public void DeathMonitoringFollowsSessionWithoutTouchingModDelegates()
    {
        // Only the real event accessors are needed, not the game services used by the constructor.
        var first = (MyPlayerCollection)RuntimeHelpers.GetUninitializedObject(typeof(MyPlayerCollection));
        var second = (MyPlayerCollection)RuntimeHelpers.GetUninitializedObject(typeof(MyPlayerCollection));
        var nativeEvent = typeof(MyPlayerCollection).GetField("PlayerCharacterDied", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var modCallbacks = MyVisualScriptLogicProvider.PlayerDied;
        var agent = new AdminPlugin();
        Action<long> otherPlugin = _ => { };
        first.PlayerCharacterDied += otherPlugin;
        try
        {
            agent.SetDeathSource(first);
            agent.SetDeathSource(first);
            var handlers = ((Delegate)nativeEvent.GetValue(first)!).GetInvocationList();
            Assert.Equal(2, handlers.Length);
            Assert.Single(handlers, handler => ReferenceEquals(handler.Target, agent));

            agent.SetDeathSource(second);
            Assert.Same(otherPlugin, nativeEvent.GetValue(first));
            Assert.Same(agent, ((Delegate)nativeEvent.GetValue(second)!).Target);

            agent.SetDeathSource(null);
            Assert.Null(nativeEvent.GetValue(second));
            agent.SetDeathSource(first);
            agent.Dispose();
            Assert.Same(otherPlugin, nativeEvent.GetValue(first));
            Assert.Same(modCallbacks, MyVisualScriptLogicProvider.PlayerDied);
        }
        finally { agent.SetDeathSource(null); first.PlayerCharacterDied -= otherPlugin; }
    }
}
