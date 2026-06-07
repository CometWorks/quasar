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

    private readonly MetricsStoreService _store;

    public AnalyticsSeriesService(MetricsStoreService store) => _store = store;

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
        if (metrics.Count == 0)
            return new AnalyticsSeriesResponse(fromUnix, toUnix, []);

        var spanSeconds = toUnix - fromUnix;
        var bucketWidth = Math.Max(1L, (long)Math.Ceiling(spanSeconds / (double)maxPoints));
        var bucketCount = (int)Math.Min(maxPoints, spanSeconds / bucketWidth + 1);
        if (bucketCount < 1)
            bucketCount = 1;

        // Read and bucket each server's samples once, accumulating per-metric sums per bucket.
        var serverBuckets = new List<ServerBuckets>(servers.Count);
        var bucketHasData = new bool[bucketCount];

        foreach (var uniqueName in servers)
        {
            var store = _store.GetStore(uniqueName);
            if (store is null)
                continue;

            var samples = ReadSamplesForRange(store, fromUnix, toUnix, spanSeconds);
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
                var bucket = (int)((sample.TimestampUnixSeconds - fromUnix) / bucketWidth);
                if (bucket < 0)
                    bucket = 0;
                else if (bucket >= bucketCount)
                    bucket = bucketCount - 1;

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
            return new AnalyticsSeriesResponse(fromUnix, toUnix, []);

        var x = new long[kept.Count];
        for (var i = 0; i < kept.Count; i++)
            x[i] = fromUnix + kept[i] * bucketWidth + bucketWidth / 2;

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

        return new AnalyticsSeriesResponse(fromUnix, toUnix, charts);
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
