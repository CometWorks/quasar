namespace Quasar.Models;

public sealed class DedicatedServerInstanceRuntimeSnapshot
{
    public string InstanceId { get; set; } = string.Empty;

    public string Name { get; set; } = string.Empty;

    public DedicatedServerInstanceGoalState GoalState { get; set; } = DedicatedServerInstanceGoalState.Off;

    public DedicatedServerInstanceProcessState State { get; set; } = DedicatedServerInstanceProcessState.Stopped;

    public DedicatedServerInstanceHealthState HealthState { get; set; } = DedicatedServerInstanceHealthState.Unknown;

    public string HealthSummary { get; set; } = string.Empty;

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
}
