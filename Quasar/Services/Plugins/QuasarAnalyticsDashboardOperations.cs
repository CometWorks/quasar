using Microsoft.AspNetCore.Authorization;
using Magnetar.Protocol.Transport;
using Quasar.Client.Operations.Analytics;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Analytics;
using Quasar.Services.Auth;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

internal sealed class DashboardAnalyticsProjection(QuasarOperationContext caller, bool clusterAccess)
{
    private T Get<T>() where T : notnull => caller.Services.GetRequiredService<T>();
    private AgentRegistry Registry => Get<AgentRegistry>();
    private DedicatedServerCatalog ServerCatalog => Get<DedicatedServerCatalog>();
    private ClusterCatalog ClusterCatalog => Get<ClusterCatalog>();
    private MetricsStoreService MetricsStore => Get<MetricsStoreService>();
    private ProfilerStoreService ProfilerStore => Get<ProfilerStoreService>();
    private bool CanQuery(string cluster) => clusterAccess && caller.User.CanQueryCluster(cluster);
    private Dictionary<string,DedicatedServerDefinition> _serverDefinitionsByName = [];
    private Dictionary<string,AgentRuntimeState> _agentsByUniqueName = [];
    private IReadOnlyList<ServerOption> _serverOptions = [];
    private IReadOnlyList<ClusterOption> _clusterOptions = [];
    private IReadOnlyDictionary<string,IReadOnlyList<string>> _selectedMembers = new Dictionary<string,IReadOnlyList<string>>();
    private IReadOnlyList<string> SelectedTelemetryKeys => _selectedMembers.Values.SelectMany(v => v).ToArray();
    private readonly AnalyticsViewConfig _config = new();
    private bool _selectionInitialized;
    private readonly List<SummaryChipModel> _summaryChips = [];
    private IReadOnlyList<ServerOption> SelectedServerOptions => _serverOptions
        .Where(option => option.ClusterId is { } clusterId ? IsClusterSelected(clusterId) : IsServerSelected(option.UniqueName))
        .ToList();


    internal AnalyticsPageState Read(AnalyticsSelection input, bool canControl)
    {
        var fromUnix = input.From;
        var toUnix = input.To;
        if (fromUnix < 0 || toUnix <= fromUnix || toUnix > DateTimeOffset.MaxValue.ToUnixTimeSeconds()) throw new QuasarOperationException(400,"Invalid chart time range.");
        var spanSeconds = toUnix - fromUnix;
        _config.SelectedUniqueNames = input.Servers ?? [];
        _config.SelectedClusterIds = input.Clusters ?? [];
        _selectionInitialized = input.Initialized;
        _serverOptions = BuildServerOptions();
        _clusterOptions = BuildClusterOptions();
        NormalizeSelection();
        var selected = GetSelectedUniqueNames().ToList();
        _selectedMembers = Registry.ExpandAnalyticsKeys(selected);
        var samplesByServer = new Dictionary<string, MetricSample[]>(StringComparer.OrdinalIgnoreCase);
        foreach (var (uniqueName, members) in _selectedMembers)
        {
            var samples = members
                .Select(member => MetricsStore.GetStore(member))
                .Where(store => store is not null)
                .SelectMany(store => ReadSamplesForRange(store!, fromUnix, toUnix, spanSeconds))
                .ToArray();
            if (samples.Length > 0)
                samplesByServer[uniqueName] = samples;
        }

        var hasScalarData = samplesByServer.Count > 0;
        var hasProfilerData = !hasScalarData && ProfilerStore.HasSamples(fromUnix, toUnix, SelectedTelemetryKeys);

        BuildSummary(samplesByServer);

        var history = Get<PluginStatsHistoryService>();
        return new(_serverOptions, _clusterOptions, _config.SelectedUniqueNames, _config.SelectedClusterIds, selected, _summaryChips,
            AnalyticsMetrics.Panels.Select(p => new MetricPanel(p.Key,p.Title,p.Subtitle,ProfilerAnalyticsMetrics.Find(p.Key) is not null,ProfilerEntryAnalyticsMetrics.Find(p.Key) is not null)).ToArray(),
            Get<PluginStatsStoreService>().GetCatalog(SelectedTelemetryKeys).Select(p => new MetricPanel(p.Key,p.Title,p.Subtitle,false,false)).ToArray(),
            _serverOptions.ToDictionary(s => s.UniqueName,s => GetProfilerView(s.UniqueName),StringComparer.OrdinalIgnoreCase), hasScalarData || hasProfilerData,
            history.GetSources().Count > 0, history.RetentionDays, canControl, _selectionInitialized);
    }

    private IReadOnlyList<ServerOption> BuildServerOptions()
    {
        var names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var definitions = ServerCatalog.GetServers();
        var agents = Registry.GetAgents();
        _serverDefinitionsByName = definitions
            .GroupBy(server => server.UniqueName, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        // A cluster slot is one option: its node instances and epochs are merged under AnalyticsKey.
        _agentsByUniqueName = agents
            .Where(agent => !string.IsNullOrWhiteSpace(agent.AnalyticsKey))
            .GroupBy(agent => agent.AnalyticsKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group
                    .OrderByDescending(agent => agent.IsConnected)
                    .ThenByDescending(agent => agent.LastSeenUtc)
                    .First(),
                StringComparer.OrdinalIgnoreCase);

        // Use the configured server name (DedicatedServerDefinition.DisplayName), not the
        // unique name / id. This drives both the server checkboxes and the chart legends.
        foreach (var server in definitions)
            names[server.UniqueName] = string.IsNullOrWhiteSpace(server.DisplayName) ? server.UniqueName : server.DisplayName;

        var storedKeys = MetricsStore.GetUniqueNames().ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in _agentsByUniqueName.Values)
        {
            var uniqueName = agent.AnalyticsKey;
            // A hot spare records nothing; it shows up here once its node or slot has data in MetricsStore.
            if (agent.Snapshot?.TelemetryPaused == true &&
                !agents.Any(member => string.Equals(member.AnalyticsKey, uniqueName, StringComparison.OrdinalIgnoreCase) &&
                                      storedKeys.Contains(member.TelemetryKey)))
                continue;

            var displayName = agent.IsCluster
                ? uniqueName == agent.TelemetryKey
                    ? $"{agent.ClusterId} / {agent.ClusterSlot} / {agent.ClusterNodeId} / epoch {agent.ClusterEpoch}"
                    : $"{agent.ClusterId} / {agent.ClusterSlot}"
                : agent.ServerDisplayName;
            if (string.IsNullOrWhiteSpace(displayName) || string.Equals(displayName, "Unknown server", StringComparison.OrdinalIgnoreCase))
                displayName = uniqueName;

            names.TryAdd(uniqueName, displayName);
        }

        var mergedKeys = agents
            .Where(agent => agent.AnalyticsKey != agent.TelemetryKey)
            .Select(agent => agent.TelemetryKey)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var uniqueName in storedKeys.Where(key => !mergedKeys.Contains(key)))
            names.TryAdd(uniqueName, uniqueName);

        return names
            .Where(pair => !_agentsByUniqueName.TryGetValue(pair.Key, out var owner) || !owner.IsCluster || CanQuery(owner.ClusterId))
            .OrderBy(pair => pair.Value, StringComparer.OrdinalIgnoreCase)
            .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
            .Select(pair =>
            {
                _agentsByUniqueName.TryGetValue(pair.Key, out var agent);
                if (agent is not { IsCluster: true })
                    return new ServerOption(pair.Key, pair.Value);
                var cluster = ClusterCatalog.GetCluster(agent.ClusterId);
                var clusterName = string.IsNullOrWhiteSpace(cluster?.DisplayName) ? agent.ClusterId : cluster.DisplayName;
                var nodeLabel = string.Equals(agent.ClusterSlot, "world-authority", StringComparison.OrdinalIgnoreCase)
                    ? "World Authority" : agent.ClusterSlot;
                return new ServerOption(pair.Key, $"{clusterName} / {nodeLabel}", agent.ClusterId, nodeLabel);
            })
            .ToList();
    }

    private IReadOnlyList<ClusterOption> BuildClusterOptions()
    {
        var clusters = ClusterCatalog.GetClusters().Where(c => CanQuery(c.UniqueName)).ToDictionary(cluster => cluster.UniqueName,
            cluster => string.IsNullOrWhiteSpace(cluster.DisplayName) ? cluster.UniqueName : cluster.DisplayName,
            StringComparer.OrdinalIgnoreCase);
        foreach (var source in _serverOptions.Where(option => option.ClusterId is not null))
            clusters.TryAdd(source.ClusterId!, source.ClusterId!);
        return clusters.Select(cluster => new ClusterOption(cluster.Key, cluster.Value,
                _serverOptions.Where(option => string.Equals(option.ClusterId, cluster.Key, StringComparison.OrdinalIgnoreCase)).ToList()))
            .OrderBy(cluster => cluster.DisplayName, StringComparer.OrdinalIgnoreCase).ToList();
    }

    private ProfilerView GetProfilerView(string uniqueName)
    {
        _serverDefinitionsByName.TryGetValue(uniqueName, out var definition);
        _agentsByUniqueName.TryGetValue(uniqueName, out var agent);

        var configuredMode = DedicatedServerCatalog.NormalizeProfilerMode(
            definition?.AgentProfilerMode ?? agent?.Snapshot?.ProfilerMode ?? "SafeContinuous");
        var liveMode = string.Empty;
        if (agent is { IsConnected: true, Snapshot: not null } &&
            !string.IsNullOrWhiteSpace(agent.Snapshot.ProfilerMode))
        {
            liveMode = DedicatedServerCatalog.NormalizeProfilerMode(agent.Snapshot.ProfilerMode);
        }

        return new ProfilerView(
            ConfiguredMode: configuredMode,
            LiveMode: liveMode,
            CanConfigure: definition is not null || agent is { IsConnected: true },
            HasDefinition: definition is not null,
            IsConnected: agent is { IsConnected: true });
    }

    private void NormalizeSelection()
    {
        // Upgrade saved per-node/per-epoch selections to the whole logical cluster.
        foreach (var agent in Registry.GetAgents().Where(agent => agent.IsCluster))
        {
            if (_config.SelectedUniqueNames.Contains(agent.AnalyticsKey, StringComparer.OrdinalIgnoreCase)
                || _config.SelectedUniqueNames.Contains(agent.TelemetryKey, StringComparer.OrdinalIgnoreCase))
                _config.SelectedClusterIds.Add(agent.ClusterId);
        }
        var validClusters = _clusterOptions.Select(option => option.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _config.SelectedClusterIds = _config.SelectedClusterIds.Where(validClusters.Contains).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var validIds = _serverOptions.Where(option => option.ClusterId is null).Select(option => option.UniqueName).ToHashSet(StringComparer.OrdinalIgnoreCase);
        _config.SelectedUniqueNames = _config.SelectedUniqueNames
            .Where(validIds.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (!_selectionInitialized && _config.SelectedUniqueNames.Count == 0 && _config.SelectedClusterIds.Count == 0
            && (_serverOptions.Count > 0 || _clusterOptions.Count > 0))
        {
            _config.SelectedUniqueNames = validIds.ToList();
            _config.SelectedClusterIds = validClusters.ToList();
            _selectionInitialized = true;
        }

    }

    private bool IsServerSelected(string uniqueName)
    {
        return _config.SelectedUniqueNames.Any(id => string.Equals(id, uniqueName, StringComparison.OrdinalIgnoreCase));
    }

    private IReadOnlyList<string> GetSelectedUniqueNames()
    {
        return SelectedServerOptions.Select(option => option.UniqueName).ToList();
    }

    private bool IsClusterSelected(string id) => _config.SelectedClusterIds.Contains(id, StringComparer.OrdinalIgnoreCase);

    private static MetricSample[] ReadSamplesForRange(ServerMetricsStore store, long fromUnix, long toUnix, long spanSeconds)
    {
        if (spanSeconds <= 2 * 3600)
            return store.Raw.Read(fromUnix, toUnix);

        if (spanSeconds <= 24 * 3600)
            return store.OneMinute.Read(fromUnix, toUnix);

        return store.OneHour.Read(fromUnix, toUnix);
    }

    private void BuildSummary(IReadOnlyDictionary<string, MetricSample[]> samplesByServer)
    {
        _summaryChips.Clear();

        var latestSamples = new List<MetricSample>();
        foreach (var members in _selectedMembers.Values)
        {
            var latest = members
                .SelectMany(member => MetricsStore.GetStore(member)?.Raw.ReadLatest(1) ?? [])
                .OrderByDescending(sample => sample.TimestampUnixSeconds)
                .Take(1);
            latestSamples.AddRange(latest);
        }

        var hasClusters = _config.SelectedClusterIds.Count > 0;
        _summaryChips.Add(new SummaryChipModel("Servers", Number: _config.SelectedUniqueNames.Count, Format: "0"));
        if (hasClusters)
        {
            _summaryChips.Add(new SummaryChipModel("Clusters", Number: _config.SelectedClusterIds.Count, Format: "0"));
            _summaryChips.Add(new SummaryChipModel("Node series", Number: SelectedServerOptions.Count(option => option.ClusterId is not null), Format: "0"));
        }

        if (latestSamples.Count == 0)
        {
            _summaryChips.Add(new SummaryChipModel("Avg process sim speed", Text: "-"));
            _summaryChips.Add(new SummaryChipModel(hasClusters ? "Total process CPU" : "Avg process CPU", Text: "-"));
            _summaryChips.Add(new SummaryChipModel("Avg simulation CPU", Text: "-"));
            _summaryChips.Add(new SummaryChipModel("Total process memory", Text: "-"));
            if (!hasClusters)
            {
                _summaryChips.Add(new SummaryChipModel("Players", Text: "-"));
                _summaryChips.Add(new SummaryChipModel("PCU", Text: "-"));
                _summaryChips.Add(new SummaryChipModel("Grids", Text: "-"));
                _summaryChips.Add(new SummaryChipModel("Entities", Text: "-"));
            }
            return;
        }
        if (hasClusters)
            _summaryChips.Add(new SummaryChipModel("Reporting processes", Text: $"{latestSamples.Count}/{SelectedServerOptions.Count}"));

        var simSpeed = latestSamples.Average(sample => sample.SimSpeed);
        var cpu = hasClusters ? latestSamples.Sum(sample => sample.CpuPercent) : latestSamples.Average(sample => sample.CpuPercent);
        var simCpu = latestSamples.Average(sample => sample.SimCpuPercent);
        var memory = latestSamples.Sum(sample => sample.MemoryMb > 0f ? sample.MemoryMb : 0f);
        var players = latestSamples.Sum(sample => sample.PlayersOnline);
        var pcu = latestSamples.Sum(sample => sample.UsedPcu);
        var grids = latestSamples.Where(sample => sample.ActiveGridCount >= 0).Sum(sample => sample.ActiveGridCount);
        var entities = latestSamples.Where(sample => sample.ActiveEntityCount >= 0).Sum(sample => sample.ActiveEntityCount);

        // Keep numbers as numbers. The browser formats them with the viewer's
        // culture; the worker's own culture must not decide what the user sees.
        _summaryChips.Add(new SummaryChipModel("Avg process sim speed", Number: simSpeed, Format: "0.000"));
        _summaryChips.Add(new SummaryChipModel(hasClusters ? "Total process CPU" : "Avg process CPU", Number: cpu, Format: "0.0", Suffix: "%"));
        _summaryChips.Add(simCpu is { } averagedCpu
            ? new SummaryChipModel("Avg simulation CPU", Number: averagedCpu, Format: "0.0", Suffix: "%")
            : new SummaryChipModel("Avg simulation CPU", Text: "-"));
        _summaryChips.Add(new SummaryChipModel("Total process memory", Number: memory, Format: "0", Suffix: " MB"));
        if (!hasClusters)
        {
            _summaryChips.Add(new SummaryChipModel("Players", Number: players, Format: "0"));
            _summaryChips.Add(new SummaryChipModel("PCU", Number: pcu, Format: "0"));
            _summaryChips.Add(grids > 0
                ? new SummaryChipModel("Grids", Number: grids, Format: "0")
                : new SummaryChipModel("Grids", Text: "-"));
            _summaryChips.Add(entities > 0
                ? new SummaryChipModel("Entities", Number: entities, Format: "0")
                : new SummaryChipModel("Entities", Text: "-"));
        }

        if (samplesByServer.Values.SelectMany(sampleSet => sampleSet).Any())
        {
            var rangeSimAverage = samplesByServer.Values
                .SelectMany(sampleSet => sampleSet)
                .Average(sample => sample.SimSpeed);
            _summaryChips.Add(new SummaryChipModel("Range Avg Sim", Number: rangeSimAverage, Format: "0.000"));
        }
    }

}
internal static class QuasarAnalyticsDashboardOperations
{
    internal sealed record ProfilerChange(string UniqueName, string Mode);
    internal sealed record ProfilerChangeResult(bool Persisted, bool Applied);
    internal static void AddAnalyticsDashboardOperations(this IServiceCollection services)
    {
        var change = new QuasarOperationMetadata { RequireDefaultPolicies = true, AgentAccess = QuasarAgentAccess.Denied };
        services.AddQuasarOperation<AnalyticsSelection, AnalyticsPageState>(PluginId, "analytics.dashboard", [QuasarPolicyNames.CanView],
            async (caller,input,ct) =>
            {
                var auth = caller.Services.GetRequiredService<IAuthorizationService>();
                var clusters = (await auth.AuthorizeAsync(caller.User,null,QuasarPolicyNames.ClusterQuery)).Succeeded;
                var control = (await auth.AuthorizeAsync(caller.User,null,QuasarPolicyNames.CanControlServers)).Succeeded;
                return new DashboardAnalyticsProjection(caller,clusters).Read(input,control);
            }, change with { Description = "Returns the data sources and panels available in Analytics.", Effect = QuasarOperationEffect.Read });
        services.AddQuasarOperation<EmptyRequest,bool>(PluginId, "analytics.persist", [QuasarPolicyNames.CanView],
            async (caller,_,ct) => { await caller.Services.GetRequiredService<MetricsStoreService>().PersistAllAsync(); return true; }, change with { Description = "Saves the dashboard's analytics preferences." });
        services.AddQuasarOperation<ProfilerChange,ProfilerChangeResult>(PluginId, "analytics.profiler.mode", [QuasarPolicyNames.CanView,QuasarPolicyNames.CanControlServers],
            async (caller,input,ct) =>
            {
                if (input.Mode is not ("Off" or "SafeContinuous" or "DeepContinuous")) throw new QuasarOperationException(400,"Invalid profiler mode.");
                var catalog = caller.Services.GetRequiredService<DedicatedServerCatalog>();
                var registry = caller.Services.GetRequiredService<AgentRegistry>();
                var definition = catalog.GetServer(input.UniqueName);
                var agent = registry.GetAgents().Where(a => a.IsConnected && a.AnalyticsKey.Equals(input.UniqueName,StringComparison.OrdinalIgnoreCase))
                    .OrderByDescending(a => a.LastSeenUtc).FirstOrDefault();
                if (agent is { IsCluster:true } && (!caller.User.CanQueryCluster(agent.ClusterId) ||
                    !(await caller.Services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(caller.User,null,QuasarPolicyNames.ClusterManage)).Succeeded))
                    throw new QuasarOperationException(403,"Cluster access denied.");
                if (definition is not null) { definition.AgentProfilerMode = input.Mode; await catalog.UpsertAsync(definition); }
                if (agent is not null)
                {
                    var result = await registry.SendCommandAndWaitAsync(new ServerCommandEnvelope { UniqueName = agent.UniqueNameKey,AgentId=agent.AgentId,
                        ServerId=agent.ServerKey,CommandType=ServerCommandType.SetProfilerMode,Text=input.Mode }, TimeSpan.FromSeconds(15));
                    if (!result.Success) throw new QuasarOperationException(409,result.Message);
                }
                return new(definition is not null,agent is not null);
            }, change with { Description = "Changes profiler collection mode for the selected server." });
    }
}
