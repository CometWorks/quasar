using Magnetar.Protocol.Model;

namespace Quasar.Services.Analytics;

/// <summary>
/// Builds chart series for the Analytics page directly as JSON over plain HTTP, instead of
/// serializing them through the Blazor Server SignalR circuit. The browser fetches this and renders
/// with uPlot client-side, which keeps the heavy data off the circuit and off the server render tree.
///
/// Each series is averaged down to at most <c>maxPoints</c> samples (default 1000). Samples are
/// bucketed onto a single shared timeline so every series in a chart is aligned for uPlot's
/// columnar data format (<c>[x[], y0[], y1[], ...]</c>); buckets with no samples are dropped.
/// </summary>
public sealed class AnalyticsSeriesService
{
    public const int MaxPointsCeiling = 1000;
    private const int MaxPointsFloor = 10;
    private const int MaxProfilerEntrySeriesPerChart = 20;

    private readonly MetricsStoreService _store;
    private readonly ProfilerStoreService _profilerStore;

    public AnalyticsSeriesService(MetricsStoreService store, ProfilerStoreService profilerStore)
    {
        _store = store;
        _profilerStore = profilerStore;
    }

    public AnalyticsSeriesResponse Build(
        long fromUnix,
        long toUnix,
        IReadOnlyList<string> servers,
        IReadOnlyList<string> metricKeys,
        int maxPoints)
    {
        if (toUnix <= fromUnix || servers.Count == 0 || metricKeys.Count == 0)
            return new AnalyticsSeriesResponse(fromUnix, toUnix, []);

        maxPoints = Math.Clamp(maxPoints <= 0 ? MaxPointsCeiling : maxPoints, MaxPointsFloor, MaxPointsCeiling);

        var metrics = metricKeys
            .Select(AnalyticsMetrics.Find)
            .Where(metric => metric is not null)
            .Select(metric => metric!)
            .ToList();

        var profilerMetrics = metricKeys
            .Select(ProfilerAnalyticsMetrics.Find)
            .Where(metric => metric is not null)
            .Select(metric => metric!)
            .ToList();

        var profilerEntryMetrics = metricKeys
            .Select(ProfilerEntryAnalyticsMetrics.Find)
            .Where(metric => metric is not null)
            .Select(metric => metric!)
            .ToList();

        if (metrics.Count == 0 && profilerMetrics.Count == 0 && profilerEntryMetrics.Count == 0)
            return new AnalyticsSeriesResponse(fromUnix, toUnix, []);

        var charts = new List<AnalyticsChartDto>(metrics.Count + profilerMetrics.Count + profilerEntryMetrics.Count);
        charts.AddRange(BuildMetricCharts(fromUnix, toUnix, servers, metrics, maxPoints));
        charts.AddRange(BuildProfilerCharts(fromUnix, toUnix, servers, profilerMetrics, maxPoints));
        charts.AddRange(BuildProfilerEntryCharts(fromUnix, toUnix, servers, profilerEntryMetrics, maxPoints));
        return new AnalyticsSeriesResponse(fromUnix, toUnix, charts);
    }

    private IReadOnlyList<AnalyticsChartDto> BuildMetricCharts(
        long fromUnix,
        long toUnix,
        IReadOnlyList<string> servers,
        IReadOnlyList<AnalyticsMetric> metrics,
        int maxPoints)
    {
        if (metrics.Count == 0)
            return [];

        var spanSeconds = toUnix - fromUnix;
        var (bucketWidth, alignedFrom, bucketCount) = ResolveBuckets(fromUnix, toUnix, maxPoints);

        // Read and bucket each server's samples once, accumulating per-metric sums per bucket.
        var serverBuckets = new List<ServerBuckets>(servers.Count);
        var bucketHasData = new bool[bucketCount];

        foreach (var uniqueName in servers)
        {
            var store = _store.GetStore(uniqueName);
            if (store is null)
                continue;

            // Read from alignedFrom (not fromUnix) so the leading bucket is also a complete window and
            // stays stable as fromUnix slides within it.
            var samples = ReadSamplesForRange(store, alignedFrom, toUnix, spanSeconds);
            if (samples.Length == 0)
                continue;

            var sums = new double[metrics.Count][];
            var counts = new int[metrics.Count][];
            for (var mi = 0; mi < metrics.Count; mi++)
            {
                sums[mi] = new double[bucketCount];
                counts[mi] = new int[bucketCount];
            }

            foreach (var sample in samples)
            {
                var bucket = (int)((sample.TimestampUnixSeconds - alignedFrom) / bucketWidth);
                if (bucket < 0 || bucket >= bucketCount)
                    continue;

                bucketHasData[bucket] = true;

                for (var mi = 0; mi < metrics.Count; mi++)
                {
                    var metric = metrics[mi];
                    if (!metric.IsAvailable(sample))
                        continue;

                    var value = metric.Selector(sample);
                    if (!double.IsFinite(value))
                        continue;

                    sums[mi][bucket] += value;
                    counts[mi][bucket]++;
                }
            }

            serverBuckets.Add(new ServerBuckets(uniqueName, sums, counts));
        }

        // Compact to the buckets that actually hold data; this X axis is shared by every chart.
        var kept = new List<int>(bucketCount);
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            if (bucketHasData[bucket])
                kept.Add(bucket);
        }

        if (kept.Count == 0)
            return [];

        var x = new long[kept.Count];
        for (var i = 0; i < kept.Count; i++)
            x[i] = alignedFrom + kept[i] * bucketWidth + bucketWidth / 2;

        var charts = new List<AnalyticsChartDto>(metrics.Count);
        for (var mi = 0; mi < metrics.Count; mi++)
        {
            var metric = metrics[mi];
            var seriesList = new List<AnalyticsSeriesDto>(serverBuckets.Count);
            var dynamicMax = 0.0;

            foreach (var server in serverBuckets)
            {
                var sums = server.Sums[mi];
                var counts = server.Counts[mi];
                var y = new double?[kept.Count];
                var any = false;

                for (var i = 0; i < kept.Count; i++)
                {
                    var bucket = kept[i];
                    if (counts[bucket] <= 0)
                        continue;

                    var rounded = Math.Round(sums[bucket] / counts[bucket], metric.Decimals + 2, MidpointRounding.AwayFromZero);
                    y[i] = rounded;
                    any = true;
                    if (rounded > dynamicMax)
                        dynamicMax = rounded;
                }

                if (any)
                    seriesList.Add(new AnalyticsSeriesDto(server.UniqueName, y));
            }

            if (seriesList.Count == 0)
                continue;

            var axis = new AnalyticsAxisDto(
                Min: metric.RequiresZero ? 0 : null,
                Max: ResolveMax(metric, dynamicMax),
                Decimals: metric.Decimals,
                Kilo: metric.Kilo,
                TickAmount: 5);

            charts.Add(new AnalyticsChartDto(metric.Key, metric.Title, metric.Subtitle, axis, x, seriesList));
        }

        return charts;
    }

    private IReadOnlyList<AnalyticsChartDto> BuildProfilerCharts(
        long fromUnix,
        long toUnix,
        IReadOnlyList<string> servers,
        IReadOnlyList<ProfilerAnalyticsMetric> metrics,
        int maxPoints)
    {
        if (metrics.Count == 0)
            return [];

        var serverKeys = servers
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (serverKeys.Count == 0)
            return [];

        var (bucketWidth, alignedFrom, bucketCount) = ResolveBuckets(fromUnix, toUnix, maxPoints);
        var response = _profilerStore.Build(alignedFrom, toUnix, serverKeys);
        var samplesByServer = response.Servers.ToDictionary(server => server.UniqueName, StringComparer.OrdinalIgnoreCase);

        var serverBuckets = new List<ServerBuckets>(serverKeys.Count);
        var bucketHasData = new bool[bucketCount];

        foreach (var uniqueName in serverKeys)
        {
            var sums = new double[metrics.Count][];
            var counts = new int[metrics.Count][];
            for (var mi = 0; mi < metrics.Count; mi++)
            {
                sums[mi] = new double[bucketCount];
                counts[mi] = new int[bucketCount];
            }

            if (!samplesByServer.TryGetValue(uniqueName, out var server))
            {
                serverBuckets.Add(new ServerBuckets(uniqueName, sums, counts));
                continue;
            }

            foreach (var sample in server.Samples)
            {
                var timestamp = sample.CapturedAtUtc.ToUnixTimeSeconds();
                var bucket = (int)((timestamp - alignedFrom) / bucketWidth);
                if (bucket < 0 || bucket >= bucketCount)
                    continue;

                var hasValue = false;
                for (var mi = 0; mi < metrics.Count; mi++)
                {
                    var metric = metrics[mi];
                    var value = metric.Selector(sample.GameLoop);
                    if (!double.IsFinite(value))
                        continue;

                    sums[mi][bucket] += value;
                    counts[mi][bucket]++;
                    hasValue = true;
                }

                if (hasValue)
                    bucketHasData[bucket] = true;
            }

            serverBuckets.Add(new ServerBuckets(uniqueName, sums, counts));
        }

        var kept = new List<int>(bucketCount);
        for (var bucket = 0; bucket < bucketCount; bucket++)
        {
            if (bucketHasData[bucket])
                kept.Add(bucket);
        }

        var x = new long[kept.Count];
        for (var i = 0; i < kept.Count; i++)
            x[i] = alignedFrom + kept[i] * bucketWidth + bucketWidth / 2;

        var charts = new List<AnalyticsChartDto>(metrics.Count);
        for (var mi = 0; mi < metrics.Count; mi++)
        {
            var metric = metrics[mi];
            var seriesList = new List<AnalyticsSeriesDto>(serverBuckets.Count);

            foreach (var server in serverBuckets)
            {
                var sums = server.Sums[mi];
                var counts = server.Counts[mi];
                var y = new double?[kept.Count];

                for (var i = 0; i < kept.Count; i++)
                {
                    var bucket = kept[i];
                    if (counts[bucket] <= 0)
                        continue;

                    y[i] = Math.Round(sums[bucket] / counts[bucket], metric.Decimals + 2, MidpointRounding.AwayFromZero);
                }

                seriesList.Add(new AnalyticsSeriesDto(server.UniqueName, y));
            }

            var axis = new AnalyticsAxisDto(
                Min: metric.RequiresZero ? 0 : null,
                Max: metric.FixedMax,
                Decimals: metric.Decimals,
                Kilo: metric.Kilo,
                TickAmount: 5);

            charts.Add(new AnalyticsChartDto(metric.Key, metric.Title, metric.Subtitle, axis, x, seriesList));
        }

        return charts;
    }

    private IReadOnlyList<AnalyticsChartDto> BuildProfilerEntryCharts(
        long fromUnix,
        long toUnix,
        IReadOnlyList<string> servers,
        IReadOnlyList<ProfilerEntryAnalyticsMetric> metrics,
        int maxPoints)
    {
        if (metrics.Count == 0)
            return [];

        var serverKeys = servers
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        if (serverKeys.Count == 0)
            return [];

        var (bucketWidth, alignedFrom, bucketCount) = ResolveBuckets(fromUnix, toUnix, maxPoints);
        var response = _profilerStore.Build(alignedFrom, toUnix, serverKeys);

        var bucketsByMetric = new List<Dictionary<string, EntryBuckets>>(metrics.Count);
        for (var mi = 0; mi < metrics.Count; mi++)
            bucketsByMetric.Add(new Dictionary<string, EntryBuckets>(StringComparer.OrdinalIgnoreCase));

        var bucketHasDataByMetric = new bool[metrics.Count][];
        for (var mi = 0; mi < metrics.Count; mi++)
            bucketHasDataByMetric[mi] = new bool[bucketCount];

        foreach (var server in response.Servers)
        {
            foreach (var sample in server.Samples)
            {
                var timestamp = sample.CapturedAtUtc.ToUnixTimeSeconds();
                var bucket = (int)((timestamp - alignedFrom) / bucketWidth);
                if (bucket < 0 || bucket >= bucketCount)
                    continue;

                for (var mi = 0; mi < metrics.Count; mi++)
                {
                    var metric = metrics[mi];
                    var entries = metric.Selector(sample);
                    if (entries.Count == 0)
                        continue;

                    foreach (var entry in entries)
                    {
                        var value = ResolveEntryValue(entry);
                        if (!double.IsFinite(value) || value <= 0d)
                            continue;

                        var label = BuildEntrySeriesLabel(server.UniqueName, entry);
                        var series = GetOrCreateEntryBuckets(bucketsByMetric[mi], label, bucketCount);
                        series.Sums[bucket] += value;
                        series.Counts[bucket]++;
                        series.Total += value;
                        bucketHasDataByMetric[mi][bucket] = true;
                    }
                }
            }
        }

        var charts = new List<AnalyticsChartDto>(metrics.Count);
        for (var mi = 0; mi < metrics.Count; mi++)
        {
            var metric = metrics[mi];
            var kept = new List<int>(bucketCount);
            for (var bucket = 0; bucket < bucketCount; bucket++)
            {
                if (bucketHasDataByMetric[mi][bucket])
                    kept.Add(bucket);
            }

            var x = new long[kept.Count];
            for (var i = 0; i < kept.Count; i++)
                x[i] = alignedFrom + kept[i] * bucketWidth + bucketWidth / 2;

            var seriesList = new List<AnalyticsSeriesDto>();
            foreach (var series in bucketsByMetric[mi].Values
                         .OrderByDescending(series => series.Total)
                         .ThenBy(series => series.Label, StringComparer.OrdinalIgnoreCase)
                         .Take(MaxProfilerEntrySeriesPerChart))
            {
                var y = new double?[kept.Count];
                for (var i = 0; i < kept.Count; i++)
                {
                    var bucket = kept[i];
                    if (series.Counts[bucket] <= 0)
                        continue;

                    y[i] = Math.Round(series.Sums[bucket] / series.Counts[bucket], metric.Decimals + 2, MidpointRounding.AwayFromZero);
                }

                seriesList.Add(new AnalyticsSeriesDto(series.Label, y));
            }

            var axis = new AnalyticsAxisDto(
                Min: metric.RequiresZero ? 0 : null,
                Max: metric.FixedMax,
                Decimals: metric.Decimals,
                Kilo: metric.Kilo,
                TickAmount: 5);

            charts.Add(new AnalyticsChartDto(metric.Key, metric.Title, metric.Subtitle, axis, x, seriesList));
        }

        return charts;
    }

    private static double? ResolveMax(AnalyticsMetric metric, double dynamicMax)
    {
        if (metric.DynamicMaxStep5)
        {
            var rounded = Math.Ceiling(Math.Max(dynamicMax, 0) / 5.0) * 5.0;
            return rounded <= 0 ? 5 : rounded;
        }

        return metric.FixedMax;
    }

    private static (long BucketWidth, long AlignedFrom, int BucketCount) ResolveBuckets(long fromUnix, long toUnix, int maxPoints)
    {
        var spanSeconds = Math.Max(1, toUnix - fromUnix);
        var bucketWidth = Math.Max(1L, (long)Math.Ceiling(spanSeconds / (double)maxPoints));

        // Align bucket boundaries to an absolute grid (integer multiples of bucketWidth) rather than to
        // fromUnix. As the [from, to] window slides forward in real time a fixed sample then always lands
        // in the same bucket, so already-plotted points keep their averaged value instead of re-bucketing
        // and visibly shifting on every refresh.
        var alignedFrom = fromUnix / bucketWidth * bucketWidth;

        // Count only buckets whose full width fits within toUnix. The trailing partial bucket (still
        // filling with fresh samples) is dropped, so the most recent point doesn't keep changing — and
        // doesn't jump when it finally settles into the past.
        var bucketCount = Math.Clamp((int)((toUnix - alignedFrom) / bucketWidth), 1, maxPoints);

        return (bucketWidth, alignedFrom, bucketCount);
    }

    private static EntryBuckets GetOrCreateEntryBuckets(Dictionary<string, EntryBuckets> buckets, string label, int bucketCount)
    {
        if (buckets.TryGetValue(label, out var existing))
            return existing;

        var created = new EntryBuckets(label, new double[bucketCount], new int[bucketCount]);
        buckets[label] = created;
        return created;
    }

    private static double ResolveEntryValue(ProfilerEntrySnapshot entry)
    {
        if (entry.TotalMsPerFrame > 0d)
            return entry.TotalMsPerFrame;

        return entry.TotalMs;
    }

    private static string BuildEntrySeriesLabel(string uniqueName, ProfilerEntrySnapshot entry)
    {
        var name = FirstNonBlank(entry.Name, entry.GridName, entry.BlockName, entry.TypeName, entry.MethodName, entry.Key, entry.Category, "Unknown");
        return $"{uniqueName} / {name}";
    }

    private static string FirstNonBlank(params string[] values)
    {
        foreach (var value in values)
        {
            if (!string.IsNullOrWhiteSpace(value))
                return value.Trim();
        }

        return "Unknown";
    }

    // Mirrors the store-tier selection the Analytics page uses: raw 2s samples for short windows,
    // 1-minute rollups up to a day, 1-hour rollups beyond.
    private static MetricSample[] ReadSamplesForRange(ServerMetricsStore store, long fromUnix, long toUnix, long spanSeconds)
    {
        if (spanSeconds <= 2 * 3600)
            return store.Raw.Read(fromUnix, toUnix);

        if (spanSeconds <= 24 * 3600)
            return store.OneMinute.Read(fromUnix, toUnix);

        return store.OneHour.Read(fromUnix, toUnix);
    }

    private readonly record struct ServerBuckets(string UniqueName, double[][] Sums, int[][] Counts);

    private sealed record EntryBuckets(string Label, double[] Sums, int[] Counts)
    {
        public double Total { get; set; }
    }
}

public sealed record AnalyticsSeriesResponse(long From, long To, IReadOnlyList<AnalyticsChartDto> Charts);

public sealed record AnalyticsChartDto(
    string Metric,
    string Title,
    string Subtitle,
    AnalyticsAxisDto Axis,
    IReadOnlyList<long> X,
    IReadOnlyList<AnalyticsSeriesDto> Series);

public sealed record AnalyticsAxisDto(double? Min, double? Max, int Decimals, bool Kilo, int TickAmount);

public sealed record AnalyticsSeriesDto(string UniqueName, IReadOnlyList<double?> Y);
