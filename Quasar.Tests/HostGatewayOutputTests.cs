extern alias Host;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Quasar.Host.Contract.V1;
using Xunit;
using HostApp = Host::Quasar.Host;

namespace Quasar.Tests;

// SE-1281: the Gateway wrote its console into pipes read by the Host that started it. It outlives a Host
// restart, and from then on every line it wrote was lost: nothing read the pipes any more. Now it appends to
// gateway.log in its run root, which the next Host follows. The stub Gateway is a shell printing a numbered
// tick every 50 ms that, like a .NET Gateway, ignores SIGPIPE.
// With the other tests that swap the console, apart from all tests that run in parallel.
[Collection("Quasar shutdown")]
public sealed class HostGatewayOutputTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "host-gateway-output-" + Guid.NewGuid().ToString("N")
    );
    private readonly string _clusterId = "out-" + Guid.NewGuid().ToString("N")[..8];
    private readonly ConsoleCapture _console = new();

    public void Dispose()
    {
        _console.Dispose();
        if (Directory.Exists(_root))
            Directory.Delete(_root, true);
    }

    [Fact]
    public async Task GatewayOutputStillReachesTheHostLogAfterAHostRestart()
    {
        if (!OperatingSystem.IsLinux())
            return;
        GatewaySpec spec = CreateBundle(
            "trap '' PIPE; i=0; while :; do i=$((i+1)); echo \"tick $i\"; sleep 0.05; done"
        );
        string state = Path.Combine(_root, "state");
        GatewayStatus running;
        using (var first = new HostApp.GatewayActualizer(state, "host"))
        {
            running = await first.ReconcileAsync(spec, CancellationToken.None);
            Assert.Equal(GatewayObservedState.Running, running.Observed);
            await WaitForTickAsync(0);
        } // The Host exits; the Gateway keeps running.
        int beforeRestart = LastTick();

        using var second = new HostApp.GatewayActualizer(state, "host");
        try
        {
            GatewayStatus adopted = await second.ReconcileAsync(spec, CancellationToken.None);
            Assert.Equal(GatewayObservedState.Running, adopted.Observed);
            Assert.Equal(running.ProcessId, adopted.ProcessId);
            int tick = await WaitForTickAsync(beforeRestart + 10);
            Assert.True(tick > beforeRestart + 10, _console.Text);
            Assert.Equal(
                Path.Combine(spec.RunRoot, "gateway.log"),
                new FileInfo($"/proc/{running.ProcessId}/fd/1").LinkTarget
            );
            Assert.DoesNotContain("This Host can't show it", _console.Text);
        }
        finally
        {
            await StopAsync(second, spec, running);
        }
    }

    [Fact]
    public async Task AHostSaysSoWhenAnAdoptedGatewaysOutputGoesElsewhere()
    {
        if (!OperatingSystem.IsLinux())
            return;
        // As a Gateway started before the log file: its output doesn't reach gateway.log.
        GatewaySpec spec = CreateBundle("exec >/dev/null 2>&1; while :; do sleep 1; done");
        string state = Path.Combine(_root, "state");
        GatewayStatus running;
        using (var first = new HostApp.GatewayActualizer(state, "host"))
            running = await first.ReconcileAsync(spec, CancellationToken.None);
        Assert.Equal(GatewayObservedState.Running, running.Observed);

        using var second = new HostApp.GatewayActualizer(state, "host");
        try
        {
            Assert.Equal(
                GatewayObservedState.Running,
                (await second.ReconcileAsync(spec, CancellationToken.None)).Observed
            );
            await second.ReconcileAsync(spec, CancellationToken.None);
            string warning =
                $"[gateway {_clusterId}] The Gateway (PID {running.ProcessId}) was started by an earlier Host and writes its output to /dev/null";
            Assert.Equal(1, Count(_console.Text, warning));
        }
        finally
        {
            await StopAsync(second, spec, running);
        }
    }

    private async Task<int> WaitForTickAsync(int after)
    {
        var deadline = Stopwatch.StartNew();
        int tick;
        while ((tick = LastTick()) <= after && deadline.Elapsed < TimeSpan.FromSeconds(10))
            await Task.Delay(50);
        return tick;
    }

    private int LastTick()
    {
        string prefix = $"[gateway {_clusterId}] tick ";
        return _console
            .Text.Split('\n')
            .Where(line => line.StartsWith(prefix, StringComparison.Ordinal))
            .Select(line => int.Parse(line[prefix.Length..].Trim()))
            .DefaultIfEmpty(0)
            .Max();
    }

    private static int Count(string text, string value)
    {
        int count = 0;
        for (
            int at = text.IndexOf(value, StringComparison.Ordinal);
            at >= 0;
            at = text.IndexOf(value, at + 1, StringComparison.Ordinal)
        )
            count++;
        return count;
    }

    private static async Task StopAsync(
        HostApp.GatewayActualizer actualizer,
        GatewaySpec spec,
        GatewayStatus running
    )
    {
        await actualizer.ReconcileAsync(
            spec with
            {
                Goal = GatewayGoal.Off,
                StopFence = new(running.ProcessId!.Value, running.LaunchedAt!.Value),
            },
            CancellationToken.None
        );
        actualizer.EnsureStopped(spec.ClusterId);
    }

    private GatewaySpec CreateBundle(string script)
    {
        string bundle = Path.Combine(_root, "bundle");
        Directory.CreateDirectory(bundle);
        string executable = Path.Combine(bundle, "gateway");
        File.Copy("/bin/sh", executable);
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
            new HostApp.GatewaySpawnSpec("gateway", "", ["-c", script], new()),
            RuntimeRoot: Path.Combine(_root, "runtime"),
            ClusterId: _clusterId,
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
            _clusterId,
            GatewayGoal.On,
            path,
            Hash(path),
            "revision-1",
            [],
            Path.Combine(_root, "runtime/gateway"),
            StartGeneration: Guid.NewGuid()
        );
    }
}

/// <summary>Copies what the process writes to its console into <see cref="Text"/> while it lives.</summary>
internal sealed class ConsoleCapture : IDisposable
{
    private readonly TextWriter _out = Console.Out;
    private readonly TextWriter _error = Console.Error;
    private readonly StringBuilder _text = new();

    public ConsoleCapture()
    {
        Console.SetOut(new Tee(_out, _text));
        Console.SetError(new Tee(_error, _text));
    }

    public string Text
    {
        get
        {
            lock (_text)
                return _text.ToString();
        }
    }

    public void Dispose()
    {
        Console.SetOut(_out);
        Console.SetError(_error);
    }

    private sealed class Tee(TextWriter inner, StringBuilder copy) : TextWriter
    {
        public override Encoding Encoding => inner.Encoding;

        public override void Write(char value)
        {
            lock (copy)
                copy.Append(value);
            inner.Write(value);
        }

        public override void WriteLine(string? value)
        {
            lock (copy)
                copy.Append(value).Append('\n');
            inner.WriteLine(value);
        }
    }
}
