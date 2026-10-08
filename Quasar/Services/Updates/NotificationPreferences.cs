namespace Quasar.Services.Updates;

/// <summary>Account-wide choices shared by the notification panel, bell and browser push.</summary>
public sealed record NotificationPreferences
{
    public bool Updates { get; set; } = true;
    public bool Crashes { get; set; }
    public bool HealthRestarts { get; set; }
    public bool LowSimSpeed { get; set; }
    public float SimSpeedThreshold { get; set; } = 0.8f;
    public bool HighCpu { get; set; }
    public float CpuThreshold { get; set; } = 200;
    public bool HighMemory { get; set; }
    public long MemoryThresholdMb { get; set; } = 8192;
    public bool UnsavedWorld { get; set; }
    public int UnsavedThresholdMinutes { get; set; } = 15;
    public int SustainedSeconds { get; set; } = 30;
    public int CooldownSeconds { get; set; } = 300;
    public bool Recoveries { get; set; }

    public void Validate()
    {
        if (!float.IsFinite(SimSpeedThreshold) || SimSpeedThreshold is <= 0 or > 1
            || !float.IsFinite(CpuThreshold) || CpuThreshold is <= 0 or > 100000
            || MemoryThresholdMb is < 1 or > 1048576
            || UnsavedThresholdMinutes is < 1 or > 10080
            || SustainedSeconds is < 5 or > 3600 || CooldownSeconds is < 30 or > 86400)
            throw new InvalidDataException("Check notification thresholds: sim speed 0–1 (exclusive of 0), CPU 0–100,000% (exclusive of 0), memory 1–1,048,576 MB, unsaved time 1–10,080 minutes, duration 5–3,600 seconds, cooldown 30–86,400 seconds.");
    }
}
