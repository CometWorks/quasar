using PluginSdk;
using PluginSdk.Clustering;
using Quasar.Agent;
using Xunit;

namespace Quasar.Tests;

[Collection("Cluster identity environment")]
public sealed class AgentSupervisedLifecycleTests
{
    [Fact]
    public void PluginRestartBecomesQuitWhenQuasarIsTold()
    {
        var (calls, reports) = Run(reported: true, ServerTerminationKind.Restart, saveFirst: true);

        Assert.Equal(1, reports);
        Assert.Equal([(ServerTerminationKind.Shutdown, true)], calls);
    }

    [Fact]
    public void RestartWithoutSavingQuitsWithoutSaving()
    {
        var (calls, _) = Run(reported: true, ServerTerminationKind.Restart, saveFirst: false);

        Assert.Equal([(ServerTerminationKind.Shutdown, false)], calls);
    }

    [Fact]
    public void RestartStaysWithMagnetarWhenQuasarCannotBeTold()
    {
        var (calls, _) = Run(reported: false, ServerTerminationKind.Restart, saveFirst: true);

        Assert.Equal([(ServerTerminationKind.Restart, true)], calls);
    }

    [Fact]
    public void ShutdownPassesThroughWithoutReporting()
    {
        var (calls, reports) = Run(reported: true, ServerTerminationKind.Shutdown, saveFirst: true);

        Assert.Equal(0, reports);
        Assert.Equal([(ServerTerminationKind.Shutdown, true)], calls);
    }

    private static (List<(ServerTerminationKind, bool)> Calls, int Reports) Run(
        bool reported, ServerTerminationKind kind, bool saveFirst)
    {
        var calls = new List<(ServerTerminationKind, bool)>();
        var reports = 0;
        SupervisedLifecycle? lifecycle = null;
        lifecycle = new SupervisedLifecycle(
            () =>
            {
                reports++;
                return reported;
            },
            (localKind, localSaveFirst) =>
            {
                // Unregistered while running locally, so the call reaches Magnetar.
                Assert.True(ClusterLifecycle.Register(lifecycle!));
                Assert.True(ClusterLifecycle.Unregister(lifecycle!));
                calls.Add((localKind, localSaveFirst));
            });

        Assert.True(ClusterLifecycle.Register(lifecycle));
        try
        {
            // ServerControl routes here; calling it needs VRage, which the tests don't load.
            var request = new ClusterLifecycleRequest(Guid.NewGuid(), kind, ClusterLifecycleOrigin.Plugin, saveFirst);
            var acknowledgement = lifecycle.RequestAsync(request, CancellationToken.None).Result;
            Assert.Equal(ClusterLifecycleDisposition.Accepted, acknowledgement.Disposition);
            Assert.Equal(request.RequestId, acknowledgement.RequestId);
            // Registered again when the process did not exit.
            Assert.False(ClusterLifecycle.Register(new SupervisedLifecycle(() => true)));
        }
        finally
        {
            ClusterLifecycle.Unregister(lifecycle);
        }

        return (calls, reports);
    }
}
