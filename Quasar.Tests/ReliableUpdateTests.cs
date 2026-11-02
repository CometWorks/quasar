extern alias Bootstrap;
extern alias Updater;

using System.Formats.Tar;
using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
using Quasar.Services.Updates;
using Quasar.Updates;
using Xunit;
using UpdateCli = Bootstrap::Quasar.Bootstrap.UpdateCli;
using Coordinator = Bootstrap::Quasar.Bootstrap.LauncherCoordinator;
using BootstrapOptions = Bootstrap::Quasar.Bootstrap.BootstrapOptions;
using ForegroundOptions = Bootstrap::Quasar.Bootstrap.LauncherForegroundOptions;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class ReliableUpdateTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quasar-reliable-update-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly object? _previous;
    private readonly List<IDisposable> _disposables = [];
    public ReliableUpdateTests()
    {
        _previous = _cache.GetValue(null); _cache.SetValue(null, _root); Directory.CreateDirectory(_root);
        File.WriteAllText(Path.Combine(_root, "appsettings.json"), "{\"adminValue\":42}");
    }

    [Theory]
    [InlineData("1.5.0")]
    [InlineData("1.5.0-rc.1")]
    [InlineData("1.6.0.2")]
    [InlineData("2.0.0")]
    public void LaterVersionsRequireACompletedHealthyBridge(string target)
    {
        Assert.Throws<InvalidOperationException>(() => QuasarUpgradePath.EnsureAllowed(target, _root));
        QuasarUpgradePath.RecordHealthy(_root, _root, "1.4.1"); // A staged worker has no complete package identity.
        Assert.False(QuasarUpgradePath.IsCompleted(_root));
        File.WriteAllText(Path.Combine(_root, LauncherReplacement.ManifestName), "Quasar\n");
        File.WriteAllText(Path.Combine(_root, "release.json"), "{\"version\":\"1.4.1.0\",\"updaterProtocol\":1}");
        QuasarUpgradePath.RecordHealthy(_root, _root, "1.4.1");
        Assert.True(QuasarUpgradePath.IsCompleted(_root));
        QuasarUpgradePath.EnsureAllowed(target, _root);
    }

    [Fact]
    public async Task UIAndManualCliBlockSkippingTheBridgeBeforeDownloadsOrReplacement()
    {
        using var catalog = new PackageCatalog(_root);
        using var updater = Create(catalog, false);
        await updater.UpdateAsync("1.5.0");
        await updater.ProcessUpdateAsync(CancellationToken.None);
        Assert.Equal(UpdateStage.Failed, QuasarUpdateJournal.Read()!.Stage);
        Assert.Contains("1.4.1 migration first", QuasarUpdateJournal.Read()!.Message);
        Assert.Equal(0, catalog.Downloads);
        QuasarUpdateJournal.Write(new QuasarUpdateOperation { Version = "1.5.0", Stage = UpdateStage.Ready });
        var error = new StringWriter();
        Assert.Equal(3, await UpdateCli.RunAsync(["update", "--retry"], new StringWriter(), error));
        Assert.Contains("1.4.1 migration first", error.ToString());
        Assert.False(File.Exists(Path.Combine(_root, "Updates", "LauncherTransaction", "phase")));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CompletePackageStagesBothComponentsAndOnlyVerifiedSupervisorGetsAnActivationRequest(bool supervised)
    {
        using var catalog = new PackageCatalog(_root);
        using var updater = Create(catalog, supervised);
        var operation = await updater.UpdateAsync("1.4.1");
        var duplicate = await updater.UpdateAsync("1.4.1");
        Assert.Equal(operation.Id, duplicate.Id);
        await updater.ProcessUpdateAsync(CancellationToken.None);
        var state = QuasarUpdateJournal.Read()!;
        Assert.Equal(UpdateStage.Ready, state.Stage);
        Assert.Equal(operation.Id, state.Id);
        Assert.Equal("1.4.1", state.Version);
        QuasarWebReleaseLayout.ValidateDirectory(state.PackageDirectory);
        await VerifiedUpdatePackage.VerifyAsync(state.PackageDirectory, CancellationToken.None);
        Assert.False(File.Exists(MagnetarPaths.GetQuasarBootstrapUpdateRequestPath()));
        Assert.False(File.Exists(MagnetarPaths.GetQuasarActiveReleasePath()));
        Assert.Equal("{\"adminValue\":42}", File.ReadAllText(Path.Combine(_root, "appsettings.json")));
        Assert.Contains("Staged safely", state.Message);
        using var reconnected = Create(catalog, supervised);
        Assert.Equal(state.Id, reconnected.GetSnapshot().Operation!.Id);
        Assert.Equal(state.WebStageDirectory, reconnected.GetSnapshot().Web!.StagedDirectory);
        var downloads = catalog.Downloads;
        await updater.ProcessUpdateAsync(CancellationToken.None);
        Assert.Equal(downloads, catalog.Downloads); // Ready is never replayed by reconnect/poll.
    }

    [Theory]
    [InlineData(true, false, "SHA256")]
    [InlineData(false, true, "missing required files")]
    public async Task ChecksumOrIncompleteWorkerFailureCannotActivateHalfAnInstallation(bool corruptChecksum, bool missingWorker, string error)
    {
        using var catalog = new PackageCatalog(_root, corruptChecksum, missingWorker);
        using var updater = Create(catalog, true);
        var operation = await updater.UpdateAsync("1.4.1");
        await updater.ProcessUpdateAsync(CancellationToken.None);
        Assert.Equal(UpdateStage.Failed, QuasarUpdateJournal.Read()!.Stage);
        Assert.Contains(error, QuasarUpdateJournal.Read()!.Message);
        Assert.False(File.Exists(MagnetarPaths.GetQuasarActiveReleasePath()));
        Assert.False(File.Exists(MagnetarPaths.GetQuasarBootstrapUpdateRequestPath()));
        var failed = updater.GetSnapshot().Operation;
        using var restarted = Create(catalog, true);
        Assert.Equal(failed, restarted.GetSnapshot().Operation);
        var retry = await restarted.UpdateAsync(retry: true);
        Assert.Equal(operation.Version, retry.Version);
        Assert.NotEqual(operation.Id, retry.Id);
    }

    [Theory]
    [InlineData(2, "1.4.1", false, "updater protocol")]
    [InlineData(1, "1.4.2", false, "release identity")]
    [InlineData(1, "1.4.1", true, "manifest does not cover")]
    public async Task PackageProtocolIdentityAndCompleteManifestAreRequired(int protocol, string version, bool omitManifestFile, string error)
    {
        using var catalog = new PackageCatalog(_root, protocol: protocol, packageVersion: version, omitManifestFile: omitManifestFile);
        using var updater = Create(catalog, true);
        await updater.UpdateAsync("1.4.1");
        await updater.ProcessUpdateAsync(CancellationToken.None);
        Assert.Equal(UpdateStage.Failed, QuasarUpdateJournal.Read()!.Stage);
        Assert.Contains(error, QuasarUpdateJournal.Read()!.Message);
        Assert.False(File.Exists(MagnetarPaths.GetQuasarActiveReleasePath()));
    }

    [Fact]
    public async Task UnifiedManualApplicationStagesWithoutStoppingOrPartiallyActivating()
    {
        using var catalog = new PackageCatalog(_root);
        using var updater = Create(catalog, false, bootstrapVersion: "1.4.1");
        await updater.UpdateAsync("1.4.1");
        await updater.ProcessUpdateAsync(CancellationToken.None);
        Assert.Equal(UpdateStage.Ready, QuasarUpdateJournal.Read()!.Stage);
        Assert.False(QuasarUpdateJournal.Read()!.NeedsBootstrap);
        Assert.False(File.Exists(MagnetarPaths.GetQuasarActiveReleasePath()));
        Assert.False(File.Exists(MagnetarPaths.GetQuasarBootstrapUpdateRequestPath()));
        // Pointer publication alone must never report successful/running release.
        Assert.NotEqual(UpdateStage.Healthy, updater.GetSnapshot().Operation!.Stage);
    }

    [Fact]
    public async Task FailedAndCompletedOperationsDoNotReplayOnPeriodicChecks()
    {
        using var catalog = new PackageCatalog(_root);
        using var updater = Create(catalog, true, automatic: true);
        var operation = await updater.UpdateAsync("1.4.1");
        QuasarUpdateJournal.Write(operation with { Stage = UpdateStage.Failed, Message = "launch failed" });
        await updater.CheckNowAsync();
        Assert.Equal(UpdateStage.Failed, QuasarUpdateJournal.Read()!.Stage);
        Assert.Equal(operation.Id, QuasarUpdateJournal.Read()!.Id);
        QuasarUpdateJournal.Write(operation with { Stage = UpdateStage.Healthy });
        await updater.CheckNowAsync();
        Assert.Equal(UpdateStage.Healthy, QuasarUpdateJournal.Read()!.Stage);
        Assert.Equal(operation.Id, QuasarUpdateJournal.Read()!.Id);
        Assert.Equal(0, catalog.Downloads);
    }

    [Fact]
    public async Task UnsupervisedBootstrapRejectsLegacyActivationBeforeDownloadingOrReplacingFiles()
    {
        // A mode flag or invented INVOCATION_ID cannot satisfy actual unit ownership.
        using var coordinator = new Coordinator(new BootstrapOptions(), new ForegroundOptions(true), NullLogger<Coordinator>.Instance);
        var method = typeof(Coordinator).GetMethod("TryUpgradeBootstrapAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var request = Activator.CreateInstance(method.GetParameters()[1].ParameterType, nonPublic: true)!;
        request.GetType().GetProperty("Version")!.SetValue(request, "1.4.1");
        var task = (Task)method.Invoke(coordinator, [CancellationToken.None, request])!;
        await Assert.ThrowsAsync<InvalidOperationException>(() => task);
        Assert.False(File.Exists(MagnetarPaths.GetQuasarActiveReleasePath()));
    }

    [Fact]
    public async Task CliHasActionableStatusUsageAndNoFalseSuccessForStoppedHealthyRelease()
    {
        var output = new StringWriter(); var error = new StringWriter();
        Assert.Equal(2, await UpdateCli.RunAsync(["update", "--version"], output, error));
        QuasarUpdateJournal.Write(new QuasarUpdateOperation { Version = "1.4.1", Stage = UpdateStage.Failed, Message = "permissions denied" });
        Assert.Equal(5, await UpdateCli.RunAsync(["update", "--status"], output, error));
        Assert.Contains("permissions denied", output.ToString());
        QuasarUpdateJournal.Write(new QuasarUpdateOperation { Version = "1.4.1", Stage = UpdateStage.Healthy });
        Assert.Equal(3, await UpdateCli.RunAsync(["update", "--version", "1.4.1"], output, error));
        Assert.Contains("No running local", error.ToString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdaterIndependentlyChecksTheArchiveAndIgnoresApplicationExtractedFiles(bool badChecksum)
    {
        using var catalog = new PackageCatalog(_root, corruptChecksum: badChecksum);
        var id = Guid.NewGuid();
        var archive = Path.Combine(_root, "Updates", "Staged", "Package-" + id.ToString("N"), new QuasarUpdateOptions().PackageAssetName);
        Directory.CreateDirectory(Path.GetDirectoryName(archive)!);
        File.WriteAllBytes(archive, catalog.Archive);
        var install = Path.Combine(_root, "application"); Directory.CreateDirectory(install);
        var source = new Updater::Quasar.Updates.QuasarServiceInstallation { InstallDirectory = install, DataDirectory = _root };
        var operation = new Updater::Quasar.Updates.QuasarUpdateOperation { Id = id, Version = "1.4.1", PackageDirectory = "/untrusted/application-extracted-tree" };
        if (badChecksum)
            await Assert.ThrowsAsync<InvalidDataException>(() => Updater::TrustedUpdatePackage.PrepareAsync(source, operation, CancellationToken.None, catalog));
        else
        {
            var prepared = await Updater::TrustedUpdatePackage.PrepareAsync(source, operation, CancellationToken.None, catalog);
            Assert.StartsWith(Path.Combine(install, ".update-cache"), prepared);
            Assert.Equal("worker placeholder", File.ReadAllText(Path.Combine(prepared, QuasarWebReleaseLayout.WorkerExecutableFileName)));
        }
        Assert.False(File.Exists(Path.Combine(install, QuasarWebReleaseLayout.WorkerExecutableFileName)));
        Assert.Equal("{\"adminValue\":42}", File.ReadAllText(Path.Combine(_root, "appsettings.json")));
    }

    [Fact]
    public async Task UpdateMaintenanceAndExplicitDrainOverrideOrdinaryStopPolicy()
    {
        var property = typeof(QuasarServiceInstallation).GetProperty("Current")!;
        var previous = property.GetValue(null);
        using var supervisor = new DedicatedServerSupervisor(null!, null!, null!, null!, null!,
            new WebServiceOptions { PreserveManagedServersOnShutdown = false }, null!, NullLogger<DedicatedServerSupervisor>.Instance);
        try
        {
            property.SetValue(null, new QuasarServiceInstallation { InstallDirectory = _root, DataDirectory = _root });
            Assert.False(supervisor.PreserveServersOnShutdown);
            var marker = Path.Combine(MagnetarPaths.GetQuasarUpdatesDirectory(), "application-maintenance");
            QuasarUpdateJournal.WriteText(marker, Guid.NewGuid().ToString());
            Assert.True(supervisor.PreserveServersOnShutdown);
            File.Delete(marker);
            Assert.False(supervisor.PreserveServersOnShutdown);
            await supervisor.BeginLauncherDrainAsync();
            Assert.True(supervisor.PreserveServersOnShutdown);
            Assert.True(File.Exists(Path.Combine(_root, "supervisor-state.json")));
        }
        finally { property.SetValue(null, previous); }
    }

    [Fact]
    public async Task NativeMaintenancePreservesLocalClusterHostEvenWhenOrdinaryShutdownStopsServers()
    {
        if (!OperatingSystem.IsLinux()) return;
        var current = typeof(QuasarServiceInstallation).GetProperty("Current")!;
        var previous = current.GetValue(null);
        using var supervisor = new DedicatedServerSupervisor(null!, null!, null!, null!, null!,
            new WebServiceOptions { PreserveManagedServersOnShutdown = false }, null!, NullLogger<DedicatedServerSupervisor>.Instance);
        using var child = System.Diagnostics.Process.Start("sleep", "60")!;
        var localRoot = Path.Combine(_root, "LocalHost");
        Directory.CreateDirectory(Path.Combine(localRoot, "state", "gateways"));
        QuasarUpdateJournal.WriteJson(Path.Combine(localRoot, "state", "gateways", "fixture.json"), new { clusterId = "fixture", goal = "On" });
        QuasarUpdateJournal.WriteJson(Path.Combine(localRoot, "host.pid"), new ClusterLocalHost.HostPid(child.Id, ClusterLocalHost.ProcessIdentity(child.Id)!, "fixture", "fixture"));
        using var local = new ClusterLocalHost(null!, null!, null!, new ClusterHostTunnels(),
            new WebServiceOptions { PreserveManagedServersOnShutdown = false }, NullLogger<ClusterLocalHost>.Instance,
            localRoot, true, supervisor);
        try
        {
            current.SetValue(null, new QuasarServiceInstallation { InstallDirectory = _root, DataDirectory = _root });
            QuasarUpdateJournal.WriteText(Path.Combine(MagnetarPaths.GetQuasarUpdatesDirectory(), "application-maintenance"), Guid.NewGuid().ToString());
            await local.StopAsync(CancellationToken.None);
            Assert.False(child.HasExited);
            Assert.NotNull(local.ReadPid());
        }
        finally
        {
            current.SetValue(null, previous);
            if (!child.HasExited) child.Kill();
            await child.WaitForExitAsync();
        }
    }

    [Fact]
    public async Task ExistingSavedCredentialAndKeyringAreReusableByTheUpdaterReader()
    {
        var provider = DataProtectionProvider.Create(new DirectoryInfo(MagnetarPaths.GetQuasarDataProtectionKeyringDirectory()), options => options.SetApplicationName("Quasar"));
        using var catalog = new GitHubUpdateCredentialsCatalog(NullLogger<GitHubUpdateCredentialsCatalog>.Instance, provider, _ => null);
        await catalog.SaveAsync(new GitHubUpdateCredentials { Token = "saved-update-fixture" });
        Assert.Equal("saved-update-fixture", GitHubUpdateTokenReader.ReadSavedToken(NullLogger.Instance));
    }

    private QuasarUpdateService Create(PackageCatalog catalog, bool supervised, string bootstrapVersion = "1.4.0", bool automatic = false)
    {
        var credentials = new GitHubUpdateCredentialsCatalog(NullLogger<GitHubUpdateCredentialsCatalog>.Instance, new EphemeralDataProtectionProvider());
        _disposables.Add(credentials);
        return new(catalog, new QuasarUpdateOptions { AutoApplyUpdates = automatic, AutoStageWebUpdates = false },
            new WebServiceOptions { Version = "1.4.0", BootstrapVersion = bootstrapVersion, LauncherToken = "fixture",
                BootstrapUpdateUnavailableReason = supervised ? null : "No verified supervisor" }, credentials, NullLogger<QuasarUpdateService>.Instance);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables) disposable.Dispose();
        _cache.SetValue(null, _previous); Directory.Delete(_root, true);
    }

    private sealed class PackageCatalog : HttpMessageHandler, IHttpClientFactory
    {
        private readonly byte[] _archive;
        private readonly string _checksum;
        private readonly string _asset = new QuasarUpdateOptions().PackageAssetName;
        public int Downloads;
        public byte[] Archive => _archive;
        public PackageCatalog(string root, bool corruptChecksum = false, bool missingWorker = false, int protocol = 1, string packageVersion = "1.4.1", bool omitManifestFile = false)
        {
            var source = Path.Combine(root, "fixture-source", "Quasar"); Directory.CreateDirectory(source);
            File.WriteAllText(Path.Combine(source, "release.json"), JsonSerializer.Serialize(new { version = packageVersion, updaterProtocol = protocol }));
            File.WriteAllText(Path.Combine(source, "launcher-files.txt"), QuasarWebReleaseLayout.WorkerExecutableFileName + "\n");
            File.WriteAllText(Path.Combine(source, QuasarWebReleaseLayout.WorkerExecutableFileName), "launcher placeholder");
            foreach (var file in QuasarWebReleaseLayout.RequiredWebAssetFiles.Prepend(QuasarWebReleaseLayout.WorkerExecutableFileName))
            {
                if (missingWorker && file == QuasarWebReleaseLayout.RequiredWebAssetFiles[0]) continue;
                var path = Path.Combine(source, file); Directory.CreateDirectory(Path.GetDirectoryName(path)!); File.WriteAllText(path, "worker placeholder");
            }
            File.WriteAllText(Path.Combine(source, "appsettings.json"), "{\"releaseValue\":1}");
            var owned = Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories).Select(path => Path.GetRelativePath(source, path).Replace('\\', '/'))
                .Where(name => name is not ("appsettings.json" or "launcher-files.txt") && (!omitManifestFile || name != "wwwroot/app.css"));
            File.WriteAllLines(Path.Combine(source, "launcher-files.txt"), owned);
            var archivePath = Path.Combine(root, _asset);
            if (_asset.EndsWith(".zip")) ZipFile.CreateFromDirectory(Path.GetDirectoryName(source)!, archivePath);
            else
            {
                using var file = File.Create(archivePath); using var gzip = new GZipStream(file, CompressionMode.Compress);
                TarFile.CreateFromDirectory(Path.GetDirectoryName(source)!, gzip, includeBaseDirectory: false);
            }
            _archive = File.ReadAllBytes(archivePath);
            _checksum = corruptChecksum ? new string('0', 64) : Convert.ToHexString(SHA256.HashData(_archive));
        }
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            HttpContent content;
            if (request.RequestUri!.Host == "api.github.com")
                content = JsonContent.Create(new[] { new { tag_name = "v1.4.1", draft = false, assets = new[] {
                    new { name = _asset, browser_download_url = "https://fixtures.test/package" },
                    new { name = "SHA256SUMS", browser_download_url = "https://fixtures.test/SHA256SUMS" } } } });
            else if (request.RequestUri.AbsolutePath == "/SHA256SUMS") content = new StringContent(_checksum + "  " + _asset + "\n", Encoding.UTF8);
            else { Downloads++; content = new ByteArrayContent(_archive); }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = content });
        }
    }
}
