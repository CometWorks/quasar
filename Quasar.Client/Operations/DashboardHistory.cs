namespace Quasar.Services.Analytics;

public sealed record PluginStatHistoryMetric(string Provider, string Group, string LabelDescription,
    string Field, string Description, string Unit);
public sealed record PluginStatHistoryLabel(string Label, long LastSeenUnix, int Rows);
public sealed record HistorySelection(string? Source = null, string? Provider = null, string? Group = null);
public sealed record HistoryOptions(Dictionary<string, string> Sources, IReadOnlyList<PluginStatHistoryMetric> Fields,
    IReadOnlyList<PluginStatHistoryLabel> Labels);
