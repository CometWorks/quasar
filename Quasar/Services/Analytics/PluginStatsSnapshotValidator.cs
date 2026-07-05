using Magnetar.Protocol.Model;

namespace Quasar.Services.Analytics;

/// <summary>
/// Defensive normalization for an incoming <see cref="PluginStatsProvider"/> before it enters the
/// store — mirrors <see cref="ProfilerSnapshotValidator"/>. Drops empty/untimestamped providers,
/// trims strings, clamps non-finite values, aligns each instance's value array to the field count,
/// and caps group/field/instance counts so a hostile or buggy plugin cannot exhaust memory.
/// </summary>
internal static class PluginStatsSnapshotValidator
{
    private const int MaxGroups = 64;
    private const int MaxFieldsPerGroup = 64;
    private const int MaxInstancesPerGroup = 512;
    private const double MaxMagnitude = 1e15;

    public static bool TryNormalize(PluginStatsProvider? provider, out PluginStatsProvider normalized)
    {
        normalized = new PluginStatsProvider();
        if (provider is null || provider.CapturedAtUtc == default || string.IsNullOrWhiteSpace(provider.Provider))
            return false;

        var groups = new List<PluginStatGroup>();
        foreach (var group in (provider.Groups ?? []).Take(MaxGroups))
        {
            if (group is null || string.IsNullOrWhiteSpace(group.Name))
                continue;

            var fields = (group.Fields ?? [])
                .Where(field => field is not null && !string.IsNullOrWhiteSpace(field.Name))
                .Take(MaxFieldsPerGroup)
                .Select(field => new PluginStatField
                {
                    Name = Clean(field.Name),
                    Kind = Clean(field.Kind),
                    Description = Clean(field.Description),
                    Unit = Clean(field.Unit),
                    Parent = Clean(field.Parent),
                    AcrossInstances = Clean(field.AcrossInstances),
                    OverTime = Clean(field.OverTime),
                })
                .ToList();

            if (fields.Count == 0)
                continue;

            var instances = (group.Instances ?? [])
                .Where(instance => instance is not null)
                .Take(MaxInstancesPerGroup)
                .Select(instance => new PluginStatInstance
                {
                    Label = string.IsNullOrWhiteSpace(instance.Label) ? null : Clean(instance.Label),
                    Values = NormalizeValues(instance.Values, fields.Count),
                })
                .ToList();

            groups.Add(new PluginStatGroup
            {
                Name = Clean(group.Name),
                LabelDescription = Clean(group.LabelDescription),
                Fields = fields,
                Instances = instances,
            });
        }

        if (groups.Count == 0)
            return false;

        normalized = new PluginStatsProvider
        {
            Provider = Clean(provider.Provider),
            CapturedAtUtc = provider.CapturedAtUtc,
            Groups = groups,
        };
        return true;
    }

    // Aligns the value array to the field count so the store can index Values[fieldIndex] safely.
    private static double[] NormalizeValues(double[]? values, int fieldCount)
    {
        var result = new double[fieldCount];
        if (values is null)
            return result;

        var count = Math.Min(values.Length, fieldCount);
        for (var i = 0; i < count; i++)
        {
            var value = values[i];
            result[i] = double.IsNaN(value) || double.IsInfinity(value)
                ? 0d
                : Math.Clamp(value, -MaxMagnitude, MaxMagnitude);
        }

        return result;
    }

    private static string Clean(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = value.Trim();
        return value.Length <= 240 ? value : value[..240];
    }
}
