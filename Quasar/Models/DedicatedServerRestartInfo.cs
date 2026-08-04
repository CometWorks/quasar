namespace Quasar.Models;

public enum DedicatedServerRestartCause
{
    Unknown = 0,
    HealthPolicy = 1,
    AgentAttachRecovery = 2,
    Scheduled = 3,
    MaximumUptime = 4,
    Manual = 5,
    InGame = 6,
    CrashRecovery = 7,
}

public enum DedicatedServerRestartOutcome
{
    Pending = 0,
    Recovered = 1,
    Failed = 2,
}

public sealed class DedicatedServerRestartInfo
{
    public DedicatedServerRestartCause Cause { get; set; }

    public string Reason { get; set; } = string.Empty;

    public DateTimeOffset RequestedAtUtc { get; set; }

    public DateTimeOffset? CompletedAtUtc { get; set; }

    public DedicatedServerRestartOutcome Outcome { get; set; }
}
