using System.Text.Json;
using Magnetar.Protocol.Model;
using Quasar.Services.Analytics;
using Xunit;

namespace Quasar.Tests;

public sealed class SimulationSpeedTests
{
    [Theory]
    [InlineData(0f, 0f)]
    [InlineData(0.75f, 0.75f)]
    [InlineData(1f, 1f)]
    [InlineData(1.25f, 1f)]
    [InlineData(float.MaxValue, 1f)]
    public void CapturedAndIncomingSnapshotsCapSimulationSpeedBeforeAnalytics(float speed, float expected)
    {
        var metrics = new ServerMetrics { SimSpeed = speed, ServerCpuLoadPercent = 155, SimCpuLoadPercent = 150 };
        Assert.Equal(expected, metrics.SimSpeed);
        Assert.Equal(expected, JsonSerializer.Deserialize<ServerMetrics>(JsonSerializer.Serialize(metrics))!.SimSpeed);

        // Older agents can still send an uncapped ratio. Deserialization must cap it too.
        var incoming = JsonSerializer.Deserialize<AgentSnapshot>(JsonSerializer.Serialize(new
        {
            CapturedAtUtc = DateTimeOffset.FromUnixTimeSeconds(60),
            Metrics = new { SimSpeed = speed, ServerCpuLoadPercent = 155, SimCpuLoadPercent = 150 },
        }))!;
        Assert.Equal(expected, incoming.Metrics.SimSpeed);
        var sample = MetricSampleFactory.FromSnapshot(incoming);
        Assert.Equal(expected, sample.SimSpeed);
        Assert.Equal(expected > 0.001f ? 1000f / (expected * 60f) : 0f, sample.FrameTimeMs);
        Assert.Equal(155f, sample.CpuPercent);
        Assert.Equal(150f, sample.SimCpuPercent);
    }

    [Theory]
    [InlineData("r")]
    [InlineData("m")]
    [InlineData("h")]
    public void ExistingHistoryCapsSimulationSpeedWhenRestoredAndPersisted(string bucket)
    {
        var json = JsonSerializer.Serialize(new { b = bucket, T = 60, Ss = 1.25f, Cpu = 155, Scpu = 150 });
        var restored = JsonSerializer.Deserialize<MetricsStoreService.PersistedMetricLogLine>(json)!.ToMetricSample();
        Assert.Equal(1f, restored.SimSpeed);
        Assert.Equal(155f, restored.CpuPercent);
        Assert.Equal(150f, restored.SimCpuPercent);
        var persisted = MetricsStoreService.PersistedMetricLogLine.From(bucket, restored);
        Assert.Equal(1f, persisted.SimSpeed);
    }

    [Theory]
    [InlineData(60)]
    [InlineData(3600)]
    public void RawSamplesAreCappedBeforeRollupAverages(int interval)
    {
        var store = new ServerMetricsStore();
        store.Ingest(new MetricSample(0, 1.25f, 155, 1024, 16.67f, 0, 0, 0, 0, 150));
        store.Ingest(new MetricSample(1, 0.5f, 155, 1024, 33.33f, 0, 0, 0, 0, 150));
        store.Ingest(new MetricSample(interval, 1, 155, 1024, 16.67f, 0, 0, 0, 0, 150));
        Assert.Equal(1f, store.Raw.ReadAll()[0].SimSpeed);
        var rollup = interval == 60 ? store.OneMinute : store.OneHour;
        var sample = Assert.Single(rollup.ReadAll());
        Assert.Equal(0.75f, sample.SimSpeed);
        Assert.Equal(155f, sample.CpuPercent);
        Assert.Equal(150f, sample.SimCpuPercent);
    }
}
