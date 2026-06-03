using System.Text.Json.Serialization;

namespace Quasar.Models;

public sealed class DedicatedServerInstanceDefinition
{
    public string UniqueName { get; set; } = string.Empty;

    public string DisplayName { get; set; } = string.Empty;

    [JsonIgnore]
    public string OriginalUniqueName { get; set; } = string.Empty;

    public DedicatedServerInstanceGoalState GoalState { get; set; } = DedicatedServerInstanceGoalState.Off;

    public string ExecutablePath { get; set; } = string.Empty;

    public string WorkingDirectory { get; set; } = string.Empty;

    public string DedicatedServerAppDataPath { get; set; } = string.Empty;

    public string MagnetarAppDataPath { get; set; } = string.Empty;

    public string WorldPath { get; set; } = string.Empty;

    public string ConfigFilePath { get; set; } = string.Empty;

    public string ConfigProfileId { get; set; } = string.Empty;

    public string WorldTemplateId { get; set; } = string.Empty;

    public string LaunchArguments { get; set; } = string.Empty;

    public int ServerPort { get; set; } = 27016;

    public string ServerIP { get; set; } = "0.0.0.0";

    public bool AutoStart { get; set; }

    public bool EnableHealthMonitoring { get; set; } = true;

    public bool AutoRestartOnUnhealthy { get; set; } = true;

    public int AgentStartupGraceSeconds { get; set; } = 180;

    public int AgentHeartbeatTimeoutSeconds { get; set; } = 20;

    public int SimulationProgressWindowSeconds { get; set; } = 30;

    public float MinimumSimulationProgressScore { get; set; } = 0.1f;

    public int WarnAfterUptimeHours { get; set; } = 12;

    public int RecycleAfterUptimeHours { get; set; }

    public bool RestartOnCrash { get; set; } = true;

    public int RestartDelaySeconds { get; set; } = 5;

    public int MaxRestartAttempts { get; set; }

    public string DailyRestartTimeLocal { get; set; } = string.Empty;

    public string MaximumUptime { get; set; } = string.Empty;

    public bool AvoidSimultaneousScheduledRestarts { get; set; } = true;

    public DedicatedServerProcessPriority StartupProcessPriority { get; set; } = DedicatedServerProcessPriority.BelowNormal;

    public DedicatedServerProcessPriority ReadyProcessPriority { get; set; } = DedicatedServerProcessPriority.Normal;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public DedicatedServerInstanceDefinition Clone()
    {
        return new DedicatedServerInstanceDefinition
        {
            UniqueName = UniqueName,
            DisplayName = DisplayName,
            OriginalUniqueName = OriginalUniqueName,
            GoalState = GoalState,
            ExecutablePath = ExecutablePath,
            WorkingDirectory = WorkingDirectory,
            DedicatedServerAppDataPath = DedicatedServerAppDataPath,
            MagnetarAppDataPath = MagnetarAppDataPath,
            WorldPath = WorldPath,
            ConfigFilePath = ConfigFilePath,
            ConfigProfileId = ConfigProfileId,
            WorldTemplateId = WorldTemplateId,
            LaunchArguments = LaunchArguments,
            ServerPort = ServerPort,
            ServerIP = ServerIP,
            AutoStart = AutoStart,
            EnableHealthMonitoring = EnableHealthMonitoring,
            AutoRestartOnUnhealthy = AutoRestartOnUnhealthy,
            AgentStartupGraceSeconds = AgentStartupGraceSeconds,
            AgentHeartbeatTimeoutSeconds = AgentHeartbeatTimeoutSeconds,
            SimulationProgressWindowSeconds = SimulationProgressWindowSeconds,
            MinimumSimulationProgressScore = MinimumSimulationProgressScore,
            WarnAfterUptimeHours = WarnAfterUptimeHours,
            RecycleAfterUptimeHours = RecycleAfterUptimeHours,
            RestartOnCrash = RestartOnCrash,
            RestartDelaySeconds = RestartDelaySeconds,
            MaxRestartAttempts = MaxRestartAttempts,
            DailyRestartTimeLocal = DailyRestartTimeLocal,
            MaximumUptime = MaximumUptime,
            AvoidSimultaneousScheduledRestarts = AvoidSimultaneousScheduledRestarts,
            StartupProcessPriority = StartupProcessPriority,
            ReadyProcessPriority = ReadyProcessPriority,
            UpdatedAtUtc = UpdatedAtUtc,
        };
    }
}
