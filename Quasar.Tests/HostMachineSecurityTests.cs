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
    public void HostReportsWhyNodesFailed()
    {
        string state = Path.Combine(Path.GetTempPath(), "node-failures-" + Guid.NewGuid().ToString("N"));
        try
        {
            string diagnostics = Path.Combine(state, "run");
            string run = Path.Combine(state, "node");
            Directory.CreateDirectory(diagnostics);
            Directory.CreateDirectory(Path.Combine(run, "config"));
            File.WriteAllText(Path.Combine(run, "config", "info.log"), "log");
            string Report(string kind, string source, string exception, string at) => System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1, reportId = Guid.NewGuid().ToString("N"), capturedAtUtc = at, source, kind, severity = "Error",
                message = "m", exception, context = new Dictionary<string, string>(),
            });
            void Write(string report) => File.WriteAllText(Path.Combine(diagnostics, Guid.NewGuid().ToString("N") + ".json"), report);
            Write(Report("unhandled_exception", "Magnetar", "System.Exception: outer\n ---> System.InvalidOperationException: boom\n   at Preloader.Fail()", "2026-11-01T00:00:00Z"));
            Write(Report("log", "plugin", "System.Exception: survived, and later", "2026-11-01T00:00:09Z"));
            Write(Report("unexpected_exit", "host", "System.Exception: from the Host", "2026-11-01T00:00:10Z"));
            string records = Path.Combine(state, "launch-records");
            Directory.CreateDirectory(records);
            string Record(string slot, string status, string? failure, string? directory) => System.Text.Json.JsonSerializer.Serialize(new
            {
                schemaVersion = 1, clusterId = "demo", slotKey = slot, attemptKey = "attempt", bundleRevision = "r", bundleManifestSha256 = "s",
                executablePath = "e", executableSha256 = "h", runDirectory = run, launchedAt = DateTimeOffset.UtcNow, status, failure,
                diagnosticDirectory = directory,
            });
            File.WriteAllText(Path.Combine(records, "failed.json"), Record("a", "Failed", "process_exited", diagnostics));
            File.WriteAllText(Path.Combine(records, "ready.json"), Record("b", "Ready", null, null));
            File.WriteAllText(Path.Combine(records, "bad.json"), "{");

            var failure = Assert.Single(HostApp.NodeActualizer.ReadFailures(state));

            Assert.Equal(("demo", "a", "process_exited"), (failure.ClusterId, failure.SlotKey, failure.Failure));
            Assert.Equal("System.Exception: outer caused by System.InvalidOperationException: boom at Preloader.Fail()", failure.Cause);
            Assert.Equal(Path.Combine(run, "config", "info.log"), failure.LogPath);
        }
        finally { Directory.Delete(state, true); }
    }

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
