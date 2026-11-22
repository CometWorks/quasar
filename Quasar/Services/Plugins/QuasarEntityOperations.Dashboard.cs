using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Plugins;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Quasar.Services.Analytics;
using Browser = Quasar.Client.Operations;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

public static partial class QuasarEntityOperations
{
    public sealed record DashboardListRequest(string Key, string Type);
    private static readonly QuasarOperationMetadata DashboardRead = new() { Effect = QuasarOperationEffect.Read, AgentAccess = QuasarAgentAccess.Denied };
    private static async Task<bool> Allowed(QuasarOperationContext caller, string policy) =>
        (await caller.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(caller.User, null, policy)).Succeeded;

    private static async Task Require(QuasarOperationContext caller, string policy)
    {
        if (!await Allowed(caller, policy)) throw new QuasarOperationException(403, "Permission denied.");
    }

    private static async Task CheckObservedOwner(QuasarOperationContext caller, EntityQueryRow row, string? expectedAgent, string? expectedTelemetry)
    {
        if (expectedAgent is null && expectedTelemetry is null) return;
        await Require(caller, QuasarPolicyNames.CanControlServers);
        if (row.ViewingSource.Agent.IsCluster) await Require(caller, QuasarPolicyNames.ClusterManage);
        if (row.DeletionSource is not { } owner || owner.Agent.AgentId != expectedAgent || owner.TelemetryKey != expectedTelemetry)
            throw new QuasarOperationException(409, "The entity's owning node changed. Refresh the entity list first.");
    }

    private static void AddDashboardOperations(IServiceCollection services)
    {
        services.AddQuasarOperation<HistorySelection, HistoryOptions>(PluginId, "analytics.history.options", [QuasarPolicyNames.CanView], async (caller, input, _) =>
        {
            await Require(caller, QuasarPolicyNames.CanView);
            var history = caller.Services.GetRequiredService<PluginStatsHistoryService>();
            var names = caller.Services.GetRequiredService<DedicatedServerCatalog>().GetServers().ToDictionary(s => s.UniqueName,
                s => string.IsNullOrWhiteSpace(s.DisplayName) ? s.UniqueName : s.DisplayName, StringComparer.OrdinalIgnoreCase);
            var clusters = caller.Services.GetRequiredService<ClusterCatalog>().GetClusters();
            var allowedClusters = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var cluster in clusters.Where(c => caller.User.CanQueryCluster(c.UniqueName)))
            {
                var key = PluginStatsHistoryService.ClusterKey(cluster.UniqueName);
                allowedClusters.Add(key);
                names[key] = (string.IsNullOrWhiteSpace(cluster.DisplayName) ? cluster.UniqueName : cluster.DisplayName) + " (cluster)";
            }
            var sources = history.GetSources().Where(k => !k.StartsWith(PluginStatsHistoryService.ClusterKeyPrefix, StringComparison.OrdinalIgnoreCase) || allowedClusters.Contains(k))
                .ToDictionary(key => key, key => names.GetValueOrDefault(key, key), StringComparer.OrdinalIgnoreCase);
            if (input.Source is null || !sources.ContainsKey(input.Source)) return new(sources, [], []);
            var fields = history.GetFields(input.Source);
            var labels = fields.Any(f => f.Provider == input.Provider && f.Group == input.Group)
                ? history.GetLabels(input.Source, input.Provider!, input.Group!) : [];
            return new(sources, fields, labels);
        }, DashboardRead with { Description = "Lists available plugin history series and labels for the selected source." });
        services.AddQuasarOperation<EmptyRequest, IReadOnlyList<BrowserExtension>>(PluginId, "dashboard.extensions", [QuasarPolicyNames.CanView],
            async (caller, _, _) =>
            {
                await Require(caller, QuasarPolicyNames.CanView);
                return await caller.Services.GetRequiredService<QuasarUiPluginCatalog>().AuthorizedExtensionsAsync(caller);
            }, DashboardRead with { Description = "Lists browser extensions enabled for this panel and user." });
        services.AddQuasarOperation<EmptyRequest, Browser.EntityTargets>(PluginId, "entities.targets", [QuasarPolicyNames.CanView],
            async (caller, _, _) =>
            {
                await Require(caller, QuasarPolicyNames.CanView);
                var clusters = await Allowed(caller, QuasarPolicyNames.ClusterQuery);
                var agents = caller.Services.GetRequiredService<AgentRegistry>().GetAgents().Where(a => a.IsConnected &&
                    (!a.IsCluster || clusters && caller.User.CanQueryCluster(a.ClusterId))).ToList();
                var servers = caller.Services.GetRequiredService<DedicatedServerCatalog>();
                var targets = agents.Where(a => !a.IsCluster).Select(a =>
                {
                    var name = servers.GetServer(a.UniqueNameKey)?.DisplayName;
                    return new Browser.EntityTarget("agent:" + a.AgentId, string.IsNullOrWhiteSpace(name) ? a.ServerDisplayName : name, a.UniqueNameKey, false);
                }).ToList();
                if (clusters)
                    targets.AddRange(caller.Services.GetRequiredService<ClusterCatalog>().GetClusters().Where(c => caller.User.CanQueryCluster(c.UniqueName))
                        .Select(c => new Browser.EntityTarget("cluster:" + c.UniqueName, string.IsNullOrWhiteSpace(c.DisplayName) ? c.UniqueName : c.DisplayName, c.UniqueName, true)));
                return new(targets.OrderBy(t => t.DisplayName, StringComparer.OrdinalIgnoreCase).ToArray(), await Allowed(caller, QuasarPolicyNames.CanControlServers),
                    await Allowed(caller, QuasarPolicyNames.ClusterManage), agents.Select(a => OwnerKey(a.AgentId, a.TelemetryKey)).ToArray());
            }, DashboardRead with { Description = "Lists connected entity viewers and authorized clusters." });
        services.AddQuasarRead<DashboardListRequest>(PluginId, "entities.table", [QuasarPolicyNames.CanView], async (caller, input, ct) =>
        {
            await Require(caller, QuasarPolicyNames.CanView);
            var service = caller.Services.GetRequiredService<EntityService>();
            var type = Pick(Types, input.Type, "All", "Type");
            EntityQueryResult entities;
            if (input.Key.StartsWith("cluster:", StringComparison.Ordinal))
            {
                await Require(caller, QuasarPolicyNames.ClusterQuery);
                entities = await Query(() => service.QueryClusterAsync(RequireCluster(caller, input.Key[8..]), Filter(type, null, 500), token: ct));
            }
            else
            {
                var agent = caller.Services.GetRequiredService<AgentRegistry>().GetAgents().FirstOrDefault(a =>
                    a.IsConnected && !a.IsCluster && "agent:" + a.AgentId == input.Key) ?? throw new QuasarOperationException(404, "Server disconnected.");
                entities = await Query(() => service.QueryAsync(agent, Filter(type, null, 500), ct));
            }
            var table = new Browser.EntityQueryResult(entities.Rows.Select(r =>
            {
                var e = r.Entity;
                return new Browser.EntityQueryRow(new(e.EntityId, e.DisplayName, e.TypeTag, e.SubType, EntityPresentation.Name(e), EntityPresentation.Details(e),
                    EntityPresentation.TypeLabel(e.TypeTag), EntityPresentation.ItemAmount(e), e.BlockCount, e.Pcu, e.SizeMeters, e.OwnerName, e.OwnerSteamId,
                    $"{e.PositionX:0}, {e.PositionY:0}, {e.PositionZ:0}"), r.NodeLabel, r.ViewingSource.Agent.AgentId, r.ViewingSource.TelemetryKey,
                    r.DeleteUnavailableReason, r.Observations.Select(o => OwnerKey(o.Agent.AgentId, o.TelemetryKey)).ToArray());
            }).ToArray(), entities.TotalCount, entities.TotalEntityCount, entities.CapturedAtUtc, entities.Warnings, entities.QueriedNodes, entities.ExpectedNodes);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(table, JsonSerializerOptions.Web);
            return new QuasarRead(_ => Task.FromResult<Stream>(new MemoryStream(bytes, writable: false)), "application/json", Length: bytes.LongLength);
        }, DashboardRead with { Deadline = ClusterDeadline, Description = "Streams the live entity table with verified node identities and viewer metadata." });
    }

    private static string OwnerKey(string id, string telemetry) => id + "\u001f" + telemetry;
}
