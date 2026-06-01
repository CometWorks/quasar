using System.Diagnostics;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
using Magnetar.Protocol.Transport;
using Quasar.Models;
using Quasar.Services.PluginSdk;

namespace Quasar.Services;

public sealed class DedicatedServerSupervisor : IHostedService, IDisposable
{
    private static readonly JsonSerializerOptions PersistedStateJsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    private static readonly TimeSpan RestartCounterResetWindow = TimeSpan.FromMinutes(1);
    private static readonly TimeSpan ReconcileInterval = TimeSpan.FromSeconds(2);
    private readonly object _sync = new();
    private readonly DedicatedServerInstanceCatalog _catalog;
    private readonly AgentRegistry _registry;
    private readonly DedicatedServerRuntimePreparer _runtimePreparer;
    private readonly ManagedDedicatedServerRuntimeResolver _runtimeResolver;
    private readonly WebServiceOptions _options;
    private readonly ILogger<DedicatedServerSupervisor> _logger;
    private readonly PluginLogStream _pluginLogStream;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, ManagedInstanceState> _states = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _persistDebounce;
    private bool _isStopping;
    private bool _preserveManagedInstancesOnShutdown;

    public DedicatedServerSupervisor(
        DedicatedServerInstanceCatalog catalog,
        AgentRegistry registry,
        DedicatedServerRuntimePreparer runtimePreparer,
        ManagedDedicatedServerRuntimeResolver runtimeResolver,
        WebServiceOptions options,
        PluginLogStream pluginLogStream,
        ILogger<DedicatedServerSupervisor> logger)
    {
        _catalog = catalog;
        _registry = registry;
        _runtimePreparer = runtimePreparer;
        _runtimeResolver = runtimeResolver;
        _options = options;
        _pluginLogStream = pluginLogStream;
        _logger = logger;
    }

    public event Action? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SyncDefinitions();
        RestorePersistedRuntimeState();
        _catalog.Changed += HandleCatalogChanged;
        _ = Task.Run(() => ReconcileLoopAsync(_shutdown.Token), _shutdown.Token);
        SchedulePersistState();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _isStopping = true;
        _shutdown.Cancel();
        _catalog.Changed -= HandleCatalogChanged;

        if (_options.PreserveManagedInstancesOnShutdown || _preserveManagedInstancesOnShutdown)
        {
            await PersistStateSnapshotAsync(CancellationToken.None);
            return;
        }

        var runningUniqueNames = GetSnapshots()
            .Where(snapshot => snapshot.State is DedicatedServerInstanceProcessState.Starting
                or DedicatedServerInstanceProcessState.Running
                or DedicatedServerInstanceProcessState.Restarting
                or DedicatedServerInstanceProcessState.Stopping)
            .Select(snapshot => snapshot.UniqueName)
            .ToList();

        foreach (var uniqueName in runningUniqueNames)
        {
            try
            {
                await StopInstanceAsync(uniqueName, cancellationToken);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed stopping instance {UniqueName} during Quasar shutdown.", uniqueName);
            }
        }

        await PersistStateSnapshotAsync(CancellationToken.None);
    }

    public IReadOnlyList<DedicatedServerInstanceRuntimeSnapshot> GetSnapshots()
    {
        List<DedicatedServerInstanceRuntimeSnapshot> snapshots;
        var agents = BuildAgentLookup();
        var now = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            snapshots = _states.Values
                .Select(state => CloneSnapshot(
                    state,
                    agents.TryGetValue(state.UniqueName, out var agent) ? agent : null,
                    now,
                    _options.DisableInstanceHealthMonitoring))
                .OrderBy(snapshot => snapshot.UniqueName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(snapshot => snapshot.UniqueName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return snapshots;
    }

    public async Task SetGoalStateAsync(
        string uniqueName,
        DedicatedServerInstanceGoalState goalState,
        CancellationToken cancellationToken = default)
    {
        await _catalog.SetGoalStateAsync(uniqueName, goalState, cancellationToken);
        await ReconcileAsync(cancellationToken);
    }

    public async Task StartInstanceAsync(string uniqueName, CancellationToken cancellationToken = default)
    {
        ManagedInstanceState? state;

        lock (_sync)
        {
            if (!_states.TryGetValue(uniqueName, out state))
                throw new InvalidOperationException($"Unknown Quasar instance '{uniqueName}'.");

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

    public async Task StopInstanceAsync(string uniqueName, CancellationToken cancellationToken = default)
    {
        ManagedInstanceState? state;
        Process? process;

        lock (_sync)
        {
            if (!_states.TryGetValue(uniqueName, out state))
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

        await TryRequestGracefulStopAsync(uniqueName, cancellationToken);

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
                _logger.LogWarning("Instance {UniqueName} did not stop gracefully. Killing process tree.", uniqueName);
                process.Kill(entireProcessTree: true);
            }
        }
    }

    public async Task RestartInstanceAsync(string uniqueName, CancellationToken cancellationToken = default)
    {
        var definition = _catalog.GetInstance(uniqueName);
        if (definition is null)
            throw new InvalidOperationException($"Unknown Quasar instance '{uniqueName}'.");

        definition.GoalState = DedicatedServerInstanceGoalState.On;
        definition.AutoStart = true;
        await _catalog.UpsertAsync(definition, cancellationToken);
        await StopInstanceAsync(uniqueName, cancellationToken);
        await StartInstanceAsync(uniqueName, cancellationToken);
    }

    public void Dispose()
    {
        // Take ownership under the same lock used by SchedulePersistState so we cannot
        // race with a concurrent cancel/dispose/recreate of _persistDebounce.
        CancellationTokenSource? debounce;
        lock (_sync)
        {
            debounce = _persistDebounce;
            _persistDebounce = null;
        }
        debounce?.Cancel();
        debounce?.Dispose();
        _shutdown.Dispose();
    }

    public void BeginLauncherDrain()
    {
        _preserveManagedInstancesOnShutdown = true;
        PersistStateSnapshotAsync(CancellationToken.None).GetAwaiter().GetResult();
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
        List<(string UniqueName, ReconcileAction Action, string Reason)> actions = new();
        var agents = BuildAgentLookup();
        var now = DateTimeOffset.UtcNow;
        var healthChanged = false;

        lock (_sync)
        {
            foreach (var state in _states.Values)
            {
                var processActive = IsProcessActive(state.Process);
                var goalState = state.Definition.GoalState;
                var health = EvaluateHealth(
                    state,
                    agents.TryGetValue(state.UniqueName, out var agent) ? agent : null,
                    now,
                    _options.DisableInstanceHealthMonitoring);

                if (state.HealthState != health.State ||
                    !string.Equals(state.HealthSummary, health.Summary, StringComparison.Ordinal))
                {
                    state.HealthState = health.State;
                    state.HealthSummary = health.Summary;
                    healthChanged = true;
                }

                state.SimulationProgressScore = health.SimulationProgressScore;
                state.SimulationProgressWindowSeconds = health.SimulationProgressWindowSeconds;
                state.SimulationFramesAdvanced = health.SimulationFramesAdvanced;

                if (goalState == DedicatedServerInstanceGoalState.On)
                {
                    if (!processActive && state.State is DedicatedServerInstanceProcessState.Stopped
                            or DedicatedServerInstanceProcessState.Crashed
                            or DedicatedServerInstanceProcessState.Faulted)
                    {
                        actions.Add((state.UniqueName, ReconcileAction.Start, "goal state is on"));
                    }
                    else if (processActive &&
                             health.State == DedicatedServerInstanceHealthState.Unhealthy &&
                             state.Definition.AutoRestartOnUnhealthy &&
                             state.State == DedicatedServerInstanceProcessState.Running &&
                             CanScheduleHealthRestart(state, now))
                    {
                        state.LastHealthRecoveryActionUtc = now;
                        actions.Add((state.UniqueName, ReconcileAction.Restart, health.Summary));
                    }
                }
                else
                {
                    if (processActive && state.State != DedicatedServerInstanceProcessState.Stopping)
                        actions.Add((state.UniqueName, ReconcileAction.Stop, "goal state is off"));
                }
            }
        }

        if (healthChanged)
            NotifyChanged();

        foreach (var (uniqueName, action, reason) in actions)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            switch (action)
            {
                case ReconcileAction.Start:
                    await StartInstanceAsync(uniqueName, cancellationToken);
                    break;

                case ReconcileAction.Stop:
                    await StopInstanceAsync(uniqueName, cancellationToken);
                    break;

                case ReconcileAction.Restart:
                    _logger.LogWarning("Quasar health recovery restarting instance {UniqueName}: {Reason}", uniqueName, reason);
                    await RestartInstanceAsync(uniqueName, cancellationToken);
                    break;
            }
        }
    }

    private async Task StartProcessAsync(ManagedInstanceState state, CancellationToken cancellationToken)
    {
        var definition = state.Definition;
        ResolvedDedicatedServerRuntime runtime;
        try
        {
            runtime = await _runtimeResolver.ResolveAsync(definition, cancellationToken);
        }
        catch (Exception exception)
        {
            SetFaulted(state.UniqueName, exception.Message);
            _logger.LogWarning(exception, "Failed resolving managed runtime for instance {UniqueName}.", state.UniqueName);
            return;
        }

        var executablePath = runtime.ExecutablePath;
        var workingDirectory = runtime.WorkingDirectory;
        if (!File.Exists(executablePath))
        {
            SetFaulted(state.UniqueName, $"Executable not found: {executablePath}");
            return;
        }

        if (!Directory.Exists(workingDirectory))
        {
            SetFaulted(state.UniqueName, $"Working directory not found: {workingDirectory}");
            return;
        }

        PreparedDedicatedServerLaunch launch;
        try
        {
            launch = await _runtimePreparer.PrepareAsync(definition, runtime.DedicatedServer64Path, cancellationToken);
        }
        catch (Exception exception)
        {
            SetFaulted(state.UniqueName, exception.Message);
            _logger.LogWarning(exception, "Failed preparing runtime files for instance {UniqueName}.", state.UniqueName);
            return;
        }

        var logDirectory = MagnetarPaths.GetQuasarInstanceLogDirectory(state.UniqueName);
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

        process.StartInfo.Environment["QUASAR_UNIQUE_NAME"] = definition.UniqueName;
        process.StartInfo.Environment["QUASAR_BASE_URL"] = _options.BaseUrl;
        process.StartInfo.Environment["MAGNETAR_NODE_ID"] = _options.NodeId;
        process.StartInfo.Environment["QUASAR_DS_APPDATA_PATH"] = launch.DedicatedServerAppDataPath;
        process.StartInfo.Environment["QUASAR_MAGNETAR_APPDATA_PATH"] = launch.MagnetarAppDataPath;
        process.StartInfo.Environment["QUASAR_DS64_PATH"] = launch.DedicatedServer64Path;
        process.StartInfo.Environment["QUASAR_WORLD_PATH"] = launch.WorldPath;
        process.StartInfo.Environment["QUASAR_DS_CONFIG_PATH"] = launch.RuntimeConfigPath;
        process.StartInfo.Environment["QUASAR_LAST_SESSION_PATH"] = launch.LastSessionPath;

        // Activate the PluginSdk QuasarLogSink inside the dedicated server: any
        // non-empty QUASAR_AGENT value makes plugins emit structured JSON log
        // lines on standard output (LogEnvironment.IsManagedByQuasar), which the
        // supervisor parses into the plugin log stream below.
        process.StartInfo.Environment["QUASAR_AGENT"] = definition.UniqueName;

        process.Exited += async (_, _) => await HandleProcessExitedAsync(state.UniqueName);

        try
        {
            if (!process.Start())
            {
                SetFaulted(state.UniqueName, "Process start returned false.");
                process.Dispose();
                return;
            }
        }
        catch (Exception exception)
        {
            SetFaulted(state.UniqueName, exception.Message);
            process.Dispose();
            return;
        }

        lock (_sync)
        {
            if (!_states.TryGetValue(state.UniqueName, out var current))
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
            ResetHealthTracking(current);
        }

        _logger.LogInformation("Started instance {UniqueName} with pid {Pid}.", state.UniqueName, process.Id);
        NotifyChanged();

        _ = PumpStandardOutputAsync(process.StandardOutput, stdoutPath, state.UniqueName, cancellationToken);
        _ = PumpStandardErrorAsync(process.StandardError, stderrPath, state.UniqueName, cancellationToken);
    }

    private async Task HandleProcessExitedAsync(string uniqueName)
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
            if (!_states.TryGetValue(uniqueName, out state) || state.Process is null)
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
            ResetHealthTracking(state);

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
        _logger.LogInformation("Instance {UniqueName} exited with code {ExitCode}.", uniqueName, exitCode);

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
            if (!_states.TryGetValue(uniqueName, out state) || state.StopRequested || state.State != DedicatedServerInstanceProcessState.Restarting)
                return;
        }

        await StartInstanceAsync(uniqueName, _shutdown.Token);
    }

    private async Task TryRequestGracefulStopAsync(string uniqueName, CancellationToken cancellationToken)
    {
        var agent = _registry.GetAgents().FirstOrDefault(current =>
            current.IsConnected &&
            string.Equals(current.UniqueNameKey, uniqueName, StringComparison.OrdinalIgnoreCase));

        if (agent is null)
            return;

        try
        {
            await _registry.SendCommandAsync(new ServerCommandEnvelope
            {
                AgentId = agent.AgentId,
                UniqueName = uniqueName,
                ServerId = agent.ServerKey,
                CommandType = ServerCommandType.StopServer,
            }, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to send graceful stop to Quasar.Agent for instance {UniqueName}.", uniqueName);
        }
    }

    private void HandleCatalogChanged()
    {
        SyncDefinitions();
        NotifyChanged();
        _ = Task.Run(() => ReconcileAsync(_shutdown.Token), _shutdown.Token);
    }

    private void RestorePersistedRuntimeState()
    {
        var persisted = LoadPersistedRuntimeState();
        if (persisted?.Instances is null || persisted.Instances.Count == 0)
            return;

        lock (_sync)
        {
            foreach (var persistedState in persisted.Instances)
            {
                if (!_states.TryGetValue(persistedState.UniqueName, out var state))
                    continue;

                state.RestartAttempts = Math.Max(0, persistedState.RestartAttempts);
                state.LastExitCode = persistedState.LastExitCode;
                state.StartedAtUtc = persistedState.StartedAtUtc;
                state.StoppedAtUtc = persistedState.StoppedAtUtc;
                state.StandardOutputLogPath = persistedState.StandardOutputLogPath ?? string.Empty;
                state.StandardErrorLogPath = persistedState.StandardErrorLogPath ?? string.Empty;
                state.LastHealthRecoveryActionUtc = persistedState.LastHealthRecoveryActionUtc;
                state.StopRequested = false;
                state.IsRestartPending = false;

                if (persistedState.ProcessId is > 0 && TryAdoptProcess(persistedState.ProcessId.Value, out var process))
                {
                    process.EnableRaisingEvents = true;
                    process.Exited += async (_, _) => await HandleProcessExitedAsync(state.UniqueName);

                    state.Process = process;
                    state.ProcessId = process.Id;
                    state.State = persistedState.State is DedicatedServerInstanceProcessState.Starting
                        or DedicatedServerInstanceProcessState.Running
                        or DedicatedServerInstanceProcessState.Restarting
                        or DedicatedServerInstanceProcessState.Stopping
                        ? persistedState.State
                        : DedicatedServerInstanceProcessState.Running;
                    state.LastMessage = "Process adopted after Quasar worker turnover.";
                    state.StoppedAtUtc = null;
                }
                else
                {
                    state.Process = null;
                    state.ProcessId = null;

                    if (state.State is DedicatedServerInstanceProcessState.Starting
                        or DedicatedServerInstanceProcessState.Running
                        or DedicatedServerInstanceProcessState.Restarting
                        or DedicatedServerInstanceProcessState.Stopping)
                    {
                        state.State = DedicatedServerInstanceProcessState.Stopped;
                        state.LastMessage = "Previously running process not found during supervisor restore.";
                    }
                }
            }
        }
    }

    private void SyncDefinitions()
    {
        var definitions = _catalog.GetInstances();

        lock (_sync)
        {
            foreach (var definition in definitions)
            {
                if (_states.TryGetValue(definition.UniqueName, out var state))
                {
                    state.Definition = Clone(definition);
                    if (string.IsNullOrWhiteSpace(state.LastMessage))
                        state.LastMessage = "Stopped.";
                }
                else
                {
                    _states.Add(definition.UniqueName, new ManagedInstanceState
                    {
                        UniqueName = definition.UniqueName,
                        Definition = Clone(definition),
                        State = DedicatedServerInstanceProcessState.Stopped,
                        LastMessage = "Stopped.",
                    });
                }
            }

            var configuredIds = definitions.Select(definition => definition.UniqueName)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            foreach (var stale in _states.Values
                         .Where(state => !configuredIds.Contains(state.UniqueName) && !IsProcessActive(state.Process))
                         .Select(state => state.UniqueName)
                         .ToList())
            {
                _states.Remove(stale);
            }
        }
    }

    private PersistedSupervisorState? LoadPersistedRuntimeState()
    {
        try
        {
            var path = MagnetarPaths.GetQuasarSupervisorStatePath();
            if (!File.Exists(path))
                return null;

            return JsonSerializer.Deserialize<PersistedSupervisorState>(File.ReadAllText(path), PersistedStateJsonOptions);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed loading persisted Quasar supervisor runtime state.");
            return null;
        }
    }

    private void SetFaulted(string uniqueName, string message)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(uniqueName, out var state))
                return;

            state.State = DedicatedServerInstanceProcessState.Faulted;
            state.Process = null;
            state.ProcessId = null;
            state.IsRestartPending = false;
            state.LastMessage = message;
            state.StoppedAtUtc = DateTimeOffset.UtcNow;
            ResetHealthTracking(state);
        }

        _logger.LogWarning("Instance {UniqueName} faulted: {Message}", uniqueName, message);
        NotifyChanged();
    }

    private const string MagnetarLogSource = "magnetar";

    private async Task PumpStandardOutputAsync(StreamReader reader, string path, string uniqueName, CancellationToken cancellationToken)
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

            if (PluginLogStream.TryParseSinkLine(uniqueName, line, out var entry) && entry is not null)
                _pluginLogStream.Append(entry);
            else if (!string.IsNullOrWhiteSpace(line))
                _pluginLogStream.Append(BuildMagnetarEntry(uniqueName, line, "Info"));
        }
    }

    private async Task PumpStandardErrorAsync(StreamReader reader, string path, string uniqueName, CancellationToken cancellationToken)
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

            if (!string.IsNullOrWhiteSpace(line))
                _pluginLogStream.Append(BuildMagnetarEntry(uniqueName, line, "Error"));
        }
    }

    private static PluginLogEntry BuildMagnetarEntry(string uniqueName, string line, string level) => new()
    {
        UniqueName = uniqueName,
        TimestampUtc = DateTimeOffset.UtcNow,
        Level = level,
        Plugin = MagnetarLogSource,
        Message = line,
    };

    private static bool IsProcessActive(Process? process) =>
        process is not null && !process.HasExited;

    private Dictionary<string, AgentRuntimeState> BuildAgentLookup()
    {
        return _registry.GetAgents()
            .Where(agent => !string.IsNullOrWhiteSpace(agent.UniqueNameKey))
            .GroupBy(agent => agent.UniqueNameKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(agent => agent.LastSeenUtc).First(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static InstanceHealthAssessment EvaluateHealth(
        ManagedInstanceState state,
        AgentRuntimeState? agent,
        DateTimeOffset now,
        bool disableHealthMonitoring)
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

        if (disableHealthMonitoring)
            return new InstanceHealthAssessment(DedicatedServerInstanceHealthState.Unknown, "Health monitoring disabled for local/dev launch.");

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
                $"Quasar.Agent heartbeat stale beyond {state.Definition.AgentHeartbeatTimeoutSeconds}s timeout.");
        }

        var simulationProgress = EvaluateSimulationProgress(state, agent, uptime);
        if (simulationProgress.State != DedicatedServerInstanceHealthState.Healthy)
            return simulationProgress;

        if (simulationProgress.SimulationProgressScore.HasValue &&
            simulationProgress.SimulationProgressWindowSeconds.HasValue &&
            simulationProgress.SimulationProgressWindowSeconds.Value > 0)
        {
            state.LastHealthySummary = $"Instance healthy. Frame progress score {simulationProgress.SimulationProgressScore.Value:0.00} over {simulationProgress.SimulationProgressWindowSeconds.Value}s.";
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

        return new InstanceHealthAssessment(
            DedicatedServerInstanceHealthState.Healthy,
            string.IsNullOrWhiteSpace(state.LastHealthySummary) ? "Instance healthy." : state.LastHealthySummary,
            state.SimulationProgressScore,
            state.SimulationProgressWindowSeconds,
            state.SimulationFramesAdvanced);
    }

    private static InstanceHealthAssessment EvaluateSimulationProgress(
        ManagedInstanceState state,
        AgentRuntimeState agent,
        TimeSpan uptime)
    {
        var snapshot = agent.Snapshot;
        var metrics = snapshot?.Metrics;
        if (snapshot is null || metrics is null)
            return BuildExistingSimulationAssessment(state, "Waiting for Quasar.Agent snapshot.");

        var capturedAt = snapshot.CapturedAtUtc == default ? agent.LastSeenUtc : snapshot.CapturedAtUtc;
        if (capturedAt == default || metrics.SimulationFrameCounter == 0)
            return BuildExistingSimulationAssessment(state, "Collecting simulation progress baseline.");

        if (!state.LastSimulationFrameCounter.HasValue ||
            !state.LastSimulationFrameObservedAtUtc.HasValue ||
            capturedAt <= state.LastSimulationFrameObservedAtUtc.Value ||
            metrics.SimulationFrameCounter < state.LastSimulationFrameCounter.Value)
        {
            SetSimulationBaseline(state, capturedAt, metrics.SimulationFrameCounter);
            return BuildExistingSimulationAssessment(state, "Collecting simulation progress baseline.");
        }

        if (metrics.IsSaveInProgress)
        {
            SetSimulationBaseline(state, capturedAt, metrics.SimulationFrameCounter);
            return BuildExistingSimulationAssessment(state, "Collecting simulation progress baseline.");
        }

        var requiredWindow = TimeSpan.FromSeconds(Math.Max(1, state.Definition.SimulationProgressWindowSeconds));
        var elapsed = capturedAt - state.LastSimulationFrameObservedAtUtc.Value;
        if (elapsed < requiredWindow)
            return BuildExistingSimulationAssessment(state, "Collecting simulation progress baseline.");

        var frameDelta = metrics.SimulationFrameCounter - state.LastSimulationFrameCounter.Value;
        var score = (float)(frameDelta / (elapsed.TotalSeconds * 60d));

        state.SimulationProgressScore = score;
        state.SimulationProgressWindowSeconds = (int)Math.Round(elapsed.TotalSeconds);
        state.SimulationFramesAdvanced = frameDelta;
        state.LastSimulationProgressEvaluatedAtUtc = capturedAt;

        SetSimulationBaseline(state, capturedAt, metrics.SimulationFrameCounter);

        if (score < state.Definition.MinimumSimulationProgressScore)
        {
            return new InstanceHealthAssessment(
                DedicatedServerInstanceHealthState.Unhealthy,
                $"Simulation frame progress score {score:0.00} is below minimum {state.Definition.MinimumSimulationProgressScore:0.00} over {elapsed.TotalSeconds:0.#}s ({frameDelta} frames advanced).",
                score,
                state.SimulationProgressWindowSeconds,
                frameDelta);
        }

        return new InstanceHealthAssessment(
            DedicatedServerInstanceHealthState.Healthy,
            "Instance healthy.",
            score,
            state.SimulationProgressWindowSeconds,
            frameDelta);
    }

    private static InstanceHealthAssessment BuildExistingSimulationAssessment(ManagedInstanceState state, string waitingSummary)
    {
        if (!state.SimulationProgressScore.HasValue ||
            !state.SimulationProgressWindowSeconds.HasValue ||
            !state.SimulationFramesAdvanced.HasValue)
        {
            return new InstanceHealthAssessment(DedicatedServerInstanceHealthState.Warning, waitingSummary);
        }

        if (state.SimulationProgressScore.Value < state.Definition.MinimumSimulationProgressScore)
        {
            return new InstanceHealthAssessment(
                DedicatedServerInstanceHealthState.Unhealthy,
                $"Simulation frame progress score {state.SimulationProgressScore.Value:0.00} is below minimum {state.Definition.MinimumSimulationProgressScore:0.00} over {state.SimulationProgressWindowSeconds.Value}s ({state.SimulationFramesAdvanced.Value} frames advanced).",
                state.SimulationProgressScore,
                state.SimulationProgressWindowSeconds,
                state.SimulationFramesAdvanced);
        }

        return new InstanceHealthAssessment(
            DedicatedServerInstanceHealthState.Healthy,
            "Instance healthy.",
            state.SimulationProgressScore,
            state.SimulationProgressWindowSeconds,
            state.SimulationFramesAdvanced);
    }

    private static void SetSimulationBaseline(ManagedInstanceState state, DateTimeOffset observedAtUtc, ulong simulationFrameCounter)
    {
        state.LastSimulationFrameObservedAtUtc = observedAtUtc;
        state.LastSimulationFrameCounter = simulationFrameCounter;
    }

    private static void ResetHealthTracking(ManagedInstanceState state)
    {
        state.HealthState = DedicatedServerInstanceHealthState.Unknown;
        state.HealthSummary = string.Empty;
        state.LastSimulationFrameCounter = null;
        state.LastSimulationFrameObservedAtUtc = null;
        state.SimulationProgressScore = null;
        state.SimulationProgressWindowSeconds = null;
        state.SimulationFramesAdvanced = null;
        state.LastSimulationProgressEvaluatedAtUtc = null;
        state.LastHealthySummary = string.Empty;
    }

    private static bool CanScheduleHealthRestart(ManagedInstanceState state, DateTimeOffset now)
    {
        if (!state.LastHealthRecoveryActionUtc.HasValue)
            return true;

        return (now - state.LastHealthRecoveryActionUtc.Value) >= TimeSpan.FromSeconds(Math.Max(30, state.Definition.RestartDelaySeconds));
    }

    private void NotifyChanged()
    {
        SchedulePersistState();
        Changed?.Invoke();
    }

    private void SchedulePersistState()
    {
        CancellationTokenSource debounce;
        lock (_sync)
        {
            _persistDebounce?.Cancel();
            _persistDebounce?.Dispose();
            _persistDebounce = new CancellationTokenSource();
            debounce = _persistDebounce;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(TimeSpan.FromMilliseconds(150), debounce.Token);
                await PersistStateSnapshotAsync(debounce.Token);
            }
            catch (OperationCanceledException)
            {
            }
        }, CancellationToken.None);
    }

    private async Task PersistStateSnapshotAsync(CancellationToken cancellationToken)
    {
        PersistedSupervisorState persisted;
        lock (_sync)
        {
            persisted = new PersistedSupervisorState
            {
                UpdatedAtUtc = DateTimeOffset.UtcNow,
                Instances = _states.Values
                    .Select(state => new PersistedManagedInstanceState
                    {
                        UniqueName = state.UniqueName,
                        State = state.State,
                        RestartAttempts = state.RestartAttempts,
                        ProcessId = state.ProcessId,
                        LastExitCode = state.LastExitCode,
                        StartedAtUtc = state.StartedAtUtc,
                        StoppedAtUtc = state.StoppedAtUtc,
                        StandardOutputLogPath = state.StandardOutputLogPath,
                        StandardErrorLogPath = state.StandardErrorLogPath,
                        LastHealthRecoveryActionUtc = state.LastHealthRecoveryActionUtc,
                    })
                    .OrderBy(state => state.UniqueName, StringComparer.OrdinalIgnoreCase)
                    .ToList(),
            };
        }

        var json = JsonSerializer.Serialize(persisted, PersistedStateJsonOptions);
        await AtomicFileWriter.WriteTextAsync(MagnetarPaths.GetQuasarSupervisorStatePath(), json, cancellationToken);
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
            UniqueName = definition.UniqueName,
            OriginalUniqueName = definition.OriginalUniqueName,
            GoalState = definition.GoalState,
            ExecutablePath = definition.ExecutablePath,
            WorkingDirectory = definition.WorkingDirectory,
            DedicatedServerAppDataPath = definition.DedicatedServerAppDataPath,
            MagnetarAppDataPath = definition.MagnetarAppDataPath,
            WorldPath = definition.WorldPath,
            ConfigFilePath = definition.ConfigFilePath,
            ConfigProfileId = definition.ConfigProfileId,
            WorldTemplateId = definition.WorldTemplateId,
            LaunchArguments = definition.LaunchArguments,
            AutoStart = definition.AutoStart,
            EnableHealthMonitoring = definition.EnableHealthMonitoring,
            AutoRestartOnUnhealthy = definition.AutoRestartOnUnhealthy,
            AgentStartupGraceSeconds = definition.AgentStartupGraceSeconds,
            AgentHeartbeatTimeoutSeconds = definition.AgentHeartbeatTimeoutSeconds,
            SimulationProgressWindowSeconds = definition.SimulationProgressWindowSeconds,
            MinimumSimulationProgressScore = definition.MinimumSimulationProgressScore,
            WarnAfterUptimeHours = definition.WarnAfterUptimeHours,
            RecycleAfterUptimeHours = definition.RecycleAfterUptimeHours,
            RestartOnCrash = definition.RestartOnCrash,
            RestartDelaySeconds = definition.RestartDelaySeconds,
            MaxRestartAttempts = definition.MaxRestartAttempts,
            UpdatedAtUtc = definition.UpdatedAtUtc,
        };
    }

    private static bool TryAdoptProcess(int processId, out Process process)
    {
        try
        {
            process = Process.GetProcessById(processId);
            if (process.HasExited)
            {
                process.Dispose();
                process = default!;
                return false;
            }

            return true;
        }
        catch
        {
            process = default!;
            return false;
        }
    }

    private static DedicatedServerInstanceRuntimeSnapshot CloneSnapshot(
        ManagedInstanceState state,
        AgentRuntimeState? agent,
        DateTimeOffset now,
        bool disableHealthMonitoring)
    {
        return new DedicatedServerInstanceRuntimeSnapshot
        {
            UniqueName = state.UniqueName,
            GoalState = state.Definition.GoalState,
            State = state.State,
            HealthState = state.HealthState,
            HealthSummary = state.HealthSummary,
            SimulationProgressScore = state.SimulationProgressScore,
            SimulationProgressWindowSeconds = state.SimulationProgressWindowSeconds,
            SimulationFramesAdvanced = state.SimulationFramesAdvanced,
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
        public string UniqueName { get; set; } = string.Empty;

        public DedicatedServerInstanceDefinition Definition { get; set; } = new();

        public DedicatedServerInstanceProcessState State { get; set; } = DedicatedServerInstanceProcessState.Stopped;

        public DedicatedServerInstanceHealthState HealthState { get; set; } = DedicatedServerInstanceHealthState.Unknown;

        public string HealthSummary { get; set; } = string.Empty;

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

        public ulong? LastSimulationFrameCounter { get; set; }

        public DateTimeOffset? LastSimulationFrameObservedAtUtc { get; set; }

        public float? SimulationProgressScore { get; set; }

        public int? SimulationProgressWindowSeconds { get; set; }

        public ulong? SimulationFramesAdvanced { get; set; }

        public DateTimeOffset? LastSimulationProgressEvaluatedAtUtc { get; set; }

        public string LastHealthySummary { get; set; } = string.Empty;
    }

    private enum ReconcileAction
    {
        Start = 0,
        Stop = 1,
        Restart = 2,
    }

    private readonly record struct InstanceHealthAssessment(
        DedicatedServerInstanceHealthState State,
        string Summary,
        float? SimulationProgressScore = null,
        int? SimulationProgressWindowSeconds = null,
        ulong? SimulationFramesAdvanced = null);

    private sealed class PersistedSupervisorState
    {
        public DateTimeOffset UpdatedAtUtc { get; set; }

        public List<PersistedManagedInstanceState> Instances { get; set; } = [];
    }

    private sealed class PersistedManagedInstanceState
    {
        public string UniqueName { get; set; } = string.Empty;

        public DedicatedServerInstanceProcessState State { get; set; } = DedicatedServerInstanceProcessState.Stopped;

        public int RestartAttempts { get; set; }

        public int? ProcessId { get; set; }

        public int? LastExitCode { get; set; }

        public DateTimeOffset? StartedAtUtc { get; set; }

        public DateTimeOffset? StoppedAtUtc { get; set; }

        public string? StandardOutputLogPath { get; set; }

        public string? StandardErrorLogPath { get; set; }

        public DateTimeOffset? LastHealthRecoveryActionUtc { get; set; }
    }
}
