extern alias Host;
using System.Diagnostics;
using Quasar.Host.Contract.V1;
using Xunit;
using HostApp = Host::Quasar.Host;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Tests;

[Collection("Cluster identity environment")]
public sealed class HostMachineSecurityTests
{
    [Fact]
    public void EnrolledHostLaunchesOnlyNodeScopedCredentialWithoutControlSecret()
    {
        const string secretName = "QSR_MANAGED_TEST_SECURITY";
        string[] names = [AgentSocketToken.EnvironmentVariable, secretName, "QUASAR_LAUNCHER_TOKEN"];
        var old = names.ToDictionary(n => n, Environment.GetEnvironmentVariable);
        try
        {
            string hostKey = AgentSocketToken.ForHost("enrollment-secret");
            Environment.SetEnvironmentVariable(AgentSocketToken.EnvironmentVariable, hostKey);
            Environment.SetEnvironmentVariable(secretName, "enrollment-secret");
            Environment.SetEnvironmentVariable("QUASAR_LAUNCHER_TOKEN", "bootstrap-secret");
            var attachment = new HostAttachmentSpec("fleet", "http://127.0.0.1:28016", secretName);
            var plan = new Admin.NodePlan("slot-a", "host-a", Admin.NodeRole.Regular,
                Admin.NodeGoal.Wanted, Admin.NodeObservation.Missing, null, Admin.IncumbentAction.None,
                null, 0, null, null, null, null, 0, false, true);
            var spec = new HostApp.NodeSpawnSpec("slot-a", Admin.NodeRole.Regular, "node-a", "unused", "",
                [], new() { [AgentSocketToken.EnvironmentVariable] = "untrusted-override" }, []);
            string root = Path.Combine(Path.GetTempPath(), "host-launch-security");
            ProcessStartInfo start = HostApp.NodeActualizer.CreateStartInfo(attachment, plan, spec,
                Path.Combine(root, "bundle"), Path.Combine(root, "run"), Path.Combine(root, "ready"),
                "attempt", Path.Combine(root, "unused"), "revision");
            Assert.Equal(AgentSocketToken.ForNode(hostKey, "fleet", "slot-a", "node-a", "Regular", "attempt"),
                start.Environment[AgentSocketToken.EnvironmentVariable]);
            Assert.DoesNotContain("enrollment-secret", start.Environment.Values);
            Assert.DoesNotContain(hostKey, start.Environment.Values);
            Assert.False(start.Environment.ContainsKey(secretName));
            Assert.False(start.Environment.ContainsKey("QUASAR_LAUNCHER_TOKEN"));
            Assert.Empty(start.ArgumentList);
        }
        finally { foreach (var (name, value) in old) Environment.SetEnvironmentVariable(name, value); }
    }

    [Fact]
    public void HostCommandAuthenticationRemainsMandatoryWithLoopbackTransport()
    {
        byte[] hash = System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes("host-control-secret"));
        Assert.False(HostApp.HostCommandServer.Authenticate(hash, null));
        Assert.False(HostApp.HostCommandServer.Authenticate(hash, "Bearer wrong"));
        Assert.False(HostApp.HostCommandServer.Authenticate(hash, "Basic host-control-secret"));
        Assert.True(HostApp.HostCommandServer.Authenticate(hash, "Bearer host-control-secret"));
    }
}
