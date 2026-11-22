using System.Text.Json;
using Magnetar.Protocol.Model;
using Microsoft.AspNetCore.Authorization;
using Quasar.Client.Fleet;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

internal static partial class QuasarFleetDashboardOperations
{
    private static readonly QuasarOperationMetadata Change = new() { RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied };
    private static readonly QuasarOperationMetadata Read = Change with { Effect = QuasarOperationEffect.Read };
    public static void AddQuasarFleetDashboardOperations(this IServiceCollection services)
    {
        RegisterStandaloneActions(services);
        RegisterEditor(services);
        services.AddQuasarRead<EmptyRequest>(PluginId, "fleet.overview", [QuasarPolicyNames.CanView],
            (caller, _, _) => Task.FromResult(new QuasarRead(async token => new MemoryStream(
                JsonSerializer.SerializeToUtf8Bytes(await Overview(caller, token), Json), false), "application/json")), Read with { Description = "Returns visible standalone servers, clusters and first setup state." });
        services.AddQuasarOperation<ServerRequest, bool>(PluginId, "fleet.clear", [QuasarPolicyNames.CanControlServers],
            (caller, input, token) => caller.Services.GetRequiredService<DedicatedServerSupervisor>().ClearErrorStatusAsync(input.UniqueName, token), Change with { Description = "Clears the selected server's recorded failure state." });
        services.AddQuasarOperation<ServerRequest, bool>(PluginId, "fleet.kill", [QuasarPolicyNames.CanControlServers], async (caller, input, token) =>
        {
            await caller.Services.GetRequiredService<DedicatedServerSupervisor>().KillStartingServerAsync(input.UniqueName, token);
            return true;
        }, Change with { Description = "Force stops a standalone server during startup." });
        services.AddQuasarOperation<EmptyRequest, bool>(PluginId, "fleet.warmup.retry", [QuasarPolicyNames.CanEditServers], (caller, _, _) =>
        {
            _ = caller.Services.GetRequiredService<ManagedRuntimeWarmupService>().RetryAsync();
            return Task.FromResult(true);
        }, Change with { Description = "Retries setup of the managed server runtime." });

    }

    internal static async Task<FleetDashboard> Overview(QuasarOperationContext caller, CancellationToken token)
    {
        var services = caller.Services;
        var auth = services.GetRequiredService<IAuthorizationService>();
        async Task<bool> Allowed(string policy) => (await auth.AuthorizeAsync(caller.User, null, policy)).Succeeded;
        var edit = await Allowed(QuasarPolicyNames.CanEditServers);
        var config = await Allowed(QuasarPolicyNames.CanEditConfigs);
        var query = await Allowed(QuasarPolicyNames.ClusterQuery);
        var manage = await Allowed(QuasarPolicyNames.ClusterManage);
        var servers = services.GetRequiredService<DedicatedServerCatalog>().GetServers();
        var supervisor = services.GetRequiredService<DedicatedServerSupervisor>();
        var runtimes = supervisor.GetSnapshots();
        var profiles = services.GetRequiredService<QuasarConfigProfileCatalog>().GetProfiles();
        var imports = services.GetRequiredService<ServerWorldImportService>();
        var clusters = services.GetRequiredService<ClusterCatalog>().GetClusters().Where(c => query && caller.User.CanQueryCluster(c.UniqueName)).ToArray();
        var clusterPlayers = services.GetRequiredService<ClusterPlayerService>();
        var observations = clusters.ToDictionary(c => c.UniqueName, c => clusterPlayers.GetSnapshot(c.UniqueName), StringComparer.OrdinalIgnoreCase);
        var agents = services.GetRequiredService<AgentRegistry>().GetAgents();
        var counts = FleetSummary.Build(servers, runtimes, agents, observations);
        var rows = new List<FleetRow>();
        var sources = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        string ProfileName(string id) => profiles.FirstOrDefault(p => p.ConfigProfileId == id)?.Name ?? id;
        foreach (var server in servers)
        {
            token.ThrowIfCancellationRequested();
            var runtime = runtimes.FirstOrDefault(r => r.UniqueName.Equals(server.UniqueName, StringComparison.OrdinalIgnoreCase));
            var agent = agents.Where(a => !a.IsCluster && a.UniqueNameKey.Equals(server.UniqueName, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.IsConnected).ThenByDescending(a => a.LastSnapshotReceivedUtc).FirstOrDefault();
            var m = agent?.Snapshot?.Metrics;
            var name = string.IsNullOrWhiteSpace(server.DisplayName) ? server.UniqueName : server.DisplayName;
            sources[server.UniqueName] = name;
            var details = new List<string>();
            if (m is not null) details.AddRange([$"PCU {m.UsedPcu}/{m.TotalPcu}", $"Mods {m.ModsLoaded}", $"Uptime {TimeSpan.FromSeconds(m.UptimeSeconds):g}"]);
            if (!string.IsNullOrEmpty(server.CpuAffinity)) details.Add($"CPU affinity {server.CpuAffinity}");
            if (runtime is not null)
            {
                if (runtime.LastRestart is { } restart) details.Add($"Last restart: {restart.Cause}, {restart.Outcome}");
                details.AddRange(runtime.ModDownloadFailures.Take(20));
            }
            rows.Add(new(server.UniqueName, name, false, (runtime?.State ?? DedicatedServerProcessState.Stopped).ToString(), server.GoalState.ToString(), server.ServerPort,
                server.ConfigProfileId, ProfileName(server.ConfigProfileId), server.WorldSaveName, agent?.HostDisplayName, server.ServerIP, agent?.IsConnected == true,
                m?.PlayersOnline, m?.MaxPlayers, m is not null && float.IsFinite(m.SimSpeed) ? m.SimSpeed : null,
                m is not null && float.IsFinite(m.ServerCpuLoadPercent) ? m.ServerCpuLoadPercent : null, m?.MemoryWorkingSetMb, runtime?.ProcessId,
                runtime?.HealthState.ToString() ?? "Unknown", runtime?.HealthSummary is { Length: > 0 } health ? health : runtime?.LastMessage ?? "",
                runtime?.LatestErrorLogLine, runtime?.LatestErrorLogKind, details.ToArray(),
                !string.IsNullOrWhiteSpace(server.WorldSaveName) && File.Exists(Path.Combine(server.GetWorldSavePath(), "Sandbox.sbc")),
                (edit || config) && imports.CanImport(server), false, server.UpdatedAtUtc)
            {
                PriorityWarning = runtime?.ProcessPriorityWarning,
                Plugins = agent?.Snapshot is { } snapshot ? DescribePlugins(snapshot, profiles.FirstOrDefault(p => p.ConfigProfileId == server.ConfigProfileId)) : null
            });
        }
        foreach (var cluster in clusters)
        {
            var snapshot = observations[cluster.UniqueName];
            var status = snapshot?.StatusAvailable == true ? snapshot.Status : null;
            var name = string.IsNullOrWhiteSpace(cluster.DisplayName) ? cluster.UniqueName : cluster.DisplayName;
            foreach (var a in agents.Where(a => a.HasClusterIdentity && a.ClusterId.Equals(cluster.UniqueName, StringComparison.OrdinalIgnoreCase)))
                sources.TryAdd(a.AnalyticsKey, $"{name} / {a.ClusterSlot}");
            rows.Add(new(cluster.UniqueName, name, true, status?.Phase.ToString() ?? "Unavailable", cluster.GoalState.ToString(), ClusterOverview.GamePort(cluster),
                cluster.ConfigProfileId, ProfileName(cluster.ConfigProfileId), "", null, null, false, status?.Counts.ConnectedClients, null, null, null, null, null,
                status?.Health.ToString() ?? "Unknown", snapshot?.Error ?? "", null, null, status?.Nodes.Select(n => $"{n.SlotKey} · {n.Role} · {n.State}").ToArray() ?? [],
                false, false, cluster.ActiveDeployment is not null, default));
        }
        var unmanaged = agents.Where(a => a.IsCluster ? query && caller.User.CanQueryCluster(a.ClusterId) && !observations.ContainsKey(a.ClusterId)
            : !sources.ContainsKey(a.UniqueNameKey)).Select(a => new FleetAgent(a.AgentId, a.UniqueNameKey, a.ServerDisplayName, a.HostDisplayName, a.IsCluster ? a.ClusterId : null, a.ClusterSlot, a.ClusterNodeId, a.ClusterEpoch, a.IsConnected, a.Snapshot?.IsRunning == true)).ToArray();
        var consent = services.GetRequiredService<DataHandlingConsentCatalog>().GetSettings();
        var warmup = services.GetRequiredService<ManagedRuntimeWarmupService>().GetSnapshot();
        if (!edit) warmup = warmup with { Components = warmup.Components.Select(c => c with { Path = "" }).ToArray() };
        return new(new(counts.ConfiguredServers, counts.RunningServers, counts.UnobservedClusters, counts.PlayersOnline, counts.PlayerCapacity, counts.ReportingServers,
            counts.AverageSimSpeed, counts.SimulationProcesses, counts.MemoryGiB, counts.MemoryProcesses, counts.HealthWarnings, counts.HealthErrors),
            rows.ToArray(), unmanaged, sources, QuasarActivityOperations.Recent(services, caller.User, 5, includeClusters: query)
                .Select(a => new FleetActivity(a.At, a.DisplayName, a.Message, a.Level, a.ClusterDetails is { } d ? new(d.OperationId, d.Kind, d.StartedAt, d.ErrorCode) : null)).ToArray(),
            edit || config ? services.GetRequiredService<QuasarWorldTemplateCatalog>().GetTemplates().Select(t => new WorldTemplate(t.WorldTemplateId, t.Name, t.Description, t.UpdatedAtUtc, true, 0)).ToArray() : [],
            edit || config ? profiles.Select(p => new FleetProfile(p.ConfigProfileId, p.Name, p.SourceWorldTemplateId)).ToArray() : [],
            warmup, supervisor.HealthMonitoringDisabled, await Allowed(QuasarPolicyNames.CanManageSecurity) && consent.ConsentGranted is null,
            OperatingSystem.IsLinux(), edit, config, await Allowed(QuasarPolicyNames.CanControlServers), manage, query);
    }

    internal static FleetPluginLoad DescribePlugins(AgentSnapshot snapshot, QuasarConfigProfile? profile)
    {
        if (profile is null) return new(snapshot.Metrics.PluginsLoaded, null, null);
        var configured = profile.Plugins.Where(plugin => !string.IsNullOrWhiteSpace(plugin.PluginId)).ToArray();
        if (snapshot.Plugins.Count == 0)
        {
            var loaded = Math.Min(snapshot.Metrics.PluginsLoaded, configured.Length);
            return new(loaded, configured.Length, loaded == configured.Length ? null
                : "Some configured plugins are not loaded. Plugin names are unavailable; check the server logs.");
        }
        var keys = snapshot.Plugins.Where(plugin => plugin.IsLoaded)
            .SelectMany(plugin => GetPluginMatchKeys(plugin.PluginId).Concat(GetPluginMatchKeys(plugin.DisplayName)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var missing = configured.Where(plugin =>
            !GetPluginMatchKeys(plugin.PluginId).Concat(GetPluginMatchKeys(plugin.DisplayName)).Any(keys.Contains))
            .Select(plugin => string.IsNullOrWhiteSpace(plugin.DisplayName) ? plugin.PluginId : plugin.DisplayName).ToArray();
        return new(configured.Length - missing.Length, configured.Length, missing.Length == 0 ? null : "Not loaded: " + string.Join(", ", missing));
    }

    private static IEnumerable<string> GetPluginMatchKeys(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            yield break;

        var trimmed = value.Trim();
        yield return trimmed;

        var normalized = trimmed.Replace('\\', '/').TrimEnd('/');
        var slash = normalized.LastIndexOf('/');
        var fileName = slash >= 0 ? normalized[(slash + 1)..] : normalized;
        if (!string.IsNullOrWhiteSpace(fileName))
        {
            yield return fileName;

            var extension = Path.GetExtension(fileName);
            if (!string.IsNullOrEmpty(extension))
                yield return fileName[..^extension.Length];
        }

        if (slash > 0)
        {
            var parent = normalized[..slash].TrimEnd('/');
            var parentSlash = parent.LastIndexOf('/');
            var parentName = parentSlash >= 0 ? parent[(parentSlash + 1)..] : parent;
            if (!string.IsNullOrWhiteSpace(parentName))
                yield return parentName;
        }
    }

}
