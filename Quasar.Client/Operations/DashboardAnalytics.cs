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

/// <summary>
/// One analytics summary chip. A chip is either preformatted text or a raw
/// number that the browser formats with the viewer's culture.
/// </summary>
/// <param name="Label">Visible chip label.</param>
/// <param name="Text">Text for non-numeric values and missing data.</param>
/// <param name="Number">Raw numeric value; when set, it takes precedence.</param>
/// <param name="Format">Numeric format applied in the browser.</param>
/// <param name="Suffix">Unit appended after the formatted number.</param>
public sealed record SummaryChipModel(string Label, string? Text = null, double? Number = null, string? Format = null, string? Suffix = null);


public sealed record MetricPanel(string Key, string Title, string Subtitle, bool Profiler, bool Extensive);
public sealed record AnalyticsSelection(long From, long To, List<string> Servers, List<string> Clusters, bool Initialized);
public sealed record AnalyticsPageState(IReadOnlyList<ServerOption> Servers, IReadOnlyList<ClusterOption> Clusters,
    List<string> SelectedServers, List<string> SelectedClusters, List<string> Series, List<SummaryChipModel> Summary,
    IReadOnlyList<MetricPanel> Metrics, IReadOnlyList<MetricPanel> PluginPanels, Dictionary<string, ProfilerView> Profiles, bool HasData,
    bool HasHistory, int Retention, bool CanControl, bool Initialized);
