namespace Quasar.Models;

public sealed class DedicatedServerRuntimeSnapshot
{
    public string UniqueName { get; set; } = string.Empty;

    public DedicatedServerGoalState GoalState { get; set; } = DedicatedServerGoalState.Off;

    public DedicatedServerProcessState State { get; set; } = DedicatedServerProcessState.Stopped;

    public DedicatedServerHealthState HealthState { get; set; } = DedicatedServerHealthState.Unknown;

    public string HealthSummary { get; set; } = string.Empty;

    public float? SimulationProgressScore { get; set; }

    public int? SimulationProgressWindowSeconds { get; set; }

    public ulong? SimulationFramesAdvanced { get; set; }

    public int RestartAttempts { get; set; }

    public int? ProcessId { get; set; }

    public int? LastExitCode { get; set; }

    public string LastMessage { get; set; } = string.Empty;

    public bool AgentAttached { get; set; }

    public DateTimeOffset? AgentLastSeenUtc { get; set; }

    public DateTimeOffset? StartedAtUtc { get; set; }

    public DateTimeOffset? StoppedAtUtc { get; set; }

    public string StandardOutputLogPath { get; set; } = string.Empty;

    public string StandardErrorLogPath { get; set; } = string.Empty;

    public List<string> ModDownloadFailures { get; set; } = [];
}
