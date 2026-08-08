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
