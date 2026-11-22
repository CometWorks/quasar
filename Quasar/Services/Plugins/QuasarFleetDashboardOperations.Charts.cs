using Microsoft.AspNetCore.Authorization;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Analytics;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

internal static partial class QuasarFleetDashboardOperations
{
    // Current slot keys, process keys and per-cluster plugin history keys are used by different charts.
    // Archived slot hashes do not include a reversible cluster ID, so a cluster-scoped principal
    // needs a current owner match before reading one.
    internal static async Task<string[]> VisibleChartSources(QuasarOperationContext caller, string[]? requested)
    {
        if (requested is null || requested.Length == 0) return [];
        bool IsClusterKey(string key)
        {
            static bool HashAfter(string value, string prefix, int length) =>
                value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && value.Length == prefix.Length + length
                    && value.AsSpan(prefix.Length).IndexOfAnyExcept("0123456789abcdefABCDEF") == -1;
            return HashAfter(key, MetricsStoreService.ClusterSlotKeyPrefix, 64)
                || HashAfter(key, PluginStatsHistoryService.ClusterKeyPrefix, 16)
                || HashAfter(key, "cluster-", 64);
        }
        var keys = requested.Where(key => !string.IsNullOrWhiteSpace(key)).ToArray();
        if (!keys.Any(IsClusterKey)) return keys;
        var auth = caller.Services.GetRequiredService<IAuthorizationService>();
        bool query = (await auth.AuthorizeAsync(caller.User, null, QuasarPolicyNames.ClusterQuery)).Succeeded;
        if (query && (!caller.User.IsQuasarServicePrincipal() || caller.User.CanQueryCluster("*"))) return keys;
        var allowed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (query)
        {
            foreach (var cluster in caller.Services.GetRequiredService<ClusterCatalog>().GetClusters().Where(c => caller.User.CanQueryCluster(c.UniqueName)))
                allowed.Add(PluginStatsHistoryService.ClusterKey(cluster.UniqueName));
            foreach (var agent in caller.Services.GetRequiredService<AgentRegistry>().GetAgents().Where(a => a.IsCluster && caller.User.CanQueryCluster(a.ClusterId)))
            {
                allowed.Add(agent.AnalyticsKey);
                allowed.Add(agent.TelemetryKey);
            }
        }
        return keys.Where(key => !IsClusterKey(key) || allowed.Contains(key)).ToArray();
    }
}
