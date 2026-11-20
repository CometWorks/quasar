extern alias Host;

using System.Diagnostics;
using System.Text.Json;
using Xunit;
using HostProgram = Host::Quasar.Host.Program;

namespace Quasar.Tests;

public sealed class HostExecutionLockTests : IDisposable
{
    private readonly string root = Path.Combine(Path.GetTempPath(), "quasar-executor-" + Guid.NewGuid().ToString("N"));
    private string StateDirectory => Path.Combine(root, "state");

    public HostExecutionLockTests()
    {
        Directory.CreateDirectory(StateDirectory);
        File.WriteAllText(Path.Combine(root, "host.json"), """
            {
              "executorId": "test-executor",
              "hostId": "test-host",
              "pollIntervalSeconds": 2,
              "stateDirectory": "state",
              "attachments": [
                { "clusterId": "test-cluster", "gatewayUrl": "http://127.0.0.1:1",
                  "tokenEnvironmentVariable": "QUASAR_TEST_MISSING_EXECUTOR_TOKEN" }
              ]
            }
            """);
    }

    public void Dispose() => Directory.Delete(root, recursive: true);

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ContenderExitsCleanlyAndLeavesTheIncumbentLockHeld(bool once)
    {
        if (!OperatingSystem.IsLinux() && !OperatingSystem.IsWindows()) return;
        string path = Path.Combine(StateDirectory, "executor.lock");
        using (var incumbent = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None))
        {
            var rejected = await RunHostAsync(once);
            Assert.Equal(5, rejected.ExitCode);
            Assert.Contains("Another process holds the Host executor lock", rejected.Error);
            string run = Assert.Single(Runs());
            using var record = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(run, "run.json")));
            Assert.True(record.RootElement.GetProperty("expectedExit").GetBoolean());
            Assert.Single(Directory.GetFiles(run));
            Assert.Throws<IOException>(() => File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None).Dispose());
        }

        var acquired = await RunHostAsync(once: true);
        Assert.Equal(4, acquired.ExitCode);
        Assert.Contains("credential environment variable", acquired.Error);
        Assert.Equal(2, Runs().Length);
    }

    [Fact]
    public async Task InvalidLockPathStillProducesAnUnhandledFailure()
    {
        Directory.CreateDirectory(Path.Combine(StateDirectory, "executor.lock"));

        var failed = await RunHostAsync(once: true);

        Assert.NotEqual(0, failed.ExitCode);
        Assert.NotEqual(5, failed.ExitCode);
        string run = Assert.Single(Runs());
        string report = Assert.Single(Directory.GetFiles(run, "*.json").Where(path => Path.GetFileName(path) != "run.json"));
        using var document = JsonDocument.Parse(File.ReadAllBytes(report));
        Assert.Equal("unhandled_exception", document.RootElement.GetProperty("kind").GetString());
    }

    private string[] Runs() => Directory.GetDirectories(Path.Combine(StateDirectory, "diagnostics", "runs"));

    private async Task<(int ExitCode, string Error)> RunHostAsync(bool once)
    {
        var start = new ProcessStartInfo(Environment.GetEnvironmentVariable("DOTNET_HOST_PATH") ?? "dotnet")
        {
            WorkingDirectory = root,
            UseShellExecute = false,
            RedirectStandardError = true,
        };
        start.ArgumentList.Add(typeof(HostProgram).Assembly.Location);
        start.ArgumentList.Add("run");
        start.ArgumentList.Add("--config");
        start.ArgumentList.Add(Path.Combine(root, "host.json"));
        if (once) start.ArgumentList.Add("--once");
        start.Environment.Remove("QUASAR_HOST_DIAGNOSTICS_DIRECTORY");
        start.Environment.Remove("QUASAR_TEST_MISSING_EXECUTOR_TOKEN");
        start.Environment["DOTNET_DbgEnableMiniDump"] = "0";
        using var process = Process.Start(start)!;
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            Task<string> error = process.StandardError.ReadToEndAsync(deadline.Token);
            await process.WaitForExitAsync(deadline.Token);
            return (process.ExitCode, await error);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync();
            }
        }
    }
}
