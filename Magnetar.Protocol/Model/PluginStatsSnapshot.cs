using System;
using System.Collections.Generic;

namespace Magnetar.Protocol.Model;

/// <summary>
/// Self-describing runtime statistics a plugin published through Magnetar's
/// <c>PluginSdk.Stats.PluginStats</c> registry, forwarded verbatim by the in-process agent.
/// These are plain protocol POCOs mirroring the SDK shapes so the Quasar service can ingest and
/// chart them without referencing the PluginSdk; only the agent maps the SDK types onto these.
/// </summary>
public class PluginStatsSnapshot
{
    /// <summary>One entry per provider name that currently has a published snapshot.</summary>
    public List<PluginStatsProvider> Providers { get; set; } = new();
}

/// <summary>The latest snapshot published under one provider name (e.g. "Performance").</summary>
public class PluginStatsProvider
{
    public string Provider { get; set; } = string.Empty;

    /// <summary>
    /// The provider's own capture timestamp (<c>StatsSnapshot.UtcTimestamp</c>). Carried so the
    /// store can dedupe: the plugin republishes only on its own cadence (~10s) while the agent
    /// snapshots every second, so the same provider snapshot arrives many times unchanged.
    /// </summary>
    public DateTimeOffset CapturedAtUtc { get; set; }

    public List<PluginStatGroup> Groups { get; set; } = new();
}

/// <summary>One schema plus its captured instances (rows) — e.g. one row per cache.</summary>
public class PluginStatGroup
{
    public string Name { get; set; } = string.Empty;

    public string LabelDescription { get; set; } = string.Empty;

    public List<PluginStatField> Fields { get; set; } = new();

    public List<PluginStatInstance> Instances { get; set; } = new();
}

/// <summary>Metadata for one numeric field, positionally parallel to <see cref="PluginStatInstance.Values"/>.</summary>
public class PluginStatField
{
    public string Name { get; set; } = string.Empty;

    /// <summary>Stat kind: <c>counter</c>, <c>gauge</c> or <c>discrete</c>.</summary>
    public string Kind { get; set; } = string.Empty;

    public string Description { get; set; } = string.Empty;

    public string Unit { get; set; } = string.Empty;

    public string Parent { get; set; } = string.Empty;

    /// <summary>Aggregation hint across instances (enum member name, e.g. "Sum").</summary>
    public string AcrossInstances { get; set; } = string.Empty;

    /// <summary>Aggregation hint over time (enum member name, e.g. "Mean").</summary>
    public string OverTime { get; set; } = string.Empty;
}

/// <summary>One captured row: a label (or null) and the field values in schema order.</summary>
public class PluginStatInstance
{
    public string? Label { get; set; }

    public double[] Values { get; set; } = Array.Empty<double>();
}
