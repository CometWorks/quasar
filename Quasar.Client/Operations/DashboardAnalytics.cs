namespace Quasar.Client.Operations.Analytics;

public sealed record ServerOption(string UniqueName, string DisplayName, string? ClusterId = null, string? NodeLabel = null);

public sealed record ClusterOption(string Id, string DisplayName, IReadOnlyList<ServerOption> Nodes);

public sealed record ProfilerModeOption(string Value, string Label);

public sealed record ProfilerView(
        string ConfiguredMode,
        string LiveMode,
        bool CanConfigure,
        bool HasDefinition,
        bool IsConnected);

public sealed record SummaryChipModel(string Label, string Value);


public sealed record MetricPanel(string Key, string Title, string Subtitle, bool Profiler, bool Extensive);
public sealed record AnalyticsSelection(long From, long To, List<string> Servers, List<string> Clusters, bool Initialized);
public sealed record AnalyticsPageState(IReadOnlyList<ServerOption> Servers, IReadOnlyList<ClusterOption> Clusters,
    List<string> SelectedServers, List<string> SelectedClusters, List<string> Series, List<SummaryChipModel> Summary,
    IReadOnlyList<MetricPanel> Metrics, IReadOnlyList<MetricPanel> PluginPanels, Dictionary<string, ProfilerView> Profiles, bool HasData,
    bool HasHistory, int Retention, bool CanControl, bool Initialized);
