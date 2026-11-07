extern alias Host;
using System.Diagnostics;
using System.Text.Json;
using Quasar.Host.Contract.V1;
using Xunit;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;
using HostApp = Host::Quasar.Host;

namespace Quasar.Tests;

// SE-1186: the Host passed its whole start-time environment to every Gateway and node it launched: a stale
// QUASAR_CLUSTER_ARCHIVE_URL, the launcher token and the Host's agent key among them. The stub children are
// /bin/sh writing out their environment.
[Collection("Cluster identity environment")]
public sealed class HostChildEnvironmentTests : IDisposable
{
    private static readonly Dictionary<string, string> HostEnvironment = new()
    {
        ["QUASAR_CLUSTER_ARCHIVE_URL"] = "file:///old/ClusterForLinux-0.2.5.tar.gz",
        ["QUASAR_LAUNCHER_TOKEN"] = "launcher-secret",
        [AgentSocketToken.EnvironmentVariable] = AgentSocketToken.ForHost("enrollment-secret"),
        ["QUASAR_INSTALL_DIR"] = "/opt/quasar",
        ["QUASAR_BASE_URL"] = "http://quasar.test:5000",
        ["MAGNETAR_HOST_ID"] = "host-a",
        ["QSR_MANAGED_SE1186"] = "enrollment-secret",
        ["CLUSTER_WORLD"] = "/stale/world",
        ["SE1186_UNRELATED"] = "1",
        ["DOTNET_SE1186"] = "1",
    };

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "host-child-env-" + Guid.NewGuid().ToString("N")
    );

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public async Task GatewayGetsOnlyTheAllowListAndItsSpec()
    {
        if (!OperatingSystem.IsLinux())
            return;
        Dictionary<string, string> child = await WithHostEnvironmentAsync(LaunchGatewayAsync);

        Assert.Equal("/new/world", child["CLUSTER_WORLD"]);
        Assert.Equal("cluster", child["CLUSTER_ID"]);
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), child["PATH"]);
        Assert.Equal("1", child["DOTNET_SE1186"]);
        Assert.DoesNotContain(
            child.Keys,
            name => name.StartsWith("QUASAR_") || name.StartsWith("QSR_")
        );
        Assert.DoesNotContain("MAGNETAR_HOST_ID", child.Keys);
        Assert.DoesNotContain("SE1186_UNRELATED", child.Keys);
    }

    [Fact]
    public async Task NodeGetsOnlyTheAllowListItsSpecAndItsOwnAgentToken()
    {
        if (!OperatingSystem.IsLinux())
            return;
        Dictionary<string, string> child = await WithHostEnvironmentAsync(LaunchNodeAsync);

        Assert.Equal("/new/world", child["CLUSTER_WORLD"]);
        Assert.Equal("node-a", child["CLUSTER_NODE_ID"]);
        Assert.Equal(Environment.GetEnvironmentVariable("PATH"), child["PATH"]);
        Assert.Equal("1", child["DOTNET_SE1186"]);
        Assert.Equal("http://quasar.test:5000", child["QUASAR_BASE_URL"]);
        Assert.Equal("host-a", child["MAGNETAR_HOST_ID"]);
        Assert.Equal(
            AgentSocketToken.ForNode(
                HostEnvironment[AgentSocketToken.EnvironmentVariable],
                "cluster",
                "slot-a",
                "node-a",
                "Regular",
                "attempt"
            ),
            child[AgentSocketToken.EnvironmentVariable]
        );
        Assert.Equal(
            [AgentSocketToken.EnvironmentVariable, "QUASAR_BASE_URL"],
            child.Keys.Where(name => name.StartsWith("QUASAR_") || name.StartsWith("QSR_")).Order()
        );
        Assert.DoesNotContain("SE1186_UNRELATED", child.Keys);
    }

    private static async Task<Dictionary<string, string>> WithHostEnvironmentAsync(
        Func<Task<Dictionary<string, string>>> launch
    )
    {
        var old = HostEnvironment.Keys.ToDictionary(
            name => name,
            Environment.GetEnvironmentVariable
        );
        try
        {
            foreach (var (name, value) in HostEnvironment)
                Environment.SetEnvironmentVariable(name, value);
            return await launch();
        }
        finally
        {
            foreach (var (name, value) in old)
                Environment.SetEnvironmentVariable(name, value);
        }
    }

    private async Task<Dictionary<string, string>> LaunchGatewayAsync()
    {
        string bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(bundle);
        string shell = Path.Combine(bundle, "gateway");
        File.Copy("/bin/sh", shell);
        string Hash(string path) => HostApp.ExecutionBundle.Hash(File.ReadAllBytes(path));
        var manifest = new HostApp.BundleManifest(
            1,
            "revision-1",
            [new("gateway", Hash(shell))],
            [],
            new HostApp.GatewaySpawnSpec(
                "gateway",
                "",
                [
                    "-c",
                    "env > {runRoot}/env.tmp; mv {runRoot}/env.tmp {runRoot}/env.txt; exec sleep 60",
                ],
                new() { ["CLUSTER_WORLD"] = "/new/world" }
            ),
            RuntimeRoot: Path.Combine(_root, "runtime"),
            ClusterId: "cluster",
            HostId: "host"
        );
        string path = Path.Combine(bundle, "bundle.json");
        File.WriteAllBytes(
            path,
            JsonSerializer.SerializeToUtf8Bytes(
                manifest,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)
            )
        );
        string run = Path.Combine(_root, "runtime/gateway");
        var spec = new GatewaySpec(
            "cluster",
            GatewayGoal.On,
            path,
            Hash(path),
            "revision-1",
            [],
            run,
            StartGeneration: Guid.NewGuid()
        );
        var actualizer = new HostApp.GatewayActualizer(Path.Combine(_root, "state"), "host");
        GatewayStatus status = await actualizer.ReconcileAsync(spec, CancellationToken.None);
        Assert.Equal(GatewayObservedState.Running, status.Observed);
        try
        {
            return await ReadEnvironmentAsync(Path.Combine(run, "env.txt"));
        }
        finally
        {
            await actualizer.ReconcileAsync(
                spec with
                {
                    Goal = GatewayGoal.Off,
                    StopFence = new(status.ProcessId!.Value, status.LaunchedAt!.Value),
                },
                CancellationToken.None
            );
        }
    }

    private async Task<Dictionary<string, string>> LaunchNodeAsync()
    {
        string run = Path.Combine(_root, "node");
        Directory.CreateDirectory(run);
        var attachment = new HostAttachmentSpec(
            "cluster",
            "http://127.0.0.1:28016",
            "QSR_MANAGED_SE1186"
        );
        var plan = new Admin.NodePlan(
            "slot-a",
            "host-a",
            Admin.NodeRole.Regular,
            Admin.NodeGoal.Wanted,
            Admin.NodeObservation.Missing,
            null,
            Admin.IncumbentAction.None,
            null,
            0,
            null,
            null,
            null,
            null,
            0,
            false,
            true
        );
        var spec = new HostApp.NodeSpawnSpec(
            "slot-a",
            Admin.NodeRole.Regular,
            "node-a",
            "sh",
            "",
            [
                "-c",
                "env > {runDirectory}/env.tmp; mv {runDirectory}/env.tmp {runDirectory}/env.txt",
            ],
            new() { ["CLUSTER_WORLD"] = "/new/world" },
            []
        );
        ProcessStartInfo start = HostApp.NodeActualizer.CreateStartInfo(
            attachment,
            plan,
            spec,
            run,
            run,
            Path.Combine(run, "ready"),
            "attempt",
            "/bin/sh",
            "revision"
        );
        using Process process = Process.Start(start)!;
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        return await ReadEnvironmentAsync(Path.Combine(run, "env.txt"));
    }

    private static async Task<Dictionary<string, string>> ReadEnvironmentAsync(string path)
    {
        for (int attempt = 0; attempt < 100 && !File.Exists(path); attempt++)
            await Task.Delay(100);
        // The values set here are single lines; another variable's extra lines have no name to match.
        return File.ReadAllLines(path)
            .Where(line => line.Contains('='))
            .Select(line => line.Split('=', 2))
            .GroupBy(pair => pair[0])
            .ToDictionary(group => group.Key, group => group.First()[1]);
    }
}
