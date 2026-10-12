using System.Security.Claims;
using System.Reflection;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Services;
using Quasar.Services.Auth;
using Quasar.Services.Plugins;
using Quasar.Services.Updates;
using Xunit;

namespace Quasar.Tests;

[Collection("Exact server creation")]
public sealed class AutomaticUpdateTests
{
    [Fact]
    public void AutomaticReleasesRequireMatchingAssetsAndNeverDowngradeEitherComponent()
    {
        var newestUi = new QuasarUpdateCandidate { Version = "1.4.2", IsNewer = true };
        var pairedUi = newestUi with { Version = "1.4.1" };
        var launcher = pairedUi with { IsNewer = false, IsCurrent = true };
        var pair = QuasarUpdateService.SelectAutomaticRelease([newestUi, pairedUi], [launcher]);
        Assert.Equal("1.4.1", pair.Web!.Version);
        Assert.Equal(pair.Web.Version, pair.Bootstrap!.Version);
        Assert.Null(QuasarUpdateService.SelectAutomaticRelease([newestUi], [launcher]).Web);
        Assert.Null(QuasarUpdateService.SelectAutomaticRelease([pairedUi], [launcher with { IsCurrent = false }]).Web);
        Assert.Null(QuasarUpdateService.SelectAutomaticRelease([pairedUi with { IsNewer = false, IsCurrent = true }], [launcher]).Web);
        Assert.NotNull(QuasarUpdateService.SelectAutomaticRelease([pairedUi with { IsNewer = false, IsCurrent = true }], [launcher with { IsNewer = true, IsCurrent = false }]).Bootstrap);
    }

    [Theory]
    [InlineData(true, true, "old", ">=1.3.0.4", "1.3.1", true)]
    [InlineData(false, true, "old", "", "1.3.1", false)]
    [InlineData(true, false, "old", "", "1.3.1", false)]
    [InlineData(true, true, "new", "", "1.3.1", false)]
    [InlineData(true, true, "", "", "1.3.1", false)]
    [InlineData(true, true, "old", ">=1.4.0", "1.3.1", false)]
    [InlineData(true, true, "old", ">=1.3.1", "1.3.1-beta", false)]
    [InlineData(true, true, "old", "[1,2)", "1.3.1", false)]
    [InlineData(true, true, "old", "", "unknown", false)]
    public void PluginAutomationRequiresAnInstalledEnabledCompatibleChange(bool installed, bool enabled,
        string commit, string requirement, string current, bool expected)
    {
        var entry = new QuasarUiPluginHubEntry { Commit = "new", QuasarVersion = requirement };
        var state = new QuasarUiPluginInstallState { Installed = installed, Enabled = enabled, InstalledCommit = commit };
        Assert.Equal(expected, QuasarUiPluginHubCatalogService.CanAutomaticallyUpdate(entry, state, current));
        state.Error = "Broken manifest";
        Assert.False(QuasarUiPluginHubCatalogService.CanAutomaticallyUpdate(entry, state, current));
    }

    [Fact]
    public void PluginNoticesAreOptInAdminOnlyAndDeduplicateByCatalogRevision()
    {
        Assert.False(new NotificationPreferences().UiPluginUpdates);
        var entry = new QuasarUiPluginHubEntry { CatalogId = "viewer", Commit = "next", FriendlyName = "Entity Viewer" };
        var state = new QuasarUiPluginInstallState { Installed = true, InstalledCommit = "old" };
        var admin = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, QuasarRoles.Admin)], "test"));
        var first = Assert.Single(UpdateNotices.UiPlugins([entry], _ => state, admin));
        Assert.Equal("/settings/ui-plugins", first.Url);
        Assert.Equal(first.Key, Assert.Single(UpdateNotices.UiPlugins([entry], _ => state, admin)).Key);
        Assert.Empty(UpdateNotices.UiPlugins([entry], _ => state, new ClaimsPrincipal(new ClaimsIdentity([], "test"))));
        state.InstalledCommit = entry.Commit;
        Assert.Empty(UpdateNotices.UiPlugins([entry], _ => state, admin));
    }

    [Fact]
    public async Task PairedRequestDoesNotPublishActivePointerAndPoliciesSurviveRestart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "quasar-auto-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = cache.GetValue(null);
        cache.SetValue(null, directory);
        try
        {
            using var credentials = new GitHubUpdateCredentialsCatalog(NullLogger<GitHubUpdateCredentialsCatalog>.Instance, new EphemeralDataProtectionProvider());
            var options = new QuasarUpdateOptions();
            using var updater = new QuasarUpdateService(new NoNetwork(), options,
                new WebServiceOptions { Version = "1.3.0", BootstrapVersion = "1.3.0", LauncherToken = "test" }, credentials,
                NullLogger<QuasarUpdateService>.Instance);
            await updater.SetAutoApplyUpdatesAsync(true);
            await updater.SetAutoUpdateUiPluginsAsync(true);
            using var settings = JsonDocument.Parse(await File.ReadAllTextAsync(Path.Combine(directory, "appsettings.json")));
            var policy = settings.RootElement.GetProperty("Quasar").GetProperty("Updates");
            Assert.True(policy.GetProperty("AutoApplyUpdates").GetBoolean());
            Assert.True(policy.GetProperty("AutoUpdateUiPlugins").GetBoolean());
            typeof(QuasarUpdateService).GetField("_snapshot", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(updater,
                new QuasarUpdateSnapshot { Bootstrap = new() { Version = "1.4.0", IsNewer = true, AssetName = "installer" } });
            var pointer = new QuasarActiveReleasePointer { Version = "1.4.0", FileName = "/prepared/Quasar", WorkingDirectory = "/prepared" };
            var request = typeof(QuasarUpdateService).GetMethod("RequestBootstrapUpdateCoreAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
            await (Task)request.Invoke(updater, [pointer, CancellationToken.None])!;
            Assert.False(File.Exists(MagnetarPaths.GetQuasarActiveReleasePath()));
            using var document = JsonDocument.Parse(await File.ReadAllTextAsync(MagnetarPaths.GetQuasarBootstrapUpdateRequestPath()));
            Assert.Equal("1.4.0", document.RootElement.GetProperty("webRelease").GetProperty("version").GetString());
            Assert.Equal("1.4.0", document.RootElement.GetProperty("version").GetString());
        }
        finally { cache.SetValue(null, previous); Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task MaintenanceLeaseSerializesActivationAndSurvivesCancellation()
    {
        var cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.Static | BindingFlags.NonPublic)!;
        var previous = cache.GetValue(null);
        var directory = Path.Combine(Path.GetTempPath(), "quasar-lease-" + Guid.NewGuid().ToString("N"));
        cache.SetValue(null, directory);
        try
        {
            using (await Quasar.Updates.QuasarMaintenanceLease.AcquireAsync(CancellationToken.None))
            {
                using var cancelled = new CancellationTokenSource(250);
                await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
                { using var blocked = await Quasar.Updates.QuasarMaintenanceLease.AcquireAsync(cancelled.Token); });
            }
            using var released = await Quasar.Updates.QuasarMaintenanceLease.AcquireAsync(CancellationToken.None);
        }
        finally { cache.SetValue(null, previous); Directory.Delete(directory, true); }
    }

    private sealed class NoNetwork : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new InvalidOperationException("No network expected.");
    }
}
