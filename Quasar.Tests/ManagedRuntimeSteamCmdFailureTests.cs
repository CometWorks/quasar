using System.Diagnostics;
using System.Reflection;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

// SE1-0261: a SteamCMD that can't run (the 32-bit linux32/steamcmd without a 32-bit loader
// exits 127) must fail SteamCMD alone when the Dedicated Server is already in place.
public sealed class ManagedRuntimeSteamCmdFailureTests : IDisposable
{
    private const string MissingLoaderScript =
        "#!/bin/sh\necho \"steamcmd.sh: line 37: linux32/steamcmd: cannot execute: required file not found\" >&2\nexit 127\n";

    private const string WorkingScript =
        "#!/bin/sh\nmkdir -p \"$(dirname \"$0\")/linux64\"\ncd \"$(dirname \"$0\")/linux64\" && touch steamclient.so libtier0_s.so libvstdlib_s.so\n";

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "quasar-tests",
        $"steamcmd-failure-{Guid.NewGuid():N}"
    );
    private readonly GitHubUpdateCredentialsCatalog _credentials = new(
        NullLogger<GitHubUpdateCredentialsCatalog>.Instance,
        new EphemeralDataProtectionProvider()
    );

    public void Dispose()
    {
        _credentials.Dispose();
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch { }
    }

    [Fact]
    public async Task SteamCmdThatCannotRunFailsOnlySteamCmdWhenTheDedicatedServerIsInPlace()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var resolver = CreateResolver(MissingLoaderScript, withDedicatedServer: true);
        var reports = new List<ManagedRuntimeInstallProgress>();

        var readiness = await resolver.EnsureManagedRuntimeReadyAsync(
            new SyncProgress(reports.Add)
        );

        Assert.True(readiness.IsReady, readiness.FailureMessage);
        Assert.Contains("ExitCode=127", readiness.SteamCmdFailure);
        Assert.Contains("sudo apt install lib32gcc-s1", readiness.SteamCmdFailure);
        Assert.Equal(
            ManagedRuntimeInstallPhase.Failed,
            LastPhase(reports, ManagedRuntimeInstallComponent.SteamCmd)
        );
        Assert.Equal(
            ManagedRuntimeInstallPhase.Ready,
            LastPhase(reports, ManagedRuntimeInstallComponent.DedicatedServer)
        );
        Assert.Equal(
            ManagedRuntimeInstallPhase.Ready,
            LastPhase(reports, ManagedRuntimeInstallComponent.Magnetar)
        );
    }

    [Fact]
    public async Task WarmupCompletesWithOnlySteamCmdFailed()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var warmup = new ManagedRuntimeWarmupService(
            CreateResolver(MissingLoaderScript, withDedicatedServer: true),
            NullLogger<ManagedRuntimeWarmupService>.Instance
        );

        await warmup.RetryAsync();

        Assert.True(warmup.IsReady);
        // Final readiness must include the component states, without queued progress lag.
        var snapshot = warmup.GetSnapshot();
        var steamCmd = snapshot.Components.Single(c =>
            c.Component == ManagedRuntimeInstallComponent.SteamCmd
        );
        Assert.Equal(ManagedRuntimeComponentState.Failed, steamCmd.State);
        Assert.Contains(
            ManagedDedicatedServerRuntimeResolver.SteamCmdLinuxLibrariesHint,
            steamCmd.Message
        );
        Assert.All(
            snapshot.Components.Where(c => c.Component != ManagedRuntimeInstallComponent.SteamCmd),
            c => Assert.Equal(ManagedRuntimeComponentState.Ready, c.State)
        );
        Assert.Contains("SteamCMD failed", snapshot.Message);
    }

    [Fact]
    public async Task SteamCmdOutputIsReportedBeforeTheProcessExits()
    {
        if (!OperatingSystem.IsLinux()) return;
        using var process = new Process
        {
            StartInfo = new ProcessStartInfo("/bin/sh")
            {
                ArgumentList = { "-c", "printf '%s\\n' 'Update state (0x61) downloading, progress: 25.0 (25 / 100)'; read answer" },
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            },
        };
        process.Start();
        var reported = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var resolver = CreateResolver(WorkingScript, withDedicatedServer: false);
        var wait = (Task)typeof(ManagedDedicatedServerRuntimeResolver)
            .GetMethod("WaitForSteamCmdProcessAsync", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(resolver, [process, "test progress", CancellationToken.None, (Action<string>)(line => reported.TrySetResult(line))])!;
        try
        {
            string line = await reported.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(25, ManagedDedicatedServerRuntimeResolver.ParseDedicatedServerInstallProgress(line, "")!.Percent);
            Assert.False(process.HasExited);
        }
        finally
        {
            await process.StandardInput.WriteLineAsync("finish");
            await wait.WaitAsync(TimeSpan.FromSeconds(5));
        }
    }

    [Fact]
    public async Task SteamCmdThatCannotRunStillFailsAMissingDedicatedServer()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var resolver = CreateResolver(MissingLoaderScript, withDedicatedServer: false);

        var readiness = await resolver.EnsureManagedRuntimeReadyAsync();

        Assert.False(readiness.IsReady);
        Assert.StartsWith(
            "Space Engineers Dedicated Server is not installed and could not be downloaded.",
            readiness.FailureMessage
        );
    }

    [Fact]
    public async Task WorkingSteamCmdPreparesTheNativeRuntimeAsBefore()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var resolver = CreateResolver(WorkingScript, withDedicatedServer: true);
        var reports = new List<ManagedRuntimeInstallProgress>();

        var readiness = await resolver.EnsureManagedRuntimeReadyAsync(
            new SyncProgress(reports.Add)
        );

        Assert.True(readiness.IsReady, readiness.FailureMessage);
        Assert.Empty(readiness.SteamCmdFailure);
        Assert.Equal(Path.Combine(_root, "SteamCMD", "linux64"), readiness.SteamCmdRuntimePath);
        var steamCmd = reports.Last(r => r.Component == ManagedRuntimeInstallComponent.SteamCmd);
        Assert.Equal(ManagedRuntimeInstallPhase.Ready, steamCmd.Phase);
        Assert.Equal("SteamCMD and linux64 native runtime ready.", steamCmd.Message);
    }

    [Fact]
    public void HintNamesThe32BitPackagesForExitCode127Only()
    {
        if (!OperatingSystem.IsLinux() || !File.Exists("/lib/ld-linux.so.2"))
            return;

        Assert.Contains(
            "lib32gcc-s1",
            ManagedDedicatedServerRuntimeResolver.DescribeMissingSteamCmdLibraries(127)
        );
        Assert.Empty(ManagedDedicatedServerRuntimeResolver.DescribeMissingSteamCmdLibraries(1));
        Assert.Empty(ManagedDedicatedServerRuntimeResolver.DescribeMissingSteamCmdLibraries());
    }

    // QSR-27: SteamCMD that prepares its runtime, logs each call and fails app_update with exit code 5 when told to.
    private const string UpdateScript =
        "#!/bin/sh\necho \"$*\" >> \"$(dirname \"$0\")/calls.log\"\n"
        + "case \"$*\" in *app_update*) test -e \"$(dirname \"$0\")/fail\" && exit 5;; esac\n"
        + "mkdir -p \"$(dirname \"$0\")/linux64\"\ncd \"$(dirname \"$0\")/linux64\" && touch steamclient.so libtier0_s.so libvstdlib_s.so\n";

    private string[] SteamCmdCalls() =>
        File.Exists(Path.Combine(_root, "SteamCMD", "calls.log")) ? File.ReadAllLines(Path.Combine(_root, "SteamCMD", "calls.log")) : [];

    // QSR-27: a game update left the managed Dedicated Server on the old build, because only startup and the
    // manual check updated it, and every new cluster copied that build. Cluster setup now updates it first.
    [Theory]
    [InlineData(DedicatedServerLaunchUpdateMode.Update, false)]
    [InlineData(DedicatedServerLaunchUpdateMode.Validate, true)]
    public async Task ClusterUpdateRunsAppUpdateOnAnInstalledServer(DedicatedServerLaunchUpdateMode mode, bool validate)
    {
        if (!OperatingSystem.IsLinux())
            return;

        var resolver = CreateResolver(UpdateScript, withDedicatedServer: true, mode);

        await resolver.UpdateManagedDedicatedServerForClusterAsync(null, default);

        string update = Assert.Single(SteamCmdCalls(), call => call.Contains("+app_update 298740"));
        Assert.Equal(validate, update.Contains("validate"));
    }

    [Fact]
    public async Task ClusterUpdateSkipsSteamCmdInSkipMode()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var resolver = CreateResolver(UpdateScript, withDedicatedServer: true, DedicatedServerLaunchUpdateMode.Skip);

        await resolver.UpdateManagedDedicatedServerForClusterAsync(null, default);

        Assert.Empty(SteamCmdCalls());
    }

    // Before, a failed app_update kept the old install and reported it ready.
    [Fact]
    public async Task FailedClusterUpdateThrowsAlthoughAnOlderServerIsInstalled()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var resolver = CreateResolver(UpdateScript, withDedicatedServer: true);
        File.WriteAllText(Path.Combine(_root, "SteamCMD", "fail"), "");

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            resolver.UpdateManagedDedicatedServerForClusterAsync(null, default));

        Assert.Contains("Updating the Dedicated Server failed", error.Message);
        Assert.Contains("exit code 5", error.Message);
        Assert.Equal(3, SteamCmdCalls().Count(call => call.Contains("+app_update")));
    }

    [Fact]
    public async Task FailedUpdateStopsClusterSetupBeforeItCopiesTheServer()
    {
        if (!OperatingSystem.IsLinux())
            return;

        var resolver = CreateResolver(UpdateScript, withDedicatedServer: true);
        File.WriteAllText(Path.Combine(_root, "SteamCMD", "fail"), "");
        var cache = typeof(Magnetar.Protocol.Runtime.MagnetarPaths).GetField("_cachedQuasarDirectory",
            BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = cache.GetValue(null);
        cache.SetValue(null, _root);
        try
        {
            string work = Path.Combine(_root, "ClusterSetup", "demo");
            Directory.CreateDirectory(work);
            // Only the runtime is set: setup must stop before it touches the package, catalog or dependencies.
            var setup = new ClusterSetupService(null!, null!, null!, null!, null!, null!,
                resolver, null!, null!, null!, null!, null!, null!, null!);
            var request = new ClusterSetupRequest("demo", "Demo", "world", "profile", "local", 28000, [new("local", 2)]);

            var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                setup.PrepareInputsAsync(new Quasar.Models.ClusterDefinition { UniqueName = "demo" }, request, work, null, default));

            Assert.Contains("exit code 5", error.Message);
            Assert.Equal(ClusterSetupService.RuntimeProvisioningPhase, setup.GetStatus("demo")!.Phase);
        }
        finally
        {
            cache.SetValue(null, previous);
        }
    }

    private ManagedDedicatedServerRuntimeResolver CreateResolver(
        string steamCmdScript,
        bool withDedicatedServer,
        DedicatedServerLaunchUpdateMode launchUpdateMode = DedicatedServerLaunchUpdateMode.Update
    )
    {
        var steamCmdDirectory = Path.Combine(_root, "SteamCMD");
        Directory.CreateDirectory(steamCmdDirectory);
        File.WriteAllText(Path.Combine(steamCmdDirectory, "steamcmd.sh"), steamCmdScript);

        var dedicatedServerDirectory = Path.Combine(_root, "SpaceEngineersDedicatedServer");
        if (withDedicatedServer)
        {
            var ds64 = Path.Combine(dedicatedServerDirectory, "DedicatedServer64");
            Directory.CreateDirectory(ds64);
            foreach (
                var name in new[]
                {
                    "SpaceEngineersDedicated.exe",
                    "SpaceEngineers.Game.dll",
                    "VRage.dll",
                    "Sandbox.Game.dll",
                }
            )
                File.WriteAllText(Path.Combine(ds64, name), string.Empty);
        }

        // An installed Magnetar with GitHub unreachable keeps the installed one.
        var magnetarDirectory = Path.Combine(_root, "Magnetar");
        Directory.CreateDirectory(Path.Combine(magnetarDirectory, "Libraries", "MagnetarInterim"));
        File.WriteAllText(Path.Combine(magnetarDirectory, "MagnetarInterim.bin"), string.Empty);

        var options = new ManagedRuntimeOptions
        {
            SteamCmdInstallDirectory = steamCmdDirectory,
            SteamCmdHomeDirectory = Path.Combine(_root, "SteamCmdHome"),
            DedicatedServerInstallDirectory = dedicatedServerDirectory,
            MagnetarInstallDirectory = magnetarDirectory,
            SteamCmdArchiveUrl = "http://127.0.0.1:9/unused",
            DedicatedServerLaunchUpdateMode = launchUpdateMode,
        };

        return new ManagedDedicatedServerRuntimeResolver(
            NullLogger<ManagedDedicatedServerRuntimeResolver>.Instance,
            new OfflineHttpClientFactory(),
            options,
            _credentials,
            new TestLifetime()
        );
    }

    private static ManagedRuntimeInstallPhase LastPhase(
        IEnumerable<ManagedRuntimeInstallProgress> reports,
        ManagedRuntimeInstallComponent component
    ) => reports.Last(r => r.Component == component).Phase;

    private sealed class SyncProgress(Action<ManagedRuntimeInstallProgress> report)
        : IProgress<ManagedRuntimeInstallProgress>
    {
        public void Report(ManagedRuntimeInstallProgress value) => report(value);
    }

    private sealed class OfflineHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new OfflineHandler());
    }

    private sealed class OfflineHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken
        ) => throw new HttpRequestException("No network in tests.");
    }

    private sealed class TestLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => CancellationToken.None;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication() { }
    }
}
