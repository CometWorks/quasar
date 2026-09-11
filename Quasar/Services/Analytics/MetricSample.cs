namespace Quasar.Services.Analytics;

public readonly struct MetricSample
{
    public readonly long TimestampUnixSeconds;
    public readonly float SimSpeed;
    // Process CPU: 100% is one logical CPU, so values may exceed 100%.
    public readonly float CpuPercent;
    // Simulation frame budget used; null for history recorded before collection began.
    public readonly float? SimCpuPercent;
    public readonly float MemoryMb;
    public readonly float FrameTimeMs;
    public readonly int PlayersOnline;
    public readonly int UsedPcu;
    public readonly int ActiveGridCount;
    public readonly int ActiveEntityCount;

    public MetricSample(
        long timestampUnixSeconds,
        float simSpeed,
        float cpuPercent,
        float memoryMb,
        float frameTimeMs,
        int playersOnline,
        int usedPcu,
        int activeGridCount,
        int activeEntityCount,
        float? simCpuPercent = null)
    {
        TimestampUnixSeconds = timestampUnixSeconds;
        SimSpeed = simSpeed;
        CpuPercent = cpuPercent;
        SimCpuPercent = simCpuPercent is >= 0 && float.IsFinite(simCpuPercent.Value) ? simCpuPercent : null;
        MemoryMb = memoryMb;
        FrameTimeMs = frameTimeMs;
        PlayersOnline = playersOnline;
        UsedPcu = usedPcu;
        ActiveGridCount = activeGridCount;
        ActiveEntityCount = activeEntityCount;
    }
}
