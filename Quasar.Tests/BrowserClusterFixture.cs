using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Quasar.Client.Clusters;
using Quasar.Models;
using Quasar.Services;

namespace Quasar.Tests;

internal sealed class BrowserClusterFixture : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cluster-browser-" + Guid.NewGuid().ToString("N"));
    internal ClusterCatalog Catalog { get; }

    internal BrowserClusterFixture(ClusterDefinition definition)
    {
        var directory = Directory.CreateDirectory(Path.Combine(_root, definition.UniqueName));
        File.WriteAllText(Path.Combine(directory.FullName, "cluster.json"), JsonSerializer.Serialize(definition));
        Catalog = new ClusterCatalog(NullLogger<ClusterCatalog>.Instance,
            new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?> { ["Quasar:ClusterCatalogPath"] = _root }).Build());
    }

    internal static ClusterDashboard Dashboard(ClusterDefinition cluster, bool manage = true) =>
        new(cluster.UniqueName, cluster.DisplayName, cluster.GoalState == DedicatedServerGoalState.On, cluster.Gateway is not null,
            cluster.Gateway is not null, false, null, null, false, null, null, null, null, manage, manage, manage,
            "", null, 0, 0, [], [], "", "");

    public void Dispose()
    {
        Catalog.Dispose();
        Directory.Delete(_root, recursive: true);
    }
}
