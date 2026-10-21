using System.Reflection;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using Xunit;

namespace Quasar.Tests;

/// <summary>#1331: new clusters get Bugfixes and Performance with their default settings; an opt-out sticks.</summary>
[Collection("Exact server creation")]
public sealed class ClusterDefaultPluginsTests : IDisposable
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cluster-default-plugins-" + Guid.NewGuid().ToString("N"));
    private readonly FieldInfo _cache = typeof(MagnetarPaths).GetField("_cachedQuasarDirectory", BindingFlags.NonPublic | BindingFlags.Static)!;
    private readonly object? _previousRoot;

    public ClusterDefaultPluginsTests()
    {
        _previousRoot = _cache.GetValue(null);
        _cache.SetValue(null, _root);
    }

    [Fact]
    public void DefaultsAreTheMagnetarHubBugfixesAndPerformancePluginsAtTheirLatestCommit()
    {
        var profile = new QuasarConfigProfile();
        var added = QuasarDefaultPlugins.Apply(profile, enable: true);

        Assert.Equal(["bugfixes", "454B0F55-C727-4DAB-B1A9-5AE6050A0932"], added.Select(p => p.PluginId));
        Assert.Equal(["Bugfixes", "Performance"], profile.Plugins.Select(p => p.DisplayName));
        // An empty version is the hub's current commit; no plugin configuration is written, so the SDK defaults apply.
        Assert.All(profile.Plugins, p => Assert.Equal("", p.SelectedVersion));
        Assert.Equal(QuasarDefaultPlugins.PluginIds, profile.OfferedDefaultPlugins);
        Assert.Empty(QuasarDefaultPlugins.Pending(profile));
    }

    [Fact]
    public void RemovedOrDeclinedDefaultsAreNeverOfferedAgain()
    {
        var profile = new QuasarConfigProfile();
        QuasarDefaultPlugins.Apply(profile, enable: true);
        profile.Plugins.RemoveAll(p => p.PluginId == QuasarDefaultPlugins.PerformancePluginId);
        Assert.Empty(QuasarDefaultPlugins.Apply(profile, enable: true));
        Assert.Equal([QuasarDefaultPlugins.BugfixesPluginId], profile.Plugins.Select(p => p.PluginId));

        var declined = new QuasarConfigProfile();
        Assert.Empty(QuasarDefaultPlugins.Apply(declined, enable: false));
        Assert.Empty(declined.Plugins);
        Assert.Empty(QuasarDefaultPlugins.Apply(declined, enable: true));
        Assert.Empty(declined.Plugins);
    }

    [Fact]
    public void AProfileThatAlreadySelectsADefaultKeepsItsVersionAndGetsOnlyTheOther()
    {
        var profile = new QuasarConfigProfile { Plugins = [new() { PluginId = "BUGFIXES", DisplayName = "Bugfixes", SelectedVersion = new('a', 40) }] };
        Assert.Equal(["Performance"], QuasarDefaultPlugins.Pending(profile).Select(p => p.DisplayName));
        QuasarDefaultPlugins.Apply(profile, enable: true);
        Assert.Equal(2, profile.Plugins.Count);
        Assert.Equal(new string('a', 40), profile.Plugins.Single(p => p.PluginId == "BUGFIXES").SelectedVersion);
    }

    [Fact]
    public async Task OfferedDefaultsSurviveTheCatalogAndOlderProfilesKeepTheirJsonAndHash()
    {
        var older = new QuasarConfigProfile { ConfigProfileId = "older", Name = "Older", UpdatedAtUtc = DateTimeOffset.UnixEpoch };
        string olderJson = JsonSerializer.Serialize(older, Json);
        Assert.DoesNotContain("offeredDefaultPlugins", olderJson);
        // The pending-update check hashes the profile; a profile never offered the defaults hashes as before.
        Assert.Equal(Quasar.ClusterDeployment.ClusterDeploymentFiles.Hash(System.Text.Encoding.UTF8.GetBytes(olderJson)),
            ClusterUpdatePreparationService.ProfileHash(older));

        var profile = new QuasarConfigProfile { ConfigProfileId = "cluster", Name = "Cluster" };
        QuasarDefaultPlugins.Apply(profile, enable: true);
        profile.Plugins.RemoveAll(p => p.PluginId == QuasarDefaultPlugins.BugfixesPluginId);
        using (var catalog = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance))
            await catalog.UpsertAsync(profile);
        using var reloaded = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        var saved = reloaded.GetProfile("cluster")!;
        Assert.Equal(["Performance"], saved.Plugins.Select(p => p.DisplayName));
        Assert.Empty(QuasarDefaultPlugins.Pending(saved));
    }

    [Theory]
    [InlineData(true, false, new[] { "Bugfixes", "Performance" })]
    [InlineData(false, false, new string[0])]
    [InlineData(true, true, new[] { "Bugfixes" })]
    public async Task GuidedSetupPutsTheDefaultsIntoTheClustersCopyOnly(bool enable, bool performanceRemovedBefore, string[] expected)
    {
        var credentials = new ClusterCredentialStore(new EphemeralDataProtectionProvider(), Path.Combine(_root, "credentials.json"));
        var hosts = new ClusterHostCatalog(credentials, Path.Combine(_root, "hosts"));
        await hosts.RegisterAsync("one", "One", "10.1.0.1", 18400, default);
        using var clusters = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, new ConfigurationBuilder().Build());
        using var servers = new DedicatedServerCatalog(NullLogger<DedicatedServerCatalog>.Instance);
        using var profiles = new QuasarConfigProfileCatalog(NullLogger<QuasarConfigProfileCatalog>.Instance);
        using var worlds = new QuasarWorldTemplateCatalog(NullLogger<QuasarWorldTemplateCatalog>.Instance);
        var source = new QuasarConfigProfile { ConfigProfileId = "profile", Name = "Profile" };
        if (performanceRemovedBefore)
        {
            // An earlier cluster's profile, from which the operator removed Performance.
            QuasarDefaultPlugins.Apply(source, enable: true);
            source.Plugins.RemoveAll(p => p.PluginId == QuasarDefaultPlugins.PerformancePluginId);
        }
        await profiles.UpsertAsync(source);
        string sourceJson = JsonSerializer.Serialize(profiles.GetProfile("profile"), Json);
        string sourceWorld = Path.Combine(_root, "source");
        Directory.CreateDirectory(sourceWorld);
        File.WriteAllText(Path.Combine(sourceWorld, "Sandbox.sbc"), "<MyObjectBuilder_Checkpoint />");
        var world = await worlds.ImportAsync("World", "", sourceWorld);
        // The Host executor is unreachable, so setup stops right after binding the profile snapshot.
        using var http = new HttpClient(new Offline());
        var setup = new ClusterSetupService(clusters, hosts, new ClusterHostClient(http, credentials), credentials,
            null!, null!, null!, null!, worlds, profiles, null!, new ClusterOperationStore(Path.Combine(_root, "operations")), servers, null!);
        var request = new ClusterSetupRequest("demo", "Demo", world.WorldTemplateId, "profile", "one", 28000, [new("one", 2)],
            DefaultPlugins: enable);

        var result = await setup.RunAsync(request, "first", "test", default);

        Assert.Equal(ClusterOperationState.Failed, result.State);
        var snapshot = JsonSerializer.Deserialize<QuasarConfigProfile>(
            File.ReadAllText(Path.Combine(_root, "ClusterSetup/demo/profile.json")), Json)!;
        Assert.Equal(expected, snapshot.Plugins.Select(p => p.DisplayName).Order());
        Assert.Equal(QuasarDefaultPlugins.PluginIds.Order(), snapshot.OfferedDefaultPlugins!.Order());
        Assert.Equal(sourceJson, JsonSerializer.Serialize(profiles.GetProfile("profile"), Json));
    }

    [Fact]
    public void ARequestSavedBeforeTheOptionResumesWithTheDefaultsOn()
    {
        var request = JsonSerializer.Deserialize<ClusterSetupRequest>(
            """{"uniqueName":"demo","displayName":"Demo","worldTemplateId":"world","configProfileId":"profile","gatewayHost":"one","playerPort":28000,"machines":[{"hostId":"one","regularNodes":2}],"hotSpares":1}""", Json)!;
        Assert.True(request.DefaultPlugins);
    }

    private sealed class Offline : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            throw new HttpRequestException("offline");
    }

    public void Dispose()
    {
        _cache.SetValue(null, _previousRoot);
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
