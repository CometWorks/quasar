using Magnetar.Protocol.Model;

namespace Quasar.Services.Analytics;

/// <summary>
/// Describes a single analytics metric: how to read its value from a sample, when that value is
/// meaningful, and how the chart should present it (decimals, units, Y-axis bounds). This is the
/// single source of truth shared by the Analytics page (panel titles) and the series endpoint
/// (<see cref="AnalyticsSeriesService"/>), so the two can never drift apart.
/// </summary>
public sealed record AnalyticsMetric(
    string Key,
    string Title,
    string Subtitle,
    Func<MetricSample, double> Selector,
    Func<MetricSample, bool> IsAvailable,
    bool RequiresZero,
    int Decimals,
    bool Kilo,
    double? FixedMax,
    bool DynamicMaxStep5);

public sealed record AnalyticsPanelDefinition(string Key, string Title, string Subtitle);

public sealed record ProfilerAnalyticsMetric(
    string Key,
    string Title,
    string Subtitle,
    Func<ProfilerTimingBreakdown, double> Selector,
    bool RequiresZero,
    int Decimals,
    bool Kilo,
    double? FixedMax);

public static class AnalyticsMetrics
{
    // Order here is the default panel order on the Analytics page.
    public static readonly IReadOnlyList<AnalyticsMetric> All =
    [
        new("simspeed", "SimSpeed", "Simulation ratio", static s => s.SimSpeed, static _ => true, RequiresZero: true, Decimals: 2, Kilo: false, FixedMax: 1.0, DynamicMaxStep5: false),
        new("cpu", "CPU %", "Server process load", static s => s.CpuPercent, static _ => true, RequiresZero: true, Decimals: 1, Kilo: false, FixedMax: null, DynamicMaxStep5: false),
        new("memory", "Memory GB", "Working set", static s => s.MemoryMb / 1024f, static s => s.MemoryMb > 0f, RequiresZero: false, Decimals: 1, Kilo: false, FixedMax: null, DynamicMaxStep5: false),
        new("players", "Player Count", "Players online", static s => s.PlayersOnline, static _ => true, RequiresZero: true, Decimals: 0, Kilo: false, FixedMax: null, DynamicMaxStep5: true),
        new("frametime", "Frame Time ms", "Derived from sim speed", static s => s.FrameTimeMs, static s => s.FrameTimeMs > 0f, RequiresZero: true, Decimals: 1, Kilo: false, FixedMax: 100, DynamicMaxStep5: false),
        new("pcu", "PCU Used", "Used PCU", static s => s.UsedPcu, static _ => true, RequiresZero: true, Decimals: 0, Kilo: true, FixedMax: null, DynamicMaxStep5: false),
        new("grids", "Active Grids", "Entity grids", static s => s.ActiveGridCount, static s => s.ActiveGridCount >= 0, RequiresZero: true, Decimals: 0, Kilo: false, FixedMax: null, DynamicMaxStep5: false),
        new("entities", "Active Entities", "Entity count", static s => s.ActiveEntityCount, static s => s.ActiveEntityCount >= 0, RequiresZero: true, Decimals: 0, Kilo: false, FixedMax: null, DynamicMaxStep5: false),
    ];

    private static readonly Dictionary<string, AnalyticsMetric> Map =
        All.ToDictionary(metric => metric.Key, StringComparer.OrdinalIgnoreCase);

    public static readonly IReadOnlyList<AnalyticsPanelDefinition> Panels =
        All.Select(metric => new AnalyticsPanelDefinition(metric.Key, metric.Title, metric.Subtitle))
            .Concat(ProfilerAnalyticsMetrics.All.Select(metric => new AnalyticsPanelDefinition(metric.Key, metric.Title, metric.Subtitle)))
            .ToList();

    private static readonly Dictionary<string, AnalyticsPanelDefinition> PanelMap =
        Panels.ToDictionary(metric => metric.Key, StringComparer.OrdinalIgnoreCase);

    public static AnalyticsMetric? Find(string? key) =>
        !string.IsNullOrWhiteSpace(key) && Map.TryGetValue(key, out var metric) ? metric : null;

    public static AnalyticsPanelDefinition? FindPanel(string? key) =>
        !string.IsNullOrWhiteSpace(key) && PanelMap.TryGetValue(key, out var panel) ? panel : null;
}

public static class ProfilerAnalyticsMetrics
{
    public static readonly IReadOnlyList<ProfilerAnalyticsMetric> All =
    [
        new("profiler-frame", "Profiler: Frame ms", "Continuous game-loop frame time", static s => s.FrameMs, RequiresZero: true, Decimals: 2, Kilo: false, FixedMax: null),
        new("profiler-update", "Profiler: Update ms", "Continuous update work", static s => s.UpdateMs, RequiresZero: true, Decimals: 2, Kilo: false, FixedMax: null),
        new("profiler-physics", "Profiler: Physics ms", "Continuous physics work", static s => s.PhysicsMs, RequiresZero: true, Decimals: 2, Kilo: false, FixedMax: null),
        new("profiler-scripts", "Profiler: Scripts ms", "Continuous programmable block work", static s => s.ScriptsMs, RequiresZero: true, Decimals: 2, Kilo: false, FixedMax: null),
        new("profiler-network", "Profiler: Network ms", "Continuous network and replication work", static s => s.NetworkMs + s.ReplicationMs, RequiresZero: true, Decimals: 2, Kilo: false, FixedMax: null),
        new("profiler-other", "Profiler: Other ms", "Continuous frame time outside tracked buckets", static s => s.OtherMs, RequiresZero: true, Decimals: 2, Kilo: false, FixedMax: null),
    ];

    private static readonly Dictionary<string, ProfilerAnalyticsMetric> Map =
        All.ToDictionary(metric => metric.Key, StringComparer.OrdinalIgnoreCase);

    public static ProfilerAnalyticsMetric? Find(string? key) =>
        !string.IsNullOrWhiteSpace(key) && Map.TryGetValue(key, out var metric) ? metric : null;
}
