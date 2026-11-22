using System.Text.Json.Serialization;

namespace Quasar.Models;

public sealed class DedicatedServerDefinition : EditableServerDefinition
{
    public DedicatedServerGoalState GoalState { get; set; } = DedicatedServerGoalState.Off;

    public DedicatedServerDefinition Clone()
    {
        return new DedicatedServerDefinition
        {
            UniqueName = UniqueName,
            DisplayName = DisplayName,
            InGameServerName = InGameServerName,
            InGameWorldName = InGameWorldName,
            OriginalUniqueName = OriginalUniqueName,
            GoalState = GoalState,
            ExecutablePath = ExecutablePath,
            WorkingDirectory = WorkingDirectory,
            ManagedRuntime = ManagedRuntime,
            DedicatedServerAppDataPath = DedicatedServerAppDataPath,
            MagnetarAppDataPath = MagnetarAppDataPath,
            WorldPath = WorldPath,
            WorldSaveName = WorldSaveName,
            ConfigFilePath = ConfigFilePath,
            ConfigProfileId = ConfigProfileId,
            WorldTemplateId = WorldTemplateId,
            LaunchArguments = LaunchArguments,
            DisableImplicitMagnetarModLoad = DisableImplicitMagnetarModLoad,
            LogLaunchEnvironment = LogLaunchEnvironment,
            AgentProfilerMode = AgentProfilerMode,
            DsLogFilesToKeep = DsLogFilesToKeep,
            LogMaxAgeDays = LogMaxAgeDays,
            LogMaxSizeMB = LogMaxSizeMB,
            ServerPort = ServerPort,
            ServerIP = ServerIP,
            AutoStart = AutoStart,
            EnableHealthMonitoring = EnableHealthMonitoring,
            AutoRestartOnUnhealthy = AutoRestartOnUnhealthy,
            AgentStartupGraceSeconds = AgentStartupGraceSeconds,
            AgentStartupHardLimitSeconds = AgentStartupHardLimitSeconds,
            AgentAttachRetryAttempts = AgentAttachRetryAttempts,
            AgentAttachRetryDelaySeconds = AgentAttachRetryDelaySeconds,
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
            CpuAffinity = CpuAffinity,
            UpdatedAtUtc = UpdatedAtUtc,
        };
    }

    public string GetWorldSavePath()
    {
        return DedicatedServerPathResolver.Resolve(this).WorldSavePath;
    }
}
