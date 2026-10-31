extern alias Bootstrap;

using System.Net;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
using Quasar.Services.Updates;
using Xunit;
using Coordinator = Bootstrap::Quasar.Bootstrap.LauncherCoordinator;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class PairedRollbackTests : IDisposable
{
    private const string Target = "1.3.1.87";
    private readonly string _root = Path.Combine(Path.GetTempPath(), "quasar-rollback-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.Static | BindingFlags.NonPublic)!;
    private readonly object? _previous;
    private readonly string? _previousCapability = Environment.GetEnvironmentVariable("QUASAR_COORDINATED_ROLLBACKS");

    public PairedRollbackTests()
    {
        _previous = _cache.GetValue(null);
        _cache.SetValue(null, _root);
        Environment.SetEnvironmentVariable("QUASAR_COORDINATED_ROLLBACKS", "true");
    }

    [Fact]
    public async Task ManualRollbackRequestsMatchingInstallerAndUiWithoutPublishingThePointer()
    {
        using var catalog = new CatalogHandler();
        using var updater = CreateUpdater(catalog);
        var active = MagnetarPaths.GetQuasarActiveReleasePath();
        await File.WriteAllTextAsync(active, "previous active pointer");
        await updater.ActivateStagedWebUpdateAsync();
        Assert.Equal("previous active pointer", await File.ReadAllTextAsync(active));
        using var request = JsonDocument.Parse(await File.ReadAllTextAsync(MagnetarPaths.GetQuasarBootstrapUpdateRequestPath()));
        var value = request.RootElement;
        Assert.True(value.GetProperty("allowRollback").GetBoolean());
        Assert.Equal(Target, value.GetProperty("version").GetString());
        Assert.Equal(Target, value.GetProperty("webRelease").GetProperty("version").GetString());
        Assert.Equal(new QuasarUpdateOptions().BootstrapAssetName, value.GetProperty("assetName").GetString());
        var directory = value.GetProperty("webRelease").GetProperty("workingDirectory").GetString()!;
        QuasarWebReleaseLayout.ValidateDirectory(directory);
        Assert.Equal(MagnetarPaths.GetQuasarManagedWebReleaseDirectory(Target), directory);
        Assert.Equal(1, catalog.Requests);
        Assert.Contains("Rolling back Bootstrap and Quasar UI", updater.GetSnapshot().Message);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MissingInstallerOrUnsupportedLauncherCannotFallBackToUiOnly(bool supportsRollback)
    {
        Environment.SetEnvironmentVariable("QUASAR_COORDINATED_ROLLBACKS", supportsRollback ? "true" : null);
        using var catalog = new CatalogHandler { IncludeTargetInstaller = false };
        using var updater = CreateUpdater(catalog);
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => updater.ActivateStagedWebUpdateAsync());
        Assert.Contains(supportsRollback ? "matching Bootstrap installer" : "Update Bootstrap", error.Message);
        Assert.False(File.Exists(MagnetarPaths.GetQuasarActiveReleasePath()));
        Assert.False(File.Exists(MagnetarPaths.GetQuasarBootstrapUpdateRequestPath()));
        Assert.False(Directory.Exists(MagnetarPaths.GetQuasarManagedWebReleaseDirectory(Target)));
        Assert.Equal(supportsRollback ? 1 : 0, catalog.Requests);
    }

    [Theory]
    [InlineData("1.3.1.87", "1.3.2", false, "1.3.1.87", false)]
    [InlineData("1.3.1.87", "1.3.2", true, "1.3.1.87", true)]
    [InlineData("1.3.1.87", "1.3.1.703-pr.299", true, "1.3.1.87", true)]
    [InlineData("1.3.2", "1.3.2", true, "1.3.2", true)]
    [InlineData("1.3.2", "1.3.2", false, null, false)]
    [InlineData("1.3.2", "1.3.1.87", false, null, true)]
    public void LauncherOnlyAllowsDowngradeForExplicitPairedRequests(string target, string current, bool allowRollback, string? webVersion, bool expected)
    {
        var pointer = webVersion is null ? null : new QuasarActiveReleasePointer { Version = webVersion };
        Assert.Equal(expected, Coordinator.CanActivateBootstrapVersion(target, current, allowRollback, pointer));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("1.3.0")]
    public void LauncherRejectsUnpairedOrMismatchedRollback(string? webVersion)
    {
        var pointer = webVersion is null ? null : new QuasarActiveReleasePointer { Version = webVersion };
        Assert.Throws<InvalidDataException>(() => Coordinator.CanActivateBootstrapVersion(Target, "1.3.2", true, pointer));
    }

    private QuasarUpdateService CreateUpdater(CatalogHandler handler)
    {
        var staged = Path.Combine(MagnetarPaths.GetQuasarStagingDirectory(), Target);
        foreach (var name in QuasarWebReleaseLayout.RequiredWebAssetFiles.Prepend(QuasarWebReleaseLayout.WorkerExecutableFileName))
        {
            var path = Path.Combine(staged, name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, "fixture");
        }
        var credentials = new GitHubUpdateCredentialsCatalog(NullLogger<GitHubUpdateCredentialsCatalog>.Instance, new EphemeralDataProtectionProvider());
        _credentials.Add(credentials);
        var updater = new QuasarUpdateService(handler, new QuasarUpdateOptions(),
            new WebServiceOptions { Version = "1.3.2", BootstrapVersion = "1.3.2", LauncherToken = "fixture" },
            credentials, NullLogger<QuasarUpdateService>.Instance);
        typeof(QuasarUpdateService).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(updater,
            new QuasarUpdateSnapshot { Web = new() { Version = Target, IsStaged = true, StagedDirectory = staged } });
        return updater;
    }

    private readonly List<IDisposable> _credentials = [];
    public void Dispose()
    {
        foreach (var credentials in _credentials) credentials.Dispose();
        Environment.SetEnvironmentVariable("QUASAR_COORDINATED_ROLLBACKS", _previousCapability);
        _cache.SetValue(null, _previous);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private sealed class CatalogHandler : HttpMessageHandler, IHttpClientFactory
    {
        public bool IncludeTargetInstaller = true;
        public int Requests;
        public HttpClient CreateClient(string name) => new(this, disposeHandler: false);
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            Assert.Equal("api.github.com", request.RequestUri!.Host);
            Requests++;
            object Release(string version, bool withInstaller) => new
            {
                tag_name = "v" + version,
                assets = withInstaller ? new[] { new { name = new QuasarUpdateOptions().BootstrapAssetName,
                    browser_download_url = "https://fixtures.test/installer-" + version } } : [],
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new[] { Release("1.4.0", true), Release(Target, IncludeTargetInstaller) }),
            });
        }
    }
}
