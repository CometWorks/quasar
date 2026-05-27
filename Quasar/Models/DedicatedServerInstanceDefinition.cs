namespace Quasar.Models;

public sealed class DedicatedServerInstanceDefinition
{
    public string InstanceId { get; set; } = Guid.NewGuid().ToString("N");

    public string Name { get; set; } = string.Empty;

    public DedicatedServerInstanceGoalState GoalState { get; set; } = DedicatedServerInstanceGoalState.Off;

    public string ExecutablePath { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public string DedicatedServerAppDataPath { get; set; } = string.Empty;

    public string MagnetarAppDataPath { get; set; } = string.Empty;

    public string WorldPath { get; set; } = string.Empty;

    public string ConfigFilePath { get; set; } = string.Empty;

    public string ConfigProfileId { get; set; } = string.Empty;

    public string WorldProfileId { get; set; } = string.Empty;

    public string LaunchArguments { get; set; } = string.Empty;

    public bool AutoStart { get; set; }

    public bool EnableHealthMonitoring { get; set; } = true;

    public bool AutoRestartOnUnhealthy { get; set; } = true;

    public int AgentStartupGraceSeconds { get; set; } = 180;

    public int AgentHeartbeatTimeoutSeconds { get; set; } = 20;

    public int SimulationProgressWindowSeconds { get; set; } = 30;

    public float MinimumSimulationProgressScore { get; set; } = 0.05f;

    public int WarnAfterUptimeHours { get; set; } = 12;

    public int RecycleAfterUptimeHours { get; set; }

    public bool RestartOnCrash { get; set; } = true;

    public int RestartDelaySeconds { get; set; } = 5;

    public int MaxRestartAttempts { get; set; }

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
}
