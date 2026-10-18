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
        Assert.Contains("libc6:i386 lib32gcc-s1", readiness.SteamCmdFailure);
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
        // Progress<T> posts its callbacks to the thread pool, so the rows can lag the result.
        var snapshot = await WaitForAsync(
            warmup,
            s =>
                s.Components.All(c =>
                    c.State
                        is not ManagedRuntimeComponentState.Pending
                            and not ManagedRuntimeComponentState.Checking
                )
        );
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
            "libc6:i386",
            ManagedDedicatedServerRuntimeResolver.DescribeMissingSteamCmdLibraries(127)
        );
        Assert.Empty(ManagedDedicatedServerRuntimeResolver.DescribeMissingSteamCmdLibraries(1));
        Assert.Empty(ManagedDedicatedServerRuntimeResolver.DescribeMissingSteamCmdLibraries());
    }

    private ManagedDedicatedServerRuntimeResolver CreateResolver(
        string steamCmdScript,
        bool withDedicatedServer
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

    private static async Task<ManagedRuntimeWarmupSnapshot> WaitForAsync(
        ManagedRuntimeWarmupService warmup,
        Func<ManagedRuntimeWarmupSnapshot, bool> condition
    )
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        var snapshot = warmup.GetSnapshot();
        while (!condition(snapshot) && DateTime.UtcNow < deadline)
        {
            await Task.Delay(20);
            snapshot = warmup.GetSnapshot();
        }

        return snapshot;
    }

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
