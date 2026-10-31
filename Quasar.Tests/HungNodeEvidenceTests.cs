extern alias Host;
using System.Diagnostics;
using Xunit;
using HostApp = Host::Quasar.Host;

namespace Quasar.Tests;

public sealed class HungNodeEvidenceTests : IDisposable
{
    private readonly string root = Directory.CreateTempSubdirectory("hung-node-test-").FullName;
    private string Evidence => File.ReadAllText(Path.Combine(root, "hang-evidence.txt"));

    [Fact]
    public async Task StuckToolStillLeavesThreadStatesWithinTheLimit()
    {
        if (!OperatingSystem.IsLinux()) return;
        string tool = Path.Combine(root, "stuck-tool");
        File.WriteAllText(tool, "#!/bin/sh\nexec sleep 60\n");
        File.SetUnixFileMode(tool, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var hung = Process.Start("sleep", "60");
        var clock = Stopwatch.StartNew();
        try
        {
            await HostApp.HungNodeEvidence.CaptureAsync(root, hung.Id, false, "node_hung", "Fixture hang.", TimeSpan.FromSeconds(2), tool);
            Assert.InRange(clock.Elapsed.TotalSeconds, 2, 10);
            Assert.Contains("\"sleep\" state=S", Evidence);
            Assert.Contains("createdump: timed out", Evidence);
            Assert.False(hung.HasExited);
        }
        finally { hung.Kill(); }
    }

    [Fact]
    public async Task RuntimeWritesTheStacksWhenTheToolIsRefused()
    {
        if (!OperatingSystem.IsLinux()
            || !Directory.EnumerateFiles(Path.GetTempPath(), $"dotnet-diagnostic-{Environment.ProcessId}-*-socket").Any()) return;
        await HostApp.HungNodeEvidence.CaptureAsync(root, Environment.ProcessId, false, "node_hung", "Fixture hang.", TimeSpan.FromSeconds(15), "/bin/false");
        Assert.Contains("Stacks: from createdump. Minidump: none.", Evidence);
        Assert.Contains("  System.", Evidence);
        Assert.False(File.Exists(Path.Combine(root, "hang.dmp")));
    }

    public void Dispose() => Directory.Delete(root, true);
}
