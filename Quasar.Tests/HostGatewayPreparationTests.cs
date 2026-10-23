extern alias Host;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Quasar.Host.Contract.V1;
using Xunit;
using HostApp = Host::Quasar.Host;

namespace Quasar.Tests;

// #1433: the first start's data seeding took 35 s inside the Gateway apply, beyond Quasar's 30 s
// Host command deadline, so the reconciler recorded Failed/host_timeout while the Host went on to start it.
public sealed class HostGatewayPreparationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "host-gateway-preparation-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }

    [Fact]
    public async Task SlowFirstStartPreparationReportsMissingWithoutFailureThenLaunches()
    {
        if (!OperatingSystem.IsLinux()) return;
        GatewaySpec spec = CreateBundle();
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var actualizer = new HostApp.GatewayActualizer(Path.Combine(_root, "state"), "host")
        {
            PreparationWait = TimeSpan.FromMilliseconds(200),
            BeforePreparation = () => release.Task,
        };

        var watch = Stopwatch.StartNew();
        GatewayStatus preparing = await actualizer.ReconcileAsync(spec, CancellationToken.None);
        Assert.True(watch.Elapsed < TimeSpan.FromSeconds(5), $"Gateway apply waited {watch.Elapsed} for the preparation.");
        Assert.Equal(GatewayObservedState.Missing, preparing.Observed);
        Assert.Equal(GatewayGoal.On, preparing.Goal);
        Assert.Null(preparing.Failure);
        Assert.Null(preparing.ProcessId);
        // Activation, restore and snapshots must not touch the runtime root while it is being seeded.
        Assert.Throws<InvalidOperationException>(() => actualizer.EnsureStopped(spec.ClusterId));
        // Later passes observe the same preparation instead of starting another.
        Assert.Equal(GatewayObservedState.Missing, (await actualizer.ReconcileAsync(spec, CancellationToken.None)).Observed);
        Assert.False(File.Exists(Path.Combine(_root, "runtime/world/world.dat")));

        release.SetResult();
        GatewayStatus running = await ReconcileUntilAsync(actualizer, spec, GatewayObservedState.Running);
        Assert.Null(running.Failure);
        Assert.Equal("original world", File.ReadAllText(Path.Combine(_root, "runtime/world/world.dat")));
        await StopAsync(actualizer, spec, running);
        actualizer.EnsureStopped(spec.ClusterId);
    }

    [Fact]
    public async Task QuickPreparationLaunchesWithinTheSameApply()
    {
        if (!OperatingSystem.IsLinux()) return;
        GatewaySpec spec = CreateBundle();
        var actualizer = new HostApp.GatewayActualizer(Path.Combine(_root, "state"), "host");
        GatewayStatus running = await actualizer.ReconcileAsync(spec, CancellationToken.None);
        Assert.Equal(GatewayObservedState.Running, running.Observed);
        Assert.Null(running.Failure);
        Assert.True(File.Exists(Path.Combine(_root, "runtime/world/world.dat")));
        await StopAsync(actualizer, spec, running);
    }

    [Fact]
    public async Task FailedPreparationIsReportedAsSpawnPreflightFailure()
    {
        if (!OperatingSystem.IsLinux()) return;
        GatewaySpec spec = CreateBundle();
        File.WriteAllText(Path.Combine(_root, "bundle/seed/world.dat"), "tampered world");
        var actualizer = new HostApp.GatewayActualizer(Path.Combine(_root, "state"), "host");
        GatewayStatus failed = await ReconcileUntilAsync(actualizer, spec, GatewayObservedState.Failed);
        Assert.StartsWith("spawn_preflight_failed:", failed.Failure);
        Assert.Null(failed.ProcessId);
        actualizer.EnsureStopped(spec.ClusterId);
    }

    private GatewaySpec CreateBundle()
    {
        string bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(Path.Combine(bundle, "seed"));
        string executable = Path.Combine(bundle, "gateway");
        File.Copy("/bin/sleep", executable);
        File.SetUnixFileMode(executable, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        File.WriteAllText(Path.Combine(bundle, "seed/world.dat"), "original world");
        string Hash(string path) => HostApp.ExecutionBundle.Hash(File.ReadAllBytes(path));
        var manifest = new HostApp.BundleManifest(1, "revision-1", [new("gateway", Hash(executable))], [],
            new HostApp.GatewaySpawnSpec("gateway", "", ["60"], new()),
            ConfigFiles: [new("seed/world.dat", Hash(Path.Combine(bundle, "seed/world.dat")))],
            RuntimeRoot: Path.Combine(_root, "runtime"), InitialDirectories: new() { ["world"] = "seed" },
            ClusterId: "cluster", HostId: "host");
        string path = Path.Combine(bundle, "bundle.json");
        File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        int port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return new GatewaySpec("cluster", GatewayGoal.On, path, Hash(path), "revision-1", [port],
            Path.Combine(_root, "runtime/gateway"), StartGeneration: Guid.NewGuid());
    }

    private static async Task<GatewayStatus> ReconcileUntilAsync(HostApp.GatewayActualizer actualizer, GatewaySpec spec,
        GatewayObservedState expected)
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            GatewayStatus status = await actualizer.ReconcileAsync(spec, CancellationToken.None);
            if (status.Observed == expected || deadline.Elapsed > TimeSpan.FromSeconds(30)) return status;
            await Task.Delay(50);
        }
    }

    private static async Task StopAsync(HostApp.GatewayActualizer actualizer, GatewaySpec spec, GatewayStatus running)
    {
        GatewayStatus stopped = await actualizer.ReconcileAsync(spec with
        {
            Goal = GatewayGoal.Off,
            StopFence = new(running.ProcessId!.Value, running.LaunchedAt!.Value),
        }, CancellationToken.None);
        Assert.Equal(GatewayObservedState.Missing, stopped.Observed);
    }
}
