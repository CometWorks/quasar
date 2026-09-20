using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Models;
using Quasar.Services;
using Quasar.Host.Contract.V1;
using Xunit;

namespace Quasar.Tests;

public sealed class ClusterCatalogTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(), $"quasar-cluster-catalog-{Guid.NewGuid():N}");

    [Fact]
    public void LoadsAndNormalizesClusterDefinitions()
    {
        string definitionDirectory = Path.Combine(_directory, "demo");
        Directory.CreateDirectory(definitionDirectory);
        File.WriteAllText(Path.Combine(definitionDirectory, "cluster.json"), """
        {
          "uniqueName": " demo ",
          "displayName": "",
          "gatewayUrl": "https://gateway.test/",
          "gatewayAdminTokenEnvironmentVariable": null,
          "hostCommandUrl": "http://host.test:28400/",
          "hostCommandTokenEnvironmentVariable": " HOST_TOKEN ",
          "configProfileId": " survival ",
          "worldTemplateId": null
        }
        """);
        using ClusterCatalog catalog = CreateCatalog();

        ClusterDefinition cluster = Assert.Single(catalog.GetClusters());

        Assert.Equal("demo", cluster.UniqueName);
        Assert.Equal("demo", cluster.DisplayName);
        Assert.Equal("https://gateway.test", cluster.GatewayUrl);
        Assert.Equal(string.Empty, cluster.GatewayAdminTokenEnvironmentVariable);
        Assert.Equal("http://host.test:28400", cluster.HostCommandUrl);
        Assert.Equal("HOST_TOKEN", cluster.HostCommandTokenEnvironmentVariable);
        Assert.Equal("survival", cluster.ConfigProfileId);
        Assert.Equal(string.Empty, cluster.WorldTemplateId);
    }

    [Fact]
    public void SkipsInvalidDefinitionsWithoutDroppingValidOnes()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "valid"));
        Directory.CreateDirectory(Path.Combine(_directory, "invalid"));
        File.WriteAllText(Path.Combine(_directory, "valid", "cluster.json"), """
        { "uniqueName": "valid", "gatewayUrl": "http://gateway.test" }
        """);
        File.WriteAllText(Path.Combine(_directory, "invalid", "cluster.json"), """
        { "uniqueName": "invalid", "gatewayUrl": "file:///tmp/gateway" }
        """);
        using ClusterCatalog catalog = CreateCatalog();

        Assert.Equal("valid", Assert.Single(catalog.GetClusters()).UniqueName);
    }

    [Fact]
    public async Task PersistsClusterGoalAndGatewaySpecAtomically()
    {
        string definitionDirectory = Path.Combine(_directory, "demo");
        Directory.CreateDirectory(definitionDirectory);
        string path = Path.Combine(definitionDirectory, "cluster.json");
        File.WriteAllText(path, """
        { "uniqueName": "demo", "gatewayUrl": "http://gateway.test" }
        """);
        using ClusterCatalog catalog = CreateCatalog();
        var gateway = new GatewaySpec("demo", GatewayGoal.Off, "/bundle/manifest.json",
            new string('a', 64), "r1", [28000, 28016], "/runs/demo");

        await catalog.SetGatewayAsync("demo", gateway);
        await catalog.SetGoalStateAsync("demo", DedicatedServerGoalState.On);

        using ClusterCatalog recovered = CreateCatalog();
        ClusterDefinition cluster = Assert.Single(recovered.GetClusters());
        Assert.Equal(DedicatedServerGoalState.On, cluster.GoalState);
        GatewaySpec persisted = Assert.IsType<GatewaySpec>(cluster.Gateway);
        Assert.Equal(GatewayGoal.On, persisted.Goal);
        Assert.Equal([28000, 28016], persisted.Ports);
        Assert.Contains("\"goalState\": \"On\"", File.ReadAllText(path));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public async Task PackageSelectionSurvivesRestartAndInterruptedReplayWithoutChangingLifecycle()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "demo"));
        File.WriteAllText(Path.Combine(_directory, "demo", "cluster.json"), """
        { "uniqueName": "demo", "gatewayUrl": "http://gateway.test", "goalState": "On",
          "updatedAtUtc": "2026-09-19T12:00:00Z" }
        """);
        using ClusterCatalog catalog = CreateCatalog();
        var installation = new ClusterPackageInstallation(new("1.0.3", 1, 2, 3, new string('a', 64)),
            new string('b', 40), "/staged", []);
        var before = catalog.GetCluster("demo")!;
        var selected = await catalog.SelectPackageAsync("demo", 0, installation, "select-1", default);

        using ClusterCatalog recovered = CreateCatalog();
        Assert.Equal(selected, recovered.GetCluster("demo")!.PackageSelection);
        Assert.Equal(selected, await recovered.SelectPackageAsync("demo", 0, installation, "select-1", default));
        Assert.Equal(before.GoalState, recovered.GetCluster("demo")!.GoalState);
        Assert.Equal(before.Gateway, recovered.GetCluster("demo")!.Gateway);
        Assert.Equal(before.UpdatedAtUtc, recovered.GetCluster("demo")!.UpdatedAtUtc);
        var conflict = await Assert.ThrowsAsync<ClusterOperationConflictException>(() =>
            recovered.SelectPackageAsync("demo", 0, installation, "competing-key", default));
        Assert.Equal("package_selection_conflict", conflict.Code);

        var next = await recovered.SelectPackageAsync("demo", 1, installation, "select-2", default);
        Assert.Equal(2, next.Revision);
        await Assert.ThrowsAsync<ClusterOperationConflictException>(() =>
            recovered.SelectPackageAsync("demo", 0, installation, "select-1", default));
        await recovered.SetGoalStateAsync("demo", DedicatedServerGoalState.Off);
        Assert.Equal(next, recovered.GetCluster("demo")!.PackageSelection);
    }

    [Fact]
    public async Task ConcurrentPackageSelectionsCannotOverwriteEachOther()
    {
        Directory.CreateDirectory(Path.Combine(_directory, "demo"));
        File.WriteAllText(Path.Combine(_directory, "demo", "cluster.json"), """
        { "uniqueName": "demo", "gatewayUrl": "http://gateway.test" }
        """);
        using ClusterCatalog catalog = CreateCatalog();
        var installation = new ClusterPackageInstallation(new("1.0.3", 1, 2, 3, new string('a', 64)),
            new string('b', 40), "/staged", []);
        var attempts = Enumerable.Range(0, 10).Select(async i =>
        {
            try { await catalog.SelectPackageAsync("demo", 0, installation, "select-" + i, default); return true; }
            catch (ClusterOperationConflictException) { return false; }
        });
        Assert.Single(await Task.WhenAll(attempts), succeeded => succeeded);
        Assert.Equal(1, catalog.GetCluster("demo")!.PackageSelection!.Revision);
    }

    [Fact]
    public async Task CreateIsDurableAndCannotReplaceAnExistingCluster()
    {
        using var catalog = CreateCatalog();
        var request = new ClusterCreateRequest("new_cluster", "Cluster", "http://gateway.test", "GATEWAY_TOKEN");
        await catalog.CreateAsync(request, default);
        Assert.Single(catalog.GetClusters());
        await catalog.CreateAsync(request, default);
        using var reloaded = CreateCatalog();
        Assert.Equal(DedicatedServerGoalState.Off, reloaded.GetCluster("new_cluster")!.GoalState);
        await Assert.ThrowsAsync<InvalidOperationException>(() => catalog.CreateAsync(request with { GatewayUrl = "http://another" }, default));
        await Assert.ThrowsAsync<InvalidDataException>(() => catalog.CreateAsync(request with { UniqueName = "../escape" }, default));
    }

    private ClusterCatalog CreateCatalog()
    {
        IConfiguration configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Quasar:ClusterCatalogPath"] = _directory,
            })
            .Build();
        return new ClusterCatalog(NullLogger<ClusterCatalog>.Instance, configuration);
    }
}
