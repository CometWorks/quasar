using System.Collections.Concurrent;
using Magnetar.Protocol.Model;

namespace Quasar.Services.Analytics;

/// <summary>
/// In-memory store for self-describing plugin statistics forwarded by the agents — the telemetry
/// sibling of <see cref="ProfilerStoreService"/>. Keyed by (server, provider); each key holds a
/// short rolling history of that provider's snapshots, deduped by the provider's own capture
/// timestamp (the agent resends the same snapshot every second until the plugin republishes).
///
/// Nothing here knows what any provider's numbers mean: the schema (groups → fields → per-instance
/// values) travels inside every sample, so <see cref="GetCatalog"/> discovers chartable metrics at
/// runtime and <see cref="AnalyticsSeriesService"/> renders them without any plugin-specific code.
/// Like the profiler store this is not persisted, so history is lost on a Quasar restart.
/// </summary>
public sealed class PluginStatsStoreService
{
    public const string KeyPrefix = "plugin:";
    private const int MaxSamplesPerKey = 12 * 60;
    private const char ServerProviderSeparator = '\u001f';

    private readonly ConcurrentDictionary<string, ConcurrentQueue<PluginStatSample>> _samples =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _lastCapturedAt =
        new(StringComparer.OrdinalIgnoreCase);

    public void Enqueue(string uniqueName, PluginStatsSnapshot? snapshot)
    {
        if (string.IsNullOrWhiteSpace(uniqueName) || snapshot?.Providers is null)
            return;

        foreach (var provider in snapshot.Providers)
        {
            if (!PluginStatsSnapshotValidator.TryNormalize(provider, out var normalized))
                continue;

            var key = ComposeKey(uniqueName, normalized.Provider);
            if (!TryAdvance(key, normalized.CapturedAtUtc))
                continue;

            var queue = _samples.GetOrAdd(key, _ => new ConcurrentQueue<PluginStatSample>());
            queue.Enqueue(new PluginStatSample(normalized.CapturedAtUtc, normalized.Groups));
            while (queue.Count > MaxSamplesPerKey)
                queue.TryDequeue(out _);
        }
    }

    /// <summary>Per-server timelines for one provider within the range, for charting.</summary>
    public IReadOnlyList<PluginStatTimeline> Read(string provider, long fromUnix, long toUnix, IReadOnlyList<string> servers)
    {
        if (string.IsNullOrWhiteSpace(provider) || toUnix <= fromUnix || servers.Count == 0)
            return [];

        var from = DateTimeOffset.FromUnixTimeSeconds(fromUnix);
        var to = DateTimeOffset.FromUnixTimeSeconds(toUnix);
        var result = new List<PluginStatTimeline>();

        foreach (var server in servers.Where(value => !string.IsNullOrWhiteSpace(value)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_samples.TryGetValue(ComposeKey(server, provider), out var queue))
                continue;

            var samples = queue
                .Where(sample => sample.CapturedAtUtc >= from && sample.CapturedAtUtc <= to)
                .OrderBy(sample => sample.CapturedAtUtc)
                .ToList();
            if (samples.Count == 0)
                continue;

            result.Add(new PluginStatTimeline(server, samples));
        }

        return result;
    }

    /// <summary>
    /// The chartable metrics discovered so far, one per (provider, group, field) seen in the latest
    /// snapshot of the requested servers (all stored servers when <paramref name="servers"/> is empty).
    /// </summary>
    public IReadOnlyList<PluginStatPanel> GetCatalog(IReadOnlyList<string> servers)
    {
        var serverFilter = servers.Count == 0
            ? null
            : new HashSet<string>(servers.Where(value => !string.IsNullOrWhiteSpace(value)), StringComparer.OrdinalIgnoreCase);

        var panels = new Dictionary<string, PluginStatPanel>(StringComparer.OrdinalIgnoreCase);

        foreach (var (compositeKey, queue) in _samples)
        {
            if (!TrySplitKey(compositeKey, out var server, out var provider))
                continue;
            if (serverFilter is not null && !serverFilter.Contains(server))
                continue;

            var latest = queue.OrderByDescending(sample => sample.CapturedAtUtc).FirstOrDefault();
            if (latest is null)
                continue;

            foreach (var group in latest.Groups)
            {
                foreach (var field in group.Fields)
                {
                    var key = BuildMetricKey(provider, group.Name, field.Name);
                    if (panels.ContainsKey(key))
                        continue;

                    panels[key] = new PluginStatPanel(
                        key,
                        $"{provider}: {FirstNonBlank(field.Description, field.Name)}",
                        BuildSubtitle(group.Name, field.Unit),
                        Decimals: string.Equals(field.Kind, "counter", StringComparison.OrdinalIgnoreCase) ? 0 : 2,
                        RequiresZero: true);
                }
            }
        }

        return panels.Values.OrderBy(panel => panel.Key, StringComparer.OrdinalIgnoreCase).ToList();
    }

    public static string BuildMetricKey(string provider, string group, string field) =>
        $"{KeyPrefix}{provider}:{group}:{field}";

    /// <summary>Parses a <c>plugin:{provider}:{group}:{field}</c> metric key.</summary>
    public static bool TryParseMetricKey(string? key, out string provider, out string group, out string field)
    {
        provider = group = field = string.Empty;
        if (string.IsNullOrWhiteSpace(key) || !key.StartsWith(KeyPrefix, StringComparison.Ordinal))
            return false;

        var parts = key[KeyPrefix.Length..].Split(':');
        if (parts.Length < 3)
            return false;

        provider = parts[0];
        group = parts[1];
        field = string.Join(":", parts[2..]);
        return provider.Length > 0 && group.Length > 0 && field.Length > 0;
    }

    private bool TryAdvance(string key, DateTimeOffset capturedAt)
    {
        while (true)
        {
            if (_lastCapturedAt.TryGetValue(key, out var last))
            {
                if (capturedAt <= last)
                    return false;
                if (_lastCapturedAt.TryUpdate(key, capturedAt, last))
                    return true;
            }
            else if (_lastCapturedAt.TryAdd(key, capturedAt))
            {
                return true;
            }
        }
    }

    private static string ComposeKey(string server, string provider) =>
        $"{server}{ServerProviderSeparator}{provider}";

    private static bool TrySplitKey(string composite, out string server, out string provider)
    {
        var index = composite.IndexOf(ServerProviderSeparator);
        if (index <= 0 || index >= composite.Length - 1)
        {
            server = provider = string.Empty;
            return false;
        }

        server = composite[..index];
        provider = composite[(index + 1)..];
        return true;
    }

    private static string BuildSubtitle(string group, string unit) =>
        string.IsNullOrWhiteSpace(unit) ? group : $"{group} · {unit}";

    private static string FirstNonBlank(string a, string b) =>
        !string.IsNullOrWhiteSpace(a) ? a : (!string.IsNullOrWhiteSpace(b) ? b : "Value");
}

/// <summary>One provider snapshot at a point in time: its capture timestamp and the captured groups.</summary>
public sealed record PluginStatSample(DateTimeOffset CapturedAtUtc, IReadOnlyList<PluginStatGroup> Groups);

/// <summary>One server's ordered samples for a provider within a queried range.</summary>
public sealed record PluginStatTimeline(string Server, IReadOnlyList<PluginStatSample> Samples);

/// <summary>A discovered chartable metric: its key plus display hints for the Analytics page.</summary>
public sealed record PluginStatPanel(string Key, string Title, string Subtitle, int Decimals, bool RequiresZero);
