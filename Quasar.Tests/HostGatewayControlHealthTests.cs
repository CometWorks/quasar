extern alias Host;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Quasar.Host.Contract.V1;
using Xunit;
using HostApp = Host::Quasar.Host;

namespace Quasar.Tests;

// SE-1171: after a persistence failure the Gateway stays alive and answers every control request with 503
// registry_persistence_failed. The Host only respawned a Gateway whose process exited, so nothing restarted it.
// The stub Gateway is a sleeping process; a listener on its control endpoint plays the answers.
public sealed class HostGatewayControlHealthTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "host-gateway-control-" + Guid.NewGuid().ToString("N")
    );
    private volatile string _answer = "healthy";

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public async Task AGatewayAnsweringPersistenceFailureIsRestartedOnceWithItsGeneration()
    {
        if (!OperatingSystem.IsLinux())
            return;
        int controlPort = FreePort();
        using var control = new HttpListener();
        control.Prefixes.Add($"http://127.0.0.1:{controlPort}/");
        control.Start();
        _ = Task.Run(() => ServeAsync(control));
        GatewaySpec spec = CreateBundle($"0.0.0.0:{controlPort}");
        var actualizer = new HostApp.GatewayActualizer(Path.Combine(_root, "state"), "host")
        {
            UnhealthyRestartAfter = TimeSpan.FromSeconds(2),
        };

        GatewayStatus running = await ReconcileUntilAsync(
            actualizer,
            spec,
            status => status.Observed == GatewayObservedState.Running
        );
        Assert.Equal(GatewayObservedState.Running, running.Observed);
        int first = running.ProcessId!.Value;

        // A 503 for anything else (a busy registry) is not a persistence failure.
        _answer = "busy";
        GatewayStatus busy = await ReconcileForAsync(actualizer, spec, TimeSpan.FromSeconds(3));
        Assert.Equal(GatewayObservedState.Running, busy.Observed);
        Assert.Equal(first, busy.ProcessId);

        _answer = "persistence";
        var watch = Stopwatch.StartNew();
        GatewayStatus failed = await ReconcileUntilAsync(
            actualizer,
            spec,
            status => status.Observed != GatewayObservedState.Running
        );
        Assert.Equal(GatewayObservedState.Failed, failed.Observed);
        Assert.StartsWith(
            "control_unhealthy:registry_persistence_failed_for_seconds=",
            failed.Failure
        );
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10));
        Assert.False(Alive(first), "the unhealthy Gateway process was stopped");

        _answer = "healthy";
        GatewayStatus restarted = await ReconcileUntilAsync(
            actualizer,
            spec,
            status => status.Observed == GatewayObservedState.Running
        );
        Assert.Equal(GatewayObservedState.Running, restarted.Observed);
        Assert.NotEqual(first, restarted.ProcessId);
        Assert.Equal(spec.StartGeneration, restarted.StartGeneration);

        // Healthy again: no further restart.
        GatewayStatus settled = await ReconcileForAsync(actualizer, spec, TimeSpan.FromSeconds(3));
        Assert.Equal(GatewayObservedState.Running, settled.Observed);
        Assert.Equal(restarted.ProcessId, settled.ProcessId);

        await actualizer.ReconcileAsync(
            spec with
            {
                Goal = GatewayGoal.Off,
                StopFence = new(settled.ProcessId!.Value, settled.LaunchedAt!.Value),
            },
            CancellationToken.None
        );
        actualizer.EnsureStopped(spec.ClusterId);
        control.Stop();
    }

    [Theory]
    [InlineData(new[] { "--control", "0.0.0.0:29416" }, null, "http://127.0.0.1:29416")]
    [InlineData(new[] { "--control", "[::]:29416" }, null, "http://[::1]:29416")]
    [InlineData(
        new[] { "--control", "10.0.0.5:29416", "--backend", "127.0.0.1:27016" },
        null,
        "http://10.0.0.5:29416"
    )]
    [InlineData(new string[0], "127.0.0.1", "http://127.0.0.1:28016")]
    [InlineData(new string[0], null, null)]
    public void ControlUrlComesFromTheSpawnSpec(
        string[] arguments,
        string? environment,
        string? expected
    )
    {
        var spawn = new HostApp.GatewaySpawnSpec(
            "gateway",
            "",
            arguments,
            environment is null ? new() : new() { ["CLUSTER_CONTROL"] = environment }
        );
        Assert.Equal(expected, HostApp.GatewayActualizer.ControlUrl(spawn));
    }

    private async Task ServeAsync(HttpListener listener)
    {
        while (listener.IsListening)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync();
            }
            catch (Exception exception)
                when (exception is HttpListenerException or ObjectDisposedException)
            {
                return;
            }
            (int status, string code) = _answer switch
            {
                "persistence" => (503, "registry_persistence_failed"),
                "busy" => (503, "registry_busy"),
                _ => (401, "unauthorized"),
            };
            byte[] body = JsonSerializer.SerializeToUtf8Bytes(
                new { version = 1, error = new { code, message = "test" } }
            );
            context.Response.StatusCode = status;
            context.Response.ContentType = "application/json";
            await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    }

    private GatewaySpec CreateBundle(string control)
    {
        string bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(bundle);
        string executable = Path.Combine(bundle, "gateway");
        File.Copy("/bin/sleep", executable);
        File.SetUnixFileMode(
            executable,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
        );
        string Hash(string path) => HostApp.ExecutionBundle.Hash(File.ReadAllBytes(path));
        var manifest = new HostApp.BundleManifest(
            1,
            "revision-1",
            [new("gateway", Hash(executable))],
            [],
            new HostApp.GatewaySpawnSpec(
                "gateway",
                "",
                ["60"],
                new() { ["CLUSTER_CONTROL"] = control }
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
        return new GatewaySpec(
            "cluster",
            GatewayGoal.On,
            path,
            Hash(path),
            "revision-1",
            [FreePort()],
            Path.Combine(_root, "runtime/gateway"),
            StartGeneration: Guid.NewGuid()
        );
    }

    private static int FreePort()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        return ((IPEndPoint)listener.LocalEndpoint).Port;
    }

    private static bool Alive(int processId)
    {
        try
        {
            using Process process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static async Task<GatewayStatus> ReconcileUntilAsync(
        HostApp.GatewayActualizer actualizer,
        GatewaySpec spec,
        Func<GatewayStatus, bool> done
    )
    {
        var deadline = Stopwatch.StartNew();
        while (true)
        {
            GatewayStatus status = await actualizer.ReconcileAsync(spec, CancellationToken.None);
            if (done(status) || deadline.Elapsed > TimeSpan.FromSeconds(20))
                return status;
            await Task.Delay(200);
        }
    }

    private static async Task<GatewayStatus> ReconcileForAsync(
        HostApp.GatewayActualizer actualizer,
        GatewaySpec spec,
        TimeSpan span
    )
    {
        var watch = Stopwatch.StartNew();
        GatewayStatus status;
        do
        {
            status = await actualizer.ReconcileAsync(spec, CancellationToken.None);
            if (status.Observed != GatewayObservedState.Running)
                return status;
            await Task.Delay(200);
        } while (watch.Elapsed < span);
        return status;
    }
}
