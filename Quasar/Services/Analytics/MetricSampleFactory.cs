using Magnetar.Protocol.Model;

namespace Quasar.Services.Analytics;

internal static class MetricSampleFactory
{
    public static MetricSample FromSnapshot(AgentSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        ArgumentNullException.ThrowIfNull(snapshot.Metrics);

        var metrics = snapshot.Metrics;
        var frameTimeMs = metrics.SimSpeed > 0.001f
            ? 1000f / (metrics.SimSpeed * 60f)
            : 0f;

        return new MetricSample(
            timestampUnixSeconds: snapshot.CapturedAtUtc.ToUnixTimeSeconds(),
            simSpeed: metrics.SimSpeed,
            cpuPercent: metrics.ServerCpuLoadPercent,
            memoryMb: metrics.MemoryWorkingSetMb ?? 0,
            frameTimeMs: frameTimeMs,
            playersOnline: metrics.PlayersOnline,
            usedPcu: metrics.UsedPcu,
            activeGridCount: metrics.ActiveGridCount ?? -1,
            activeEntityCount: metrics.ActiveEntityCount ?? -1);
    }
}
