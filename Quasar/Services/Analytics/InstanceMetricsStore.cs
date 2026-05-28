namespace Quasar.Services.Analytics;

public sealed class InstanceMetricsStore
{
    public InstanceMetricsStore()
    {
        Raw = new RrdCircularBuffer(3600);
        OneMinute = new RrdRollupBuffer(10080, 60);
        OneHour = new RrdRollupBuffer(2160, 3600);
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
