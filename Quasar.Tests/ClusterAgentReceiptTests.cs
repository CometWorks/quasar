using Quasar.Agent;
using Xunit;

namespace Quasar.Tests;

[CollectionDefinition("Cluster identity environment", DisableParallelization = true)]
public sealed class ClusterIdentityEnvironmentCollection;

[Collection("Cluster identity environment")]
public sealed class ClusterAgentReceiptTests
{
    [Theory]
    [InlineData("attempt-a", 500, 4294967296L)]
    [InlineData("old-attempt", 500, 0L)]
    [InlineData("attempt-a", 499, 0L)]
    public void ReceiptRequiresMatchingLaunchAndPreserves64BitEpoch(string attempt, int pid, long expectedEpoch)
    {
        string path = Path.GetTempFileName();
        string? oldPath = Environment.GetEnvironmentVariable("CLUSTER_PROCESS_IDENTITY_PATH");
        string? oldAttempt = Environment.GetEnvironmentVariable("CLUSTER_LAUNCH_ATTEMPT");
        try
        {
            File.WriteAllText(path, """{"schemaVersion":1,"clusterId":"cluster-a","slotKey":"slot-a","attemptKey":"attempt-a","nodeId":"node-a","epoch":4294967296,"processId":500}""");
            Environment.SetEnvironmentVariable("CLUSTER_PROCESS_IDENTITY_PATH", path);
            Environment.SetEnvironmentVariable("CLUSTER_LAUNCH_ATTEMPT", attempt);
            var options = new AgentOptions { ClusterMode = true, ClusterId = "cluster-a", ClusterSlot = "slot-a" };
            options.RefreshClusterIdentity(pid);
            Assert.Equal(expectedEpoch, options.ClusterEpoch);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CLUSTER_PROCESS_IDENTITY_PATH", oldPath);
            Environment.SetEnvironmentVariable("CLUSTER_LAUNCH_ATTEMPT", oldAttempt);
            File.Delete(path);
        }
    }
}
