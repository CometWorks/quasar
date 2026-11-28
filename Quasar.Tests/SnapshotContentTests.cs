extern alias Host;
using Xunit;
using Snapshots = Host::Quasar.Host.DeploymentSnapshots;

namespace Quasar.Tests;

// What a Host snapshot leaves out because a process writes it again (SE-1348, SE-1969).
public sealed class SnapshotContentTests
{
    [Theory]
    [InlineData("gateway/gateway.log", true)]
    [InlineData("gateway/gateway.log.1", true)]
    [InlineData("gateway/gateway.log.12", true)]
    [InlineData("nodes/abc/data/info.log", true)]
    [InlineData("nodes/abc/config/Preloader/x.dll", true)]
    [InlineData("gateway/gateway.log.old", false)]
    [InlineData("gateway/gateway.log.", false)]
    [InlineData("gateway/registry/events.jsonl.1", false)]
    [InlineData("gateway/registry/registry.db", false)]
    [InlineData("nodes/abc/data/cluster/saves/P-1/g1-s1.mps", false)]
    public void RegeneratedFilesAreLeftOut(string relative, bool leftOut) =>
        Assert.Equal(leftOut, Snapshots.Regenerated(relative));
}
