using System.Diagnostics;
using Magnetar.Protocol.Runtime;
using Magnetar.Protocol.Transport;
using Quasar.Models;

namespace Quasar.Services;

public sealed class DedicatedServerSupervisor : IHostedService, IDisposable
{
    private static readonly TimeSpan RestartCounterResetWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(2);
    private readonly object _sync = new();
    private readonly DedicatedServerInstanceCatalog _catalog;
    private readonly AgentRegistry _registry;
    private readonly DedicatedServerRuntimePreparer _runtimePreparer;
    private readonly WebServiceOptions _options;
    private readonly ILogger<DedicatedServerSupervisor> _logger;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, ManagedInstanceState> _states = new(StringComparer.OrdinalIgnoreCase);
    private bool _isStopping;

    public DedicatedServerSupervisor(
        DedicatedServerInstanceCatalog catalog,
        AgentRegistry registry,
        DedicatedServerRuntimePreparer runtimePreparer,
        WebServiceOptions options,
        ILogger<DedicatedServerSupervisor> logger)
    {
        _catalog = catalog;
        _registry = registry;
        _runtimePreparer = runtimePreparer;
        _options = options;
        _logger = logger;
    }

    public event Action? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SyncDefinitions();
        _catalog.Changed += HandleCatalogChanged;
        _ = Task.Run(() => ReconcileLoopAsync(_shutdown.Token), _shutdown.Token);
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _isStopping = true;
        _shutdown.Cancel();
        _catalog.Changed -= HandleCatalogChanged;

        var runningInstanceIds = GetSnapshots()
            .Where(snapshot => snapshot.State is DedicatedServerInstanceProcessState.Starting
                or DedicatedServerInstanceProcessState.Running
                or DedicatedServerInstanceProcessState.Restarting
                or DedicatedServerInstanceProcessState.Stopping)
            .Select(snapshot => snapshot.InstanceId)
            .ToList();

        foreach (var instanceId in runningInstanceIds)
        {
            try
            {
                await StopInstanceAsync(instanceId, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed stopping instance {InstanceId} during Quasar shutdown.", instanceId);
            }
        }
    }

    public IReadOnlyList<DedicatedServerInstanceRuntimeSnapshot> GetSnapshots()
    {
        List<DedicatedServerInstanceRuntimeSnapshot> snapshots;
        var agents = BuildAgentLookup();
        var now = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            snapshots = _states.Values
                .Select(state => CloneSnapshot(state, agents.TryGetValue(state.InstanceId, out var agent) ? agent : null, now))
                .OrderBy(snapshot => snapshot.Name, StringComparer.OrdinalIgnoreCase)
                .ThenBy(snapshot => snapshot.InstanceId, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return snapshots;
    }

    public async Task SetGoalStateAsync(
        string instanceId,
        DedicatedServerInstanceGoalState goalState,
        CancellationToken cancellationToken = default)
    {
        await _catalog.SetGoalStateAsync(instanceId, goalState, cancellationToken);
        await ReconcileAsync(cancellationToken);
    }

    public async Task StartInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        ManagedInstanceState? state;

        lock (_sync)
        {
            if (!_states.TryGetValue(instanceId, out state))
                throw new InvalidOperationException($"Unknown Quasar instance '{instanceId}'.");

            if (IsProcessActive(state.Process))
                return;

            state.StopRequested = false;
            state.State = state.IsRestartPending
                ? DedicatedServerInstanceProcessState.Restarting
                : DedicatedServerInstanceProcessState.Starting;
            state.LastMessage = "Starting process.";
            if (!state.IsRestartPending)
                state.RestartAttempts = 0;
        }

        NotifyChanged();
        await StartProcessAsync(state, cancellationToken);
    }

    public async Task StopInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        ManagedInstanceState? state;
        Process? process;

        lock (_sync)
        {
            if (!_states.TryGetValue(instanceId, out state))
                return;

            process = state.Process;
            state.StopRequested = true;
            state.IsRestartPending = false;

            if (!IsProcessActive(process))
            {
                state.State = DedicatedServerInstanceProcessState.Stopped;
                state.LastMessage = "Already stopped.";
                NotifyChanged();
                return;
            }

            state.State = DedicatedServerInstanceProcessState.Stopping;
            state.LastMessage = "Stopping process.";
        }

        NotifyChanged();

        await TryRequestGracefulStopAsync(instanceId, cancellationToken);

        if (process is null)
            return;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token);
        }
        catch (OperationCanceledException)
        {
            if (IsProcessActive(process))
            {
                _logger.LogWarning("Instance {InstanceId} did not stop gracefully. Killing process tree.", instanceId);
                process.Kill(entireProcessTree: true);
            }
        }
    }

    public async Task RestartInstanceAsync(string instanceId, CancellationToken cancellationToken = default)
    {
        var definition = _catalog.GetInstance(instanceId);
        if (definition is null)
            throw new InvalidOperationException($"Unknown Quasar instance '{instanceId}'.");

        definition.GoalState = DedicatedServerInstanceGoalState.On;
        definition.AutoStart = true;
        await _catalog.UpsertAsync(definition, cancellationToken);
        await StopInstanceAsync(instanceId, cancellationToken);
        await StartInstanceAsync(instanceId, cancellationToken);
    }

    public void Dispose()
    {
        _shutdown.Dispose();
    }

    private async Task ReconcileLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await ReconcileAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Quasar reconciliation loop iteration failed.");
            }

            try
            {
                await Task.Delay(ReconcileInterval, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }

    private async Task ReconcileAsync(CancellationToken cancellationToken)
    {
        List<(string InstanceId, ReconcileAction Action, string Reason)> actions = new();
        var agents = BuildAgentLookup();
        var now = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            foreach (var state in _states.Values)
            {
                var processActive = IsProcessActive(state.Process);
                var goalState = state.Definition.GoalState;
                var health = EvaluateHealth(state, agents.TryGetValue(state.InstanceId, out var agent) ? agent : null, now);

                if (goalState == DedicatedServerInstanceGoalState.On)
                {
                    if (!processActive && state.State is DedicatedServerInstanceProcessState.Stopped
                            or DedicatedServerInstanceProcessState.Crashed
                            or DedicatedServerInstanceProcessState.Faulted)
                    {
                        actions.Add((state.InstanceId, ReconcileAction.Start, "goal state is on"));
                    }
                    else if (processActive &&
                             health.State == DedicatedServerInstanceHealthState.Unhealthy &&
                             state.Definition.AutoRestartOnUnhealthy &&
                             state.State == DedicatedServerInstanceProcessState.Running &&
                             CanScheduleHealthRestart(state, now))
                    {
                        state.LastHealthRecoveryActionUtc = now;
                        actions.Add((state.InstanceId, ReconcileAction.Restart, health.Summary));
                    }
                }
                else
                {
                    if (processActive && state.State != DedicatedServerInstanceProcessState.Stopping)
                        actions.Add((state.InstanceId, ReconcileAction.Stop, "goal state is off"));
                }
            }
        }

        foreach (var (instanceId, action, reason) in actions)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            switch (action)
            {
                case ReconcileAction.Start:
                    await StartInstanceAsync(instanceId, cancellationToken);
                    break;

                case ReconcileAction.Stop:
                    await StopInstanceAsync(instanceId, cancellationToken);
                    break;

                case ReconcileAction.Restart:
                    _logger.LogWarning("Quasar health recovery restarting instance {InstanceId}: {Reason}", instanceId, reason);
                    await RestartInstanceAsync(instanceId, cancellationToken);
                    break;
            }
        }
    }

    private async Task StartProcessAsync(ManagedInstanceState state, CancellationToken cancellationToken)
    {
        var definition = state.Definition;
        var executablePath = definition.ExecutablePath?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(executablePath) || !File.Exists(executablePath))
        {
            SetFaulted(state.InstanceId, $"Executable not found: {executablePath}");
            return;
        }

        var workingDirectory = ResolveWorkingDirectory(definition, executablePath);
        if (!Directory.Exists(workingDirectory))
        {
            SetFaulted(state.InstanceId, $"Working directory not found: {workingDirectory}");
            return;
        }

        PreparedDedicatedServerLaunch launch;
        try
        {
            launch = await _runtimePreparer.PrepareAsync(definition, cancellationToken);
        }
        catch (Exception exception)
        {
            SetFaulted(state.InstanceId, exception.Message);
            _logger.LogWarning(exception, "Failed preparing runtime files for instance {InstanceId}.", state.InstanceId);
            return;
        }

        var logDirectory = MagnetarPaths.GetQuasarInstanceLogDirectory(state.InstanceId);
        Directory.CreateDirectory(logDirectory);

        var stdoutPath = Path.Combine(logDirectory, "stdout.log");
        var stderrPath = Path.Combine(logDirectory, "stderr.log");

        var process = new Process
        {
            StartInfo = new ProcessStartInfo
            {
                FileName = executablePath,
                Arguments = launch.Arguments,
                WorkingDirectory = workingDirectory,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            },
            EnableRaisingEvents = true,
        };

        process.StartInfo.Environment["QUASAR_INSTANCE_ID"] = definition.InstanceId;
        process.StartInfo.Environment["QUASAR_BASE_URL"] = _options.BaseUrl;
        process.StartInfo.Environment["MAGNETAR_NODE_ID"] = _options.NodeId;
        process.StartInfo.Environment["QUASAR_DS_APPDATA_PATH"] = launch.DedicatedServerAppDataPath;
        process.StartInfo.Environment["QUASAR_MAGNETAR_APPDATA_PATH"] = launch.MagnetarAppDataPath;
        process.StartInfo.Environment["QUASAR_WORLD_PATH"] = launch.WorldPath;
        process.StartInfo.Environment["QUASAR_DS_CONFIG_PATH"] = launch.RuntimeConfigPath;
        process.StartInfo.Environment["QUASAR_LAST_SESSION_PATH"] = launch.LastSessionPath;

        process.Exited += async (_, _) => await HandleProcessExitedAsync(state.InstanceId);

        try
        {
            if (!process.Start())
            {
                SetFaulted(state.InstanceId, "Process start returned false.");
                process.Dispose();
                return;
            }
        }
        catch (Exception exception)
        {
            SetFaulted(state.InstanceId, exception.Message);
            process.Dispose();
            return;
        }

        lock (_sync)
        {
            if (!_states.TryGetValue(state.InstanceId, out var current))
            {
                process.Kill(entireProcessTree: true);
                return;
            }

            current.Process = process;
            current.State = DedicatedServerInstanceProcessState.Running;
            current.ProcessId = process.Id;
            current.StartedAtUtc = DateTimeOffset.UtcNow;
            current.StoppedAtUtc = null;
            current.LastExitCode = null;
            current.LastMessage = "Process running.";
            current.StandardOutputLogPath = stdoutPath;
            current.StandardErrorLogPath = stderrPath;
            current.IsRestartPending = false;
            current.StopRequested = false;
        }

        _logger.LogInformation("Started instance {InstanceId} with pid {Pid}.", state.InstanceId, process.Id);
        NotifyChanged();

        _ = PumpOutputAsync(process.StandardOutput, stdoutPath, cancellationToken);
        _ = PumpOutputAsync(process.StandardError, stderrPath, cancellationToken);
    }

    private async Task HandleProcessExitedAsync(string instanceId)
    {
        ManagedInstanceState? state;
        DedicatedServerInstanceDefinition definition;
        int exitCode;
        bool stopRequested;
        bool shouldRestart = false;
        int restartDelaySeconds = 0;
        DateTimeOffset now = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            if (!_states.TryGetValue(instanceId, out state) || state.Process is null)
                return;

            definition = Clone(state.Definition);
            exitCode = SafeGetExitCode(state.Process);
            stopRequested = state.StopRequested || _isStopping;

            if (state.StartedAtUtc.HasValue && (now - state.StartedAtUtc.Value) >= RestartCounterResetWindow)
                state.RestartAttempts = 0;

            state.Process.Dispose();
            state.Process = null;
            state.ProcessId = null;
            state.LastExitCode = exitCode;
            state.StoppedAtUtc = now;
            state.StartedAtUtc = null;

            if (!stopRequested &&
                definition.GoalState == DedicatedServerInstanceGoalState.On &&
                definition.RestartOnCrash)
            {
                var nextAttempt = state.RestartAttempts + 1;
                var hasBudget = definition.MaxRestartAttempts <= 0 || nextAttempt <= definition.MaxRestartAttempts;
                if (hasBudget)
                {
                    state.RestartAttempts = nextAttempt;
                    state.IsRestartPending = true;
                    state.State = DedicatedServerInstanceProcessState.Restarting;
                    state.LastMessage = $"Process exited with code {exitCode}. Restarting.";
                    shouldRestart = true;
                    restartDelaySeconds = Math.Max(0, definition.RestartDelaySeconds);
                }
            }

            if (!shouldRestart)
            {
                state.IsRestartPending = false;
                state.State = stopRequested
                    ? DedicatedServerInstanceProcessState.Stopped
                    : exitCode == 0
                        ? DedicatedServerInstanceProcessState.Stopped
                        : DedicatedServerInstanceProcessState.Crashed;
                state.LastMessage = stopRequested
                    ? "Stopped by supervisor."
                    : exitCode == 0
                        ? "Process exited normally."
                        : $"Process exited with code {exitCode}.";
            }
        }

        NotifyChanged();
        _logger.LogInformation("Instance {InstanceId} exited with code {ExitCode}.", instanceId, exitCode);

        if (!shouldRestart)
            return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(restartDelaySeconds), _shutdown.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_sync)
        {
            if (!_states.TryGetValue(instanceId, out state) || state.StopRequested || state.State != DedicatedServerInstanceProcessState.Restarting)
                return;
        }

        await StartInstanceAsync(instanceId, _shutdown.Token);
    }

    private async Task TryRequestGracefulStopAsync(string instanceId, CancellationToken cancellationToken)
    {
        var agent = _registry.GetAgents().FirstOrDefault(current =>
            current.IsConnected &&
            string.Equals(current.InstanceKey, instanceId, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
            return;

        try
        {
            await _registry.SendCommandAsync(new ServerCommandEnvelope
            {
                AgentId = agent.AgentId,
                InstanceId = instanceId,
                ServerId = agent.ServerKey,
                CommandType = ServerCommandType.StopServer,
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to send graceful stop to Quasar.Agent for instance {InstanceId}.", instanceId);
        }
    }

    private void HandleCatalogChanged()
    {
        SyncDefinitions();
        NotifyChanged();
        _ = Task.Run(() => ReconcileAsync(_shutdown.Token), _shutdown.Token);
    }

    private void SyncDefinitions()
    {
        var definitions = _catalog.GetInstances();

        lock (_sync)
        {
            foreach (var definition in definitions)
            {
                if (_states.TryGetValue(definition.InstanceId, out var state))
                {
                    state.Definition = Clone(definition);
                    if (string.IsNullOrWhiteSpace(state.LastMessage))
                        state.LastMessage = "Stopped.";
                }
                else
                {
                    _states.Add(definition.InstanceId, new ManagedInstanceState
                    {
                        InstanceId = definition.InstanceId,
                        Definition = Clone(definition),
                        State = DedicatedServerInstanceProcessState.Stopped,
                        LastMessage = "Stopped.",
                    });
                }
            }

            var configuredIds = definitions.Select(definition => definition.InstanceId)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var stale in _states.Values
                         .Where(state => !configuredIds.Contains(state.InstanceId) && !IsProcessActive(state.Process))
                         .Select(state => state.InstanceId)
                         .ToList())
            {
                _states.Remove(stale);
            }
        }
    }

    private void SetFaulted(string instanceId, string message)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(instanceId, out var state))
                return;

            state.State = DedicatedServerInstanceProcessState.Faulted;
            state.Process = null;
            state.ProcessId = null;
            state.IsRestartPending = false;
            state.LastMessage = message;
            state.StoppedAtUtc = DateTimeOffset.UtcNow;
        }

        _logger.LogWarning("Instance {InstanceId} faulted: {Message}", instanceId, message);
        NotifyChanged();
    }

    private static string ResolveWorkingDirectory(DedicatedServerInstanceDefinition definition, string executablePath)
    {
        if (!string.IsNullOrWhiteSpace(definition.WorkingDirectory))
            return definition.WorkingDirectory.Trim();

        return Path.GetDirectoryName(executablePath) ?? AppContext.BaseDirectory;
    }

    private static async Task PumpOutputAsync(StreamReader reader, string path, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite);
        await using var writer = new StreamWriter(stream)
        {
            AutoFlush = true,
        };

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line is null)
                break;

            await writer.WriteLineAsync($"{DateTimeOffset.UtcNow:O} {line}");
        }
    }

    private static bool IsProcessActive(Process? process) =>
        process is not null && !process.HasExited;

    private Dictionary<string, AgentRuntimeState> BuildAgentLookup()
    {
        return _registry.GetAgents()
            .Where(agent => !string.IsNullOrWhiteSpace(agent.InstanceKey))
            .GroupBy(agent => agent.InstanceKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(agent => agent.LastSeenUtc).First(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static InstanceHealthAssessment EvaluateHealth(
        ManagedInstanceState state,
        AgentRuntimeState? agent,
        DateTimeOffset now)
    {
        var processActive = IsProcessActive(state.Process);
        var goalState = state.Definition.GoalState;

        if (goalState == DedicatedServerInstanceGoalState.Off)
        {
            return processActive
                ? new InstanceHealthAssessment(DedicatedServerInstanceHealthState.Warning, "Process still running while goal state is off.")
                : new InstanceHealthAssessment(DedicatedServerInstanceHealthState.Healthy, "Goal state is off and the instance is stopped.");
        }

        if (!processActive)
        {
            return new InstanceHealthAssessment(
                state.State == DedicatedServerInstanceProcessState.Starting
                    ? DedicatedServerInstanceHealthState.Warning
                    : DedicatedServerInstanceHealthState.Unhealthy,
                state.State == DedicatedServerInstanceProcessState.Starting
                    ? "Process is starting."
                    : "Goal state is on but the process is not running.");
        }

        if (!state.Definition.EnableHealthMonitoring)
            return new InstanceHealthAssessment(DedicatedServerInstanceHealthState.Unknown, "Health monitoring disabled.");

        var uptime = state.StartedAtUtc.HasValue ? now - state.StartedAtUtc.Value : TimeSpan.Zero;

        if (agent is null || !agent.IsConnected)
        {
            if (uptime < TimeSpan.FromSeconds(state.Definition.AgentStartupGraceSeconds))
            {
                return new InstanceHealthAssessment(
                    DedicatedServerInstanceHealthState.Warning,
                    "Waiting for Quasar.Agent to attach.");
            }

            return new InstanceHealthAssessment(
                DedicatedServerInstanceHealthState.Unhealthy,
                "Quasar.Agent did not attach within the configured startup grace period.");
        }

        var silence = now - agent.LastSeenUtc;
        if (silence > TimeSpan.FromSeconds(state.Definition.AgentHeartbeatTimeoutSeconds))
        {
            return new InstanceHealthAssessment(
                DedicatedServerInstanceHealthState.Unhealthy,
                $"Quasar.Agent heartbeat stale for {Math.Round(silence.TotalSeconds)}s.");
        }

        if (state.Definition.RecycleAfterUptimeHours > 0 &&
            uptime >= TimeSpan.FromHours(state.Definition.RecycleAfterUptimeHours))
        {
            return new InstanceHealthAssessment(
                DedicatedServerInstanceHealthState.Unhealthy,
                $"Process uptime exceeded recycle threshold of {state.Definition.RecycleAfterUptimeHours}h.");
        }

        if (state.Definition.WarnAfterUptimeHours > 0 &&
            uptime >= TimeSpan.FromHours(state.Definition.WarnAfterUptimeHours))
        {
            return new InstanceHealthAssessment(
                DedicatedServerInstanceHealthState.Warning,
                $"Process uptime exceeded warning threshold of {state.Definition.WarnAfterUptimeHours}h.");
        }

        return new InstanceHealthAssessment(DedicatedServerInstanceHealthState.Healthy, "Instance healthy.");
    }

    private static bool CanScheduleHealthRestart(ManagedInstanceState state, DateTimeOffset now)
    {
        if (!state.LastHealthRecoveryActionUtc.HasValue)
            return true;

        return (now - state.LastHealthRecoveryActionUtc.Value) >= TimeSpan.FromSeconds(Math.Max(30, state.Definition.RestartDelaySeconds));
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    private static int SafeGetExitCode(Process process)
    {
        try
        {
            return process.ExitCode;
        }
        catch
        {
            return -1;
        }
    }

    private static DedicatedServerInstanceDefinition Clone(DedicatedServerInstanceDefinition definition)
    {
        return new DedicatedServerInstanceDefinition
        {
            InstanceId = definition.InstanceId,
            Name = definition.Name,
            GoalState = definition.GoalState,
            ExecutablePath = definition.ExecutablePath,
            WorkingDirectory = definition.WorkingDirectory,
            DedicatedServerAppDataPath = definition.DedicatedServerAppDataPath,
            MagnetarAppDataPath = definition.MagnetarAppDataPath,
            WorldPath = definition.WorldPath,
            ConfigFilePath = definition.ConfigFilePath,
            LaunchArguments = definition.LaunchArguments,
            AutoStart = definition.AutoStart,
            EnableHealthMonitoring = definition.EnableHealthMonitoring,
            AutoRestartOnUnhealthy = definition.AutoRestartOnUnhealthy,
            AgentStartupGraceSeconds = definition.AgentStartupGraceSeconds,
            AgentHeartbeatTimeoutSeconds = definition.AgentHeartbeatTimeoutSeconds,
            WarnAfterUptimeHours = definition.WarnAfterUptimeHours,
            RecycleAfterUptimeHours = definition.RecycleAfterUptimeHours,
            RestartOnCrash = definition.RestartOnCrash,
            RestartDelaySeconds = definition.RestartDelaySeconds,
            MaxRestartAttempts = definition.MaxRestartAttempts,
            UpdatedAtUtc = definition.UpdatedAtUtc,
        };
    }

    private static DedicatedServerInstanceRuntimeSnapshot CloneSnapshot(
        ManagedInstanceState state,
        AgentRuntimeState? agent,
        DateTimeOffset now)
    {
        var health = EvaluateHealth(state, agent, now);
        return new DedicatedServerInstanceRuntimeSnapshot
        {
            InstanceId = state.InstanceId,
            Name = state.Definition.Name,
            GoalState = state.Definition.GoalState,
            State = state.State,
            HealthState = health.State,
            HealthSummary = health.Summary,
            RestartAttempts = state.RestartAttempts,
            ProcessId = state.ProcessId,
            LastExitCode = state.LastExitCode,
            LastMessage = state.LastMessage,
            AgentAttached = agent?.IsConnected == true,
            AgentLastSeenUtc = agent?.LastSeenUtc,
            StartedAtUtc = state.StartedAtUtc,
            StoppedAtUtc = state.StoppedAtUtc,
            StandardOutputLogPath = state.StandardOutputLogPath,
            StandardErrorLogPath = state.StandardErrorLogPath,
        };
    }

    private sealed class ManagedInstanceState
    {
        public string InstanceId { get; set; } = string.Empty;

        public DedicatedServerInstanceDefinition Definition { get; set; } = new();

        public DedicatedServerInstanceProcessState State { get; set; } = DedicatedServerInstanceProcessState.Stopped;

        public Process? Process { get; set; }

        public bool StopRequested { get; set; }

        public bool IsRestartPending { get; set; }

        public int RestartAttempts { get; set; }

        public int? ProcessId { get; set; }

        public int? LastExitCode { get; set; }

        public string LastMessage { get; set; } = string.Empty;

        public DateTimeOffset? StartedAtUtc { get; set; }

        public DateTimeOffset? StoppedAtUtc { get; set; }

        public string StandardOutputLogPath { get; set; } = string.Empty;

        public string StandardErrorLogPath { get; set; } = string.Empty;

        public DateTimeOffset? LastHealthRecoveryActionUtc { get; set; }
    }

    private enum ReconcileAction
    {
        Start = 0,
        Stop = 1,
        Restart = 2,
    }

    private readonly record struct InstanceHealthAssessment(
        DedicatedServerInstanceHealthState State,
        string Summary);
}
