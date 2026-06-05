namespace Quasar.Services.Analytics;

public sealed class ServerMetricsStore
{
    public const int DefaultRawCapacity = 3600;
    public const int RollupMinuteCapacityPerDay = 24 * 60;
    public const int RollupHourCapacityPerDay = 24;

    public ServerMetricsStore(AnalyticsStoreOptions? options = null)
    {
        var retentionDays = options?.RetentionDays ?? AnalyticsStoreOptions.DefaultRetentionDays;

        Raw = new RrdCircularBuffer(Math.Max(1, DefaultRawCapacity));
        OneMinute = new RrdRollupBuffer(Math.Max(1, retentionDays * RollupMinuteCapacityPerDay), 60);
        OneHour = new RrdRollupBuffer(Math.Max(1, retentionDays * RollupHourCapacityPerDay), 3600);
    }

    public RrdCircularBuffer Raw { get; }

    public RrdRollupBuffer OneMinute { get; }

    public RrdRollupBuffer OneHour { get; }

    public void Ingest(in MetricSample sample)
    {
        if (!Raw.Push(sample))
            return;

        OneMinute.Observe(sample);
        OneHour.Observe(sample);
    }

    public void Restore(
        IReadOnlyList<MetricSample> raw,
        IReadOnlyList<MetricSample> oneMinute,
        IReadOnlyList<MetricSample> oneHour)
    {
        Raw.ReplaceAll(raw);
        OneMinute.ReplaceAll(oneMinute);
        OneHour.ReplaceAll(oneHour);
    }
}
