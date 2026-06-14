using System.Diagnostics;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Magnetar.Protocol.Runtime;
using Magnetar.Protocol.Transport;
using Quasar.Models;
using Quasar.Services.Backup;
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
    private static readonly TimeSpan DefaultGracefulStopTimeout = TimeSpan.FromSeconds(30);
    private const string StandardOutputLogName = "stdout";
    private const string StandardErrorLogName = "stderr";
    private const string ActiveLogExtension = ".log";
    private const string ReniceHelperPath = "/usr/local/bin/quasar-renice";
    private const string RestoreInProgressMessage = "Start deferred: a backup restore is in progress for this server.";
    private const int MaxModDownloadFailures = 20;
    private static readonly Regex PrefixedLogLinePattern = new(
        @"^(?<timestamp>\d{4}-\d{2}-\d{2} \d{2}:\d{2}:\d{2}(?:\.\d{1,7})?)\s*[:\-]\s*(?<message>.*)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);
    private readonly object _sync = new();
    private readonly DedicatedServerCatalog _catalog;
    private readonly AgentRegistry _registry;
    private readonly DedicatedServerRuntimePreparer _runtimePreparer;
    private readonly ManagedDedicatedServerRuntimeResolver _runtimeResolver;
    private readonly ManagedRuntimeWarmupService _runtimeWarmup;
    private readonly WebServiceOptions _options;
    private readonly ILogger<DedicatedServerSupervisor> _logger;
    private readonly PluginLogStream _pluginLogStream;
    private readonly ServerRestoreCoordinator _restoreCoordinator;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly Dictionary<string, ManagedServerState> _states = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource? _persistDebounce;
    private bool _isStopping;
    private bool _preserveManagedServersOnShutdown;

    public DedicatedServerSupervisor(
        DedicatedServerCatalog catalog,
        AgentRegistry registry,
        DedicatedServerRuntimePreparer runtimePreparer,
        ManagedDedicatedServerRuntimeResolver runtimeResolver,
        ManagedRuntimeWarmupService runtimeWarmup,
        WebServiceOptions options,
        PluginLogStream pluginLogStream,
        ServerRestoreCoordinator restoreCoordinator,
        ILogger<DedicatedServerSupervisor> logger)
    {
        _catalog = catalog;
        _registry = registry;
        _runtimePreparer = runtimePreparer;
        _runtimeResolver = runtimeResolver;
        _runtimeWarmup = runtimeWarmup;
        _options = options;
        _pluginLogStream = pluginLogStream;
        _restoreCoordinator = restoreCoordinator;
        _logger = logger;

        // When set (the default), Quasar leaves managed servers running on its own
        // shutdown instead of stopping them — they are detached (Magnetar -daemon /
        // setsid) and reconnect when Quasar comes back. Without this wiring the field
        // stayed false, so StopAsync stopped every server on a normal Ctrl-C.
        _preserveManagedServersOnShutdown = options.PreserveManagedServersOnShutdown;
    }

    public event Action? Changed;

    /// <summary>
    /// True when health monitoring is disabled instance-wide (development mode or
    /// configuration). The Dashboard surfaces this once at the top rather than on
    /// every server card.
    /// </summary>
    public bool HealthMonitoringDisabled => _options.DisableServerHealthMonitoring;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        SyncDefinitions();
        RestorePersistedRuntimeState();
        _catalog.Changed += HandleCatalogChanged;
        _runtimeWarmup.Changed += HandleRuntimeWarmupChanged;
        _ = Task.Run(() => ReconcileLoopAsync(_shutdown.Token), _shutdown.Token);
        SchedulePersistState();
        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        _isStopping = true;
        _shutdown.Cancel();
        _catalog.Changed -= HandleCatalogChanged;
        _runtimeWarmup.Changed -= HandleRuntimeWarmupChanged;

        if (_preserveManagedServersOnShutdown)
        {
            await PersistStateSnapshotAsync(CancellationToken.None);
            return;
        }

        var runningUniqueNames = GetSnapshots()
            .Where(snapshot => snapshot.State is DedicatedServerProcessState.Starting
                or DedicatedServerProcessState.Running
                or DedicatedServerProcessState.Restarting
                or DedicatedServerProcessState.Stopping)
            .Select(snapshot => snapshot.UniqueName)
            .ToList();

        foreach (var uniqueName in runningUniqueNames)
        {
            try
            {
                await StopServerAsync(uniqueName, forceAfter: null, CancellationToken.None);
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed stopping server {UniqueName} during Quasar shutdown.", uniqueName);
            }
        }

        await PersistStateSnapshotAsync(CancellationToken.None);
    }

    public IReadOnlyList<DedicatedServerRuntimeSnapshot> GetSnapshots()
    {
        List<DedicatedServerRuntimeSnapshot> snapshots;
        var agents = BuildAgentLookup();
        var now = DateTimeOffset.UtcNow;

        lock (_sync)
        {
            snapshots = _states.Values
                .Select(state => CloneSnapshot(
                    state,
                    agents.TryGetValue(state.UniqueName, out var agent) ? agent : null,
                    now,
                    _options.DisableServerHealthMonitoring))
                .OrderBy(snapshot => snapshot.UniqueName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(snapshot => snapshot.UniqueName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        return snapshots;
    }

    public Task SetGoalStateAsync(
        string uniqueName,
        DedicatedServerGoalState goalState,
        CancellationToken cancellationToken = default) =>
        SetGoalStateAsync(uniqueName, goalState, reconcile: true, cancellationToken);

    /// <summary>
    /// Persists the desired <paramref name="goalState"/> for a server. When
    /// <paramref name="reconcile"/> is <c>true</c> the supervisor immediately
    /// reconciles to enforce it. Callers that drive their own graceful stop
    /// (e.g. shutting down every server at once) can pass <c>false</c> to record
    /// the new intent without kicking off a competing reconcile-driven stop.
    /// </summary>
    public async Task SetGoalStateAsync(
        string uniqueName,
        DedicatedServerGoalState goalState,
        bool reconcile,
        CancellationToken cancellationToken = default)
    {
        await _catalog.SetGoalStateAsync(uniqueName, goalState, cancellationToken);
        if (reconcile)
            await ReconcileAsync(cancellationToken);
    }

    /// <summary>
    /// True when the server's process is running or transitioning (starting,
    /// running, restarting or stopping). Used to refuse a restore that would
    /// rewrite the files of a live server.
    /// </summary>
    public bool IsServerProcessActive(string uniqueName)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
            return false;

        lock (_sync)
        {
            if (!_states.TryGetValue(uniqueName, out var state))
                return false;

            return IsProcessActive(state.Process)
                || state.State is DedicatedServerProcessState.Starting
                    or DedicatedServerProcessState.Running
                    or DedicatedServerProcessState.Restarting
                    or DedicatedServerProcessState.Stopping;
        }
    }

    public async Task StartServerAsync(string uniqueName, CancellationToken cancellationToken = default)
    {
        ManagedServerState? state;
        CancellationTokenSource? startCancellation = null;
        var restoreDeferred = false;
        var notifyDeferred = false;

        lock (_sync)
        {
            if (!_states.TryGetValue(uniqueName, out state))
                throw new InvalidOperationException($"Unknown Quasar server '{uniqueName}'.");

            if (IsProcessActive(state.Process))
                return;

            // A start is already running for this server (its process handle is
            // assigned only late in StartProcessAsync). Without this guard two
            // overlapping reconciles — the periodic loop and a catalog-change
            // reconcile kicked off via Task.Run — both pass the IsProcessActive
            // check and launch duplicate processes that then collide on the port.
            if (state.StartInProgress)
                return;

            // A restore rewrites this server's files in place; launching now would
            // race the file copy and could load a half-restored world. Claiming the
            // start (StartInProgress / State below) happens in the SAME locked block
            // as this check, while a restore claims its slot before re-checking that
            // the server is inactive — so a concurrent restore and start can never
            // both proceed. The reconcile loop starts the server once the restore
            // finishes, if the goal state is still on.
            if (_restoreCoordinator.IsRestoreInProgress(uniqueName))
            {
                restoreDeferred = true;
                if (state.LastMessage != RestoreInProgressMessage)
                {
                    state.LastMessage = RestoreInProgressMessage;
                    notifyDeferred = true;
                }
            }
            else
            {
                state.StartCancellation?.Cancel();
                state.StartCancellation?.Dispose();
                startCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _shutdown.Token);
                state.StartCancellation = startCancellation;
                state.StartInProgress = true;
                state.StopRequested = false;
                state.State = state.IsRestartPending
                    ? DedicatedServerProcessState.Restarting
                    : DedicatedServerProcessState.Starting;
                state.LastMessage = "Starting process.";
                if (!state.IsRestartPending)
                {
                    state.RestartAttempts = 0;
                    state.AgentAttachRetryAttempts = 0;
                }
            }
        }

        if (restoreDeferred)
        {
            if (notifyDeferred)
                NotifyChanged();
            return;
        }

        NotifyChanged();
        try
        {
            await StartProcessAsync(state, startCancellation?.Token ?? cancellationToken);
        }
        catch (OperationCanceledException) when (startCancellation?.IsCancellationRequested == true)
        {
            SetStopped(uniqueName, "Start cancelled.");
        }
        finally
        {
            lock (_sync)
            {
                state.StartInProgress = false;
                if (ReferenceEquals(state.StartCancellation, startCancellation))
                    state.StartCancellation = null;
            }

            startCancellation?.Dispose();
        }
    }

    public Task StopServerAsync(string uniqueName, CancellationToken cancellationToken = default) =>
        StopServerAsync(uniqueName, DefaultGracefulStopTimeout, cancellationToken);

    public async Task StopServerAsync(string uniqueName, TimeSpan? forceAfter, CancellationToken cancellationToken = default)
    {
        ManagedServerState? state;
        Process? process;

        lock (_sync)
        {
            if (!_states.TryGetValue(uniqueName, out state))
                return;

            process = state.Process;
            state.StopRequested = true;
            state.IsRestartPending = false;
            state.StartCancellation?.Cancel();

            if (!IsProcessActive(process))
            {
                if (state.StartInProgress)
                {
                    state.State = DedicatedServerProcessState.Stopping;
                    state.LastMessage = "Cancelling start.";
                    NotifyChanged();
                    return;
                }

                state.State = DedicatedServerProcessState.Stopped;
                state.LastMessage = "Already stopped.";
                NotifyChanged();
                return;
            }

            state.State = DedicatedServerProcessState.Stopping;
            state.LastMessage = "Stopping process.";
        }

        NotifyChanged();

        await TryRequestGracefulStopAsync(uniqueName, cancellationToken);

        if (process is null)
            return;

        try
        {
            if (forceAfter is null)
            {
                await process.WaitForExitAsync(cancellationToken);
            }
            else
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeout.CancelAfter(forceAfter.Value);
                await process.WaitForExitAsync(timeout.Token);
            }
        }
        catch (OperationCanceledException) when (forceAfter is not null && !cancellationToken.IsCancellationRequested)
        {
            if (IsProcessActive(process))
            {
                _logger.LogWarning("Server {UniqueName} did not stop gracefully. Killing process tree.", uniqueName);
                try
                {
                    process.Kill(entireProcessTree: true);
                    await process.WaitForExitAsync(CancellationToken.None);
                }
                catch (InvalidOperationException)
                {
                    // The process exited on its own between the active check and the
                    // kill; nothing left to stop.
                }
            }
        }
        catch (InvalidOperationException)
        {
            // "No process is associated with this object." — the process already
            // exited and was disposed while we were waiting on it. That is the
            // normal outcome of a stop, not an error worth surfacing.
        }

        if (!IsProcessActive(process))
            await HandleProcessExitedAsync(uniqueName);
    }

    public async Task KillStartingServerAsync(string uniqueName, CancellationToken cancellationToken = default)
    {
        ManagedServerState? state;
        Process? process;
        var waitForStartCancellation = false;

        lock (_sync)
        {
            if (!_states.TryGetValue(uniqueName, out state))
                return;

            if (state.State is not (DedicatedServerProcessState.Starting or DedicatedServerProcessState.Restarting))
                return;

            process = state.Process;
            waitForStartCancellation = state.StartInProgress && !IsProcessActive(process);
            state.StopRequested = true;
            state.IsRestartPending = false;
            state.StartCancellation?.Cancel();
            state.State = DedicatedServerProcessState.Stopping;
            state.LastMessage = IsProcessActive(process)
                ? "Killing starting process."
                : "Cancelling start.";
        }

        NotifyChanged();
        await _catalog.SetGoalStateAsync(uniqueName, DedicatedServerGoalState.Off, cancellationToken);

        if (IsProcessActive(process))
        {
            try
            {
                process!.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // The process exited between the active check and kill/wait.
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Failed killing starting server {UniqueName}.", uniqueName);
                throw;
            }

            if (!IsProcessActive(process))
                await HandleProcessExitedAsync(uniqueName);
            return;
        }

        if (!waitForStartCancellation)
            SetStopped(uniqueName, "Start cancelled.");
    }

    public async Task RestartServerAsync(string uniqueName, CancellationToken cancellationToken = default)
    {
        var definition = _catalog.GetServer(uniqueName);
        if (definition is null)
            throw new InvalidOperationException($"Unknown Quasar server '{uniqueName}'.");

        definition.GoalState = DedicatedServerGoalState.On;
        definition.AutoStart = true;
        await _catalog.UpsertAsync(definition, cancellationToken);
        await StopServerAsync(uniqueName, cancellationToken);
        await StartServerAsync(uniqueName, cancellationToken);
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
        _preserveManagedServersOnShutdown = true;
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
        List<(string UniqueName, DedicatedServerProcessPriority Priority, string Phase)> priorityActions = new();
        List<(string UniqueName, string Affinity, string Phase)> affinityActions = new();
        var agents = BuildAgentLookup();
        var now = DateTimeOffset.UtcNow;
        var healthChanged = false;

        lock (_sync)
        {
            foreach (var state in _states.Values)
            {
                var processActive = IsProcessActive(state.Process);
                var goalState = state.Definition.GoalState;
                var agent = agents.TryGetValue(state.UniqueName, out var currentAgent) ? currentAgent : null;
                var health = EvaluateHealth(
                    state,
                    agent,
                    now,
                    _options.DisableServerHealthMonitoring);

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

                if (processActive &&
                    state.State is DedicatedServerProcessState.Starting or DedicatedServerProcessState.Restarting &&
                    agent?.IsConnected == true &&
                    agent.Snapshot?.IsRunning == true)
                {
                    state.State = DedicatedServerProcessState.Running;
                    state.LastMessage = "Server online.";
                    state.AgentAttachRetryAttempts = 0;
                    healthChanged = true;
                }

                if (processActive &&
                    state.State == DedicatedServerProcessState.Running &&
                    health.State is DedicatedServerHealthState.Healthy or DedicatedServerHealthState.Warning &&
                    agent?.IsConnected == true &&
                    agent.Snapshot is not null)
                {
                    if (state.LastAppliedProcessPriority != state.Definition.ReadyProcessPriority &&
                        state.LastFailedProcessPriority != state.Definition.ReadyProcessPriority)
                        priorityActions.Add((state.UniqueName, state.Definition.ReadyProcessPriority, "ready"));

                    // Re-pin threads spawned during world load and pick up live config edits
                    // (a saved change kicks a reconcile via HandleCatalogChanged), all without
                    // restarting the process.
                    if (state.LastAppliedCpuAffinity != state.Definition.CpuAffinity &&
                        state.LastFailedCpuAffinity != state.Definition.CpuAffinity)
                        affinityActions.Add((state.UniqueName, state.Definition.CpuAffinity, "ready"));
                }

                if (goalState == DedicatedServerGoalState.On)
                {
                    if (!processActive && state.State == DedicatedServerProcessState.Stopped)
                    {
                        actions.Add((state.UniqueName, ReconcileAction.Start, "goal state is on"));
                    }
                    else if (processActive &&
                             health.State == DedicatedServerHealthState.Unhealthy &&
                             state.Definition.AutoRestartOnUnhealthy &&
                             state.State == DedicatedServerProcessState.Starting)
                    {
                        actions.Add((state.UniqueName, ReconcileAction.RetryAttach, health.Summary));
                    }
                    else if (processActive &&
                             health.State == DedicatedServerHealthState.Unhealthy &&
                             state.Definition.AutoRestartOnUnhealthy &&
                             state.State == DedicatedServerProcessState.Running &&
                             CanScheduleHealthRestart(state, now))
                    {
                        state.LastHealthRecoveryActionUtc = now;
                        actions.Add((state.UniqueName, ReconcileAction.Restart, health.Summary));
                    }
                    else if (processActive &&
                             state.State == DedicatedServerProcessState.Running &&
                             ShouldMaximumUptimeRestartFire(state, now))
                    {
                        if (CanRunPlannedRestart(state))
                        {
                            actions.Add((state.UniqueName, ReconcileAction.Restart, $"maximum uptime {state.Definition.MaximumUptime} reached"));
                        }
                        else
                        {
                            state.LastMessage = "Maximum uptime restart delayed; another server is stopping or restarting.";
                            healthChanged = true;
                        }
                    }
                    else if (processActive &&
                             state.State == DedicatedServerProcessState.Running &&
                             ShouldScheduledRestartFire(state, now, consume: CanRunPlannedRestart(state)))
                    {
                        if (state.LastScheduledRestartUtc == now)
                        {
                            actions.Add((state.UniqueName, ReconcileAction.Restart, $"scheduled restart at {state.Definition.DailyRestartTimeLocal}"));
                        }
                        else
                        {
                            state.LastMessage = "Scheduled restart delayed; another server is stopping or restarting.";
                            healthChanged = true;
                        }
                    }
                }
                else
                {
                    if (processActive && state.State != DedicatedServerProcessState.Stopping)
                        actions.Add((state.UniqueName, ReconcileAction.Stop, "goal state is off"));
                }
            }
        }

        if (healthChanged)
            NotifyChanged();

        foreach (var (uniqueName, priority, phase) in priorityActions)
            TryApplyProcessPriority(uniqueName, priority, phase);

        foreach (var (uniqueName, affinity, phase) in affinityActions)
            TryApplyCpuAffinity(uniqueName, affinity, phase);

        foreach (var (uniqueName, action, reason) in actions)
        {
            if (cancellationToken.IsCancellationRequested)
                break;

            switch (action)
            {
                case ReconcileAction.Start:
                    await StartServerAsync(uniqueName, cancellationToken);
                    break;

                case ReconcileAction.Stop:
                    await StopServerAsync(uniqueName, cancellationToken);
                    break;

                case ReconcileAction.Restart:
                    _logger.LogWarning("Quasar health recovery restarting server {UniqueName}: {Reason}", uniqueName, reason);
                    await RestartServerAsync(uniqueName, cancellationToken);
                    break;

                case ReconcileAction.RetryAttach:
                    await RetryAgentAttachAsync(uniqueName, reason, cancellationToken);
                    break;
            }
        }
    }

    private async Task RetryAgentAttachAsync(string uniqueName, string reason, CancellationToken cancellationToken)
    {
        ManagedServerState? state;
        DedicatedServerDefinition definition;
        Process? process;
        int attempt;
        int maxAttempts;
        int retryDelaySeconds;
        var faulted = false;

        lock (_sync)
        {
            if (!_states.TryGetValue(uniqueName, out state))
                return;

            process = state.Process;
            if (state.State != DedicatedServerProcessState.Starting || !IsProcessActive(process))
                return;

            definition = Clone(state.Definition);
            maxAttempts = EffectiveAgentAttachRetryAttempts(definition);
            retryDelaySeconds = Math.Max(0, definition.AgentAttachRetryDelaySeconds);
            attempt = state.AgentAttachRetryAttempts + 1;
            state.AgentAttachRetryAttempts = Math.Min(attempt, maxAttempts);
            state.StopRequested = true;
            state.IsRestartPending = false;
            state.State = DedicatedServerProcessState.Stopping;

            if (attempt > maxAttempts)
            {
                faulted = true;
                state.LastMessage = $"Quasar.Agent did not attach after {maxAttempts} attempt(s). Faulting.";
            }
            else
            {
                state.LastMessage = $"Quasar.Agent attach timed out ({attempt}/{maxAttempts}). Retrying in {retryDelaySeconds}s.";
            }
        }

        NotifyChanged();

        if (IsProcessActive(process))
        {
            try
            {
                _logger.LogWarning(
                    "Quasar.Agent attach timed out for server {UniqueName}. Killing starting process. Attempt {Attempt}/{MaxAttempts}. Reason: {Reason}",
                    uniqueName,
                    Math.Min(attempt, maxAttempts),
                    maxAttempts,
                    reason);
                process!.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(cancellationToken);
            }
            catch (InvalidOperationException)
            {
                // Process exited between active check and kill/wait.
            }
        }

        if (!IsProcessActive(process))
            await HandleProcessExitedAsync(uniqueName);

        if (faulted)
        {
            lock (_sync)
            {
                if (_states.TryGetValue(uniqueName, out state))
                {
                    state.Process = null;
                    state.ProcessId = null;
                    state.State = DedicatedServerProcessState.Faulted;
                    state.StopRequested = false;
                    state.IsRestartPending = false;
                    state.StartedAtUtc = null;
                    state.StoppedAtUtc = DateTimeOffset.UtcNow;
                    state.LastMessage = $"Quasar.Agent did not attach after {maxAttempts} attempt(s). {reason}";
                    ResetHealthTracking(state);
                }
            }

            NotifyChanged();
            return;
        }

        lock (_sync)
        {
            if (!_states.TryGetValue(uniqueName, out state) ||
                state.Definition.GoalState != DedicatedServerGoalState.On ||
                state.StopRequested && state.State != DedicatedServerProcessState.Stopped)
            {
                return;
            }

            state.State = DedicatedServerProcessState.Restarting;
            state.IsRestartPending = true;
            state.StopRequested = false;
            state.LastMessage = $"Retrying Quasar.Agent attach in {retryDelaySeconds}s.";
        }

        NotifyChanged();

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(retryDelaySeconds), cancellationToken);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        lock (_sync)
        {
            if (!_states.TryGetValue(uniqueName, out state) ||
                state.Definition.GoalState != DedicatedServerGoalState.On ||
                state.StopRequested ||
                state.State != DedicatedServerProcessState.Restarting)
            {
                return;
            }
        }

        await StartServerAsync(uniqueName, cancellationToken);
    }

    private async Task StartProcessAsync(ManagedServerState state, CancellationToken cancellationToken)
    {
        var definition = state.Definition;
        if (string.IsNullOrWhiteSpace(definition.WorldTemplateId))
        {
            SetFaulted(state.UniqueName, "World template required.");
            return;
        }

        if (!_runtimeWarmup.IsReady)
        {
            SetStopped(state.UniqueName, _runtimeWarmup.BlockLaunchMessage);
            return;
        }

        ResolvedDedicatedServerRuntime runtime;
        SetRuntimeMessage(state.UniqueName, "Resolving managed runtime.");
        try
        {
            runtime = await _runtimeResolver.ResolveAsync(definition, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetFaulted(state.UniqueName, exception.Message);
            _logger.LogWarning(exception, "Failed resolving managed runtime for server {UniqueName}.", state.UniqueName);
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();

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
        SetRuntimeMessage(state.UniqueName, "Preparing dedicated server runtime.");
        try
        {
            launch = await _runtimePreparer.PrepareAsync(definition, runtime.DedicatedServer64Path, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            SetFaulted(state.UniqueName, exception.Message);
            _logger.LogWarning(exception, "Failed preparing runtime files for server {UniqueName}.", state.UniqueName);
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();

        string stdoutPath;
        string stderrPath;
        try
        {
            (stdoutPath, stderrPath) = PrepareServerLogSlot(state.UniqueName, definition.DsLogFilesToKeep);
        }
        catch (Exception exception)
        {
            SetFaulted(state.UniqueName, $"Failed preparing DS log files: {exception.Message}");
            _logger.LogWarning(exception, "Failed preparing DS log files for server {UniqueName}.", state.UniqueName);
            return;
        }
        cancellationToken.ThrowIfCancellationRequested();

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
        process.StartInfo.Environment["MAGNETAR_HOST_ID"] = _options.HostId;
        process.StartInfo.Environment["QUASAR_DS_APPDATA_PATH"] = launch.DedicatedServerAppDataPath;
        process.StartInfo.Environment["QUASAR_MAGNETAR_APPDATA_PATH"] = launch.MagnetarAppDataPath;
        process.StartInfo.Environment["QUASAR_DS64_PATH"] = launch.DedicatedServer64Path;
        process.StartInfo.Environment["QUASAR_WORLD_PATH"] = launch.WorldPath;
        process.StartInfo.Environment["QUASAR_DS_CONFIG_PATH"] = launch.RuntimeConfigPath;
        process.StartInfo.Environment["QUASAR_LAST_SESSION_PATH"] = launch.LastSessionPath;
        ConfigureNativeLibrarySearchPath(process.StartInfo, runtime.NativeLibrarySearchPaths);

        // How the agent should behave when it loses contact with Quasar: keep the
        // server running and reconnect, and only save+stop after the configured
        // offline window (zero/negative = stop promptly once Quasar is gone).
        process.StartInfo.Environment["QUASAR_AGENT_OFFLINE_SHUTDOWN_SECONDS"] =
            _options.AgentOfflineShutdownSeconds.ToString(CultureInfo.InvariantCulture);
        process.StartInfo.Environment["QUASAR_AGENT_RECONNECT_INTERVAL_SECONDS"] =
            _options.AgentReconnectIntervalSeconds.ToString(CultureInfo.InvariantCulture);
        process.StartInfo.Environment["QUASAR_AGENT_RECONNECT_JITTER_SECONDS"] =
            _options.AgentReconnectJitterSeconds.ToString(CultureInfo.InvariantCulture);
        process.StartInfo.Environment["QUASAR_AGENT_PROFILER_MODE"] =
            DedicatedServerCatalog.NormalizeProfilerMode(string.IsNullOrWhiteSpace(definition.AgentProfilerMode)
                ? _options.AgentProfilerMode
                : definition.AgentProfilerMode);

        // Activate the PluginSdk QuasarLogSink inside the dedicated server: any
        // non-empty QUASAR_AGENT value makes plugins emit structured JSON log
        // lines on standard output (LogEnvironment.IsManagedByQuasar), which the
        // supervisor parses into the plugin log stream below.
        process.StartInfo.Environment["QUASAR_AGENT"] = definition.UniqueName;

        process.Exited += async (_, _) => await HandleProcessExitedAsync(state.UniqueName);
        LogManagedServerLaunchEnvironment(definition, process.StartInfo);

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

        var discardStartedProcess = false;
        lock (_sync)
        {
            if (!_states.TryGetValue(state.UniqueName, out var current))
            {
                discardStartedProcess = true;
            }
            else if (current.StopRequested || cancellationToken.IsCancellationRequested)
            {
                current.State = DedicatedServerProcessState.Stopping;
                current.LastMessage = "Killing cancelled start.";
                current.IsRestartPending = false;
                discardStartedProcess = true;
            }
            else
            {
                current.Process = process;
                current.State = DedicatedServerProcessState.Starting;
                current.ProcessId = process.Id;
                current.StartedAtUtc = DateTimeOffset.UtcNow;
                current.AgentWatchSinceUtc = current.StartedAtUtc;
                current.StoppedAtUtc = null;
                current.LastExitCode = null;
                current.LastMessage = "Process started; waiting for server online signal.";
                current.StandardOutputLogPath = stdoutPath;
                current.StandardErrorLogPath = stderrPath;
                current.IsRestartPending = false;
                current.StopRequested = false;
                current.ModDownloadFailures.Clear();
                current.LastAppliedProcessPriority = null;
                current.LastFailedProcessPriority = null;
                current.LastAppliedCpuAffinity = null;
                current.LastFailedCpuAffinity = null;
                ResetHealthTracking(current);
            }
        }

        if (discardStartedProcess)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None);
            }
            catch (InvalidOperationException)
            {
            }

            process.Dispose();
            SetStopped(state.UniqueName, "Start cancelled.");
            return;
        }

        _logger.LogInformation("Started server {UniqueName} with pid {Pid}.", state.UniqueName, process.Id);
        TryApplyProcessPriority(state.UniqueName, process, definition.StartupProcessPriority, "startup");
        TryApplyCpuAffinity(state.UniqueName, definition.CpuAffinity, "startup");
        NotifyChanged();

        _ = PumpStandardOutputAsync(process.StandardOutput, stdoutPath, state.UniqueName, _shutdown.Token);
        _ = PumpStandardErrorAsync(process.StandardError, stderrPath, state.UniqueName, _shutdown.Token);
    }

    private static void ConfigureNativeLibrarySearchPath(
        ProcessStartInfo startInfo,
        IReadOnlyList<string> nativeLibrarySearchPaths)
    {
        if (!OperatingSystem.IsLinux() || nativeLibrarySearchPaths.Count == 0)
            return;

        const string variableName = "LD_LIBRARY_PATH";
        var paths = nativeLibrarySearchPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .ToList();
        if (paths.Count == 0)
            return;

        if (startInfo.Environment.TryGetValue(variableName, out var existingValue) &&
            !string.IsNullOrWhiteSpace(existingValue))
        {
            paths.AddRange(existingValue.Split(
                Path.PathSeparator,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        }

        startInfo.Environment[variableName] = string.Join(
            Path.PathSeparator,
            paths.Distinct(StringComparer.Ordinal));
    }

    private void LogManagedServerLaunchEnvironment(DedicatedServerDefinition definition, ProcessStartInfo startInfo)
    {
        if (!definition.LogLaunchEnvironment)
            return;

        var environment = string.Join(
            Environment.NewLine,
            startInfo.Environment
                .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase)
                .Select(pair => $"{pair.Key}={pair.Value}"));

        var launchEnvironment = string.Join(
            Environment.NewLine,
            $"Server={definition.UniqueName}",
            $"FileName={startInfo.FileName}",
            $"Arguments={startInfo.Arguments}",
            $"WorkingDirectory={startInfo.WorkingDirectory}",
            "Environment:",
            environment);

        _logger.LogWarning(
            "Managed server launch environment logging is enabled. These logs may contain secrets.{NewLine}{LaunchEnvironment}",
            Environment.NewLine,
            launchEnvironment);
    }

    private void SetRuntimeMessage(string uniqueName, string message)
    {
        lock (_sync)
        {
            if (!_states.TryGetValue(uniqueName, out var state))
                return;

            state.LastMessage = message;
        }

        NotifyChanged();
    }

    private void TryApplyProcessPriority(string uniqueName, DedicatedServerProcessPriority priority, string phase)
    {
        Process? process;
        lock (_sync)
        {
            if (!_states.TryGetValue(uniqueName, out var state))
                return;

            if (state.LastAppliedProcessPriority == priority)
                return;

            if (state.LastFailedProcessPriority == priority)
                return;

            process = state.Process;
            if (!IsProcessActive(process))
                return;
        }

        if (!TryApplyProcessPriority(uniqueName, process!, priority, phase))
        {
            lock (_sync)
            {
                if (_states.TryGetValue(uniqueName, out var state))
                    state.LastFailedProcessPriority = priority;
            }
            return;
        }

        lock (_sync)
        {
            if (_states.TryGetValue(uniqueName, out var state))
            {
                state.LastAppliedProcessPriority = priority;
                state.LastFailedProcessPriority = null;
            }
        }
    }

    private bool TryApplyProcessPriority(string uniqueName, Process process, DedicatedServerProcessPriority priority, string phase)
    {
        try
        {
            if (OperatingSystem.IsWindows())
            {
                process.PriorityClass = ToWindowsPriority(priority);
                _logger.LogInformation("Applied {Phase} priority {Priority} to server {UniqueName}.", phase, priority, uniqueName);
                return true;
            }

            if (OperatingSystem.IsLinux() || OperatingSystem.IsMacOS())
            {
                return TryApplyUnixNice(uniqueName, process.Id, priority, phase);
            }
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed applying {Phase} priority {Priority} to server {UniqueName}.", phase, priority, uniqueName);
        }

        return false;
    }

    private static ProcessPriorityClass ToWindowsPriority(DedicatedServerProcessPriority priority) => priority switch
    {
        DedicatedServerProcessPriority.Low => ProcessPriorityClass.Idle,
        DedicatedServerProcessPriority.BelowNormal => ProcessPriorityClass.BelowNormal,
        DedicatedServerProcessPriority.AboveNormal => ProcessPriorityClass.AboveNormal,
        DedicatedServerProcessPriority.High => ProcessPriorityClass.High,
        _ => ProcessPriorityClass.Normal,
    };

    private bool TryApplyUnixNice(string uniqueName, int processId, DedicatedServerProcessPriority priority, string phase)
    {
        var nice = priority switch
        {
            DedicatedServerProcessPriority.Low => 10,
            DedicatedServerProcessPriority.BelowNormal => 5,
            DedicatedServerProcessPriority.AboveNormal => -5,
            DedicatedServerProcessPriority.High => -10,
            _ => 0,
        };

        try
        {
            var useHelper = File.Exists(ReniceHelperPath);
            using var renice = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = useHelper ? ReniceHelperPath : "renice",
                    Arguments = useHelper
                        ? $"{nice} {processId}"
                        : $"-n {nice} -p {processId}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };

            if (!renice.Start())
                return false;

            var stdout = renice.StandardOutput.ReadToEnd();
            var stderr = renice.StandardError.ReadToEnd();
            renice.WaitForExit(3000);
            if (renice.ExitCode == 0)
            {
                _logger.LogInformation(
                    "Applied {Phase} nice {Nice} to server {UniqueName} using {Tool}.",
                    phase,
                    nice,
                    uniqueName,
                    useHelper ? ReniceHelperPath : "renice");
                return true;
            }

            _logger.LogWarning(
                "{Tool} failed applying {Phase} nice {Nice} to server {UniqueName}. ExitCode={ExitCode}. Stdout={Stdout}. Stderr={Stderr}",
                useHelper ? ReniceHelperPath : "renice",
                phase,
                nice,
                uniqueName,
                renice.ExitCode,
                TrimForLog(stdout),
                TrimForLog(stderr));

            if (!useHelper && nice < 0)
                SetRuntimeMessage(uniqueName, $"Install {ReniceHelperPath} to use {priority} process priority on Linux.");
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed applying Unix nice for server {UniqueName}.", uniqueName);
        }

        return false;
    }

    private void TryApplyCpuAffinity(string uniqueName, string affinity, string phase)
    {
        var normalized = affinity?.Trim() ?? string.Empty;
        Process? process;
        string? previousApplied;
        lock (_sync)
        {
            if (!_states.TryGetValue(uniqueName, out var state))
                return;

            if (state.LastAppliedCpuAffinity == normalized)
                return;

            if (state.LastFailedCpuAffinity == normalized)
                return;

            process = state.Process;
            if (!IsProcessActive(process))
                return;

            previousApplied = state.LastAppliedCpuAffinity;
        }

        if (!TryApplyCpuAffinity(uniqueName, process!, normalized, previousApplied, phase))
        {
            lock (_sync)
            {
                if (_states.TryGetValue(uniqueName, out var state))
                    state.LastFailedCpuAffinity = normalized;
            }
            return;
        }

        lock (_sync)
        {
            if (_states.TryGetValue(uniqueName, out var state))
            {
                state.LastAppliedCpuAffinity = normalized;
                state.LastFailedCpuAffinity = null;
            }
        }
    }

    private bool TryApplyCpuAffinity(string uniqueName, Process process, string affinity, string? previousApplied, string phase)
    {
        try
        {
            if (affinity.Length == 0)
            {
                // No affinity requested. Release a previously pinned process back to all cores;
                // if it was never pinned there is nothing to do.
                if (string.IsNullOrEmpty(previousApplied))
                    return true;

                var allCores = Enumerable.Range(0, Environment.ProcessorCount).ToArray();
                return ApplyCpuAffinityCores(uniqueName, process, allCores, $"{phase} (clear)");
            }

            if (!CpuAffinitySpec.TryParse(affinity, Environment.ProcessorCount, out var cores, out var error))
            {
                _logger.LogWarning("Invalid CPU affinity '{Affinity}' for server {UniqueName}: {Error}", affinity, uniqueName, error);
                return false;
            }

            return ApplyCpuAffinityCores(uniqueName, process, cores, phase);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed applying {Phase} CPU affinity {Affinity} to server {UniqueName}.", phase, affinity, uniqueName);
            return false;
        }
    }

    private bool ApplyCpuAffinityCores(string uniqueName, Process process, IReadOnlyList<int> cores, string phase)
    {
        if (OperatingSystem.IsWindows())
        {
            var mask = CpuAffinitySpec.ToWindowsMask(cores);
            if (mask == 0)
            {
                _logger.LogWarning("Computed empty CPU affinity mask for server {UniqueName}; skipping.", uniqueName);
                return false;
            }

            process.ProcessorAffinity = (nint)mask;
            _logger.LogInformation("Applied {Phase} CPU affinity {Cores} to server {UniqueName}.", phase, CpuAffinitySpec.Format(cores), uniqueName);
            return true;
        }

        if (OperatingSystem.IsLinux())
            return TryApplyTaskset(uniqueName, process.Id, CpuAffinitySpec.Format(cores), phase);

        _logger.LogWarning("CPU affinity is not supported on this platform for server {UniqueName}.", uniqueName);
        return false;
    }

    private bool TryApplyTaskset(string uniqueName, int processId, string coreList, string phase)
    {
        try
        {
            using var taskset = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "taskset",
                    Arguments = $"-a -p -c {coreList} {processId}",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                },
            };

            if (!taskset.Start())
                return false;

            var stdout = taskset.StandardOutput.ReadToEnd();
            var stderr = taskset.StandardError.ReadToEnd();
            taskset.WaitForExit(3000);
            if (taskset.ExitCode == 0)
            {
                _logger.LogInformation("Applied {Phase} CPU affinity {Cores} to server {UniqueName}.", phase, coreList, uniqueName);
                return true;
            }

            _logger.LogWarning(
                "taskset failed applying {Phase} CPU affinity {Cores} to server {UniqueName}. ExitCode={ExitCode}. Stdout={Stdout}. Stderr={Stderr}",
                phase,
                coreList,
                uniqueName,
                taskset.ExitCode,
                TrimForLog(stdout),
                TrimForLog(stderr));
        }
        catch (Exception exception)
        {
            _logger.LogWarning(exception, "Failed running taskset for server {UniqueName}.", uniqueName);
        }

        return false;
    }

    private static string TrimForLog(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        value = value.Trim();
        return value.Length <= 2000 ? value : value[..2000] + "...";
    }

    private async Task HandleProcessExitedAsync(string uniqueName)
    {
        ManagedServerState? state;
        DedicatedServerDefinition definition;
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
                definition.GoalState == DedicatedServerGoalState.On &&
                definition.RestartOnCrash)
            {
                var nextAttempt = state.RestartAttempts + 1;
                var maxAttempts = EffectiveMaxRestartAttempts(definition);
                var hasBudget = nextAttempt <= maxAttempts;
                if (hasBudget)
                {
                    state.RestartAttempts = nextAttempt;
                    state.IsRestartPending = true;
                    state.State = DedicatedServerProcessState.Restarting;
                    state.LastMessage = $"Process exited with code {exitCode}. Restarting.";
                    shouldRestart = true;
                    restartDelaySeconds = Math.Max(0, definition.RestartDelaySeconds);
                }
                else
                {
                    state.RestartAttempts = maxAttempts;
                    state.IsRestartPending = false;
                    state.State = DedicatedServerProcessState.Faulted;
                    state.LastMessage = $"Process exited with code {exitCode}. Restart attempt limit ({maxAttempts}) reached.";
                }
            }

            if (!shouldRestart)
            {
                if (state.State != DedicatedServerProcessState.Faulted)
                {
                    state.IsRestartPending = false;
                    state.State = stopRequested
                        ? DedicatedServerProcessState.Stopped
                        : exitCode == 0
                            ? DedicatedServerProcessState.Stopped
                            : DedicatedServerProcessState.Crashed;
                    state.LastMessage = stopRequested
                        ? "Stopped by supervisor."
                        : exitCode == 0
                            ? "Process exited normally."
                            : $"Process exited with code {exitCode}.";
                }
            }
        }

        NotifyChanged();
        _logger.LogInformation("Server {UniqueName} exited with code {ExitCode}.", uniqueName, exitCode);
        PruneServerLogFiles(uniqueName, definition.DsLogFilesToKeep);

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
            if (!_states.TryGetValue(uniqueName, out state) || state.StopRequested || state.State != DedicatedServerProcessState.Restarting)
                return;
        }

        await StartServerAsync(uniqueName, _shutdown.Token);
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
            await _registry.SendCommandAndWaitAsync(new ServerCommandEnvelope
            {
                AgentId = agent.AgentId,
                UniqueName = uniqueName,
                ServerId = agent.ServerKey,
                CommandType = ServerCommandType.SaveWorld,
            }, TimeSpan.FromSeconds(15), cancellationToken);
        }
        catch (Exception exception)
        {
            _logger.LogDebug(exception, "Failed to request world save before stopping server {UniqueName}.", uniqueName);
        }

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
            _logger.LogDebug(exception, "Failed to send graceful stop to Quasar.Agent for server {UniqueName}.", uniqueName);
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
        if (persisted?.Servers is null || persisted.Servers.Count == 0)
            return;

        lock (_sync)
        {
            foreach (var persistedState in persisted.Servers)
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
                    // Give the agent a fresh grace window to reconnect to this new
                    // worker before health monitoring can judge the adopted server
                    // unhealthy and restart (kill) it.
                    state.AgentWatchSinceUtc = DateTimeOffset.UtcNow;
                    state.State = persistedState.State is DedicatedServerProcessState.Starting
                        or DedicatedServerProcessState.Running
                        or DedicatedServerProcessState.Restarting
                        or DedicatedServerProcessState.Stopping
                        ? persistedState.State
                        : DedicatedServerProcessState.Running;
                    state.LastMessage = "Process adopted after Quasar worker turnover.";
                    state.StoppedAtUtc = null;
                    // The adopted process was already pinned by the previous worker from the
                    // same persisted config, so treat the current affinity as applied. This
                    // keeps reconcile a no-op and honours "no need to set after reconnect".
                    state.LastAppliedCpuAffinity = state.Definition.CpuAffinity;
                }
                else
                {
                    state.Process = null;
                    state.ProcessId = null;

                    if (state.State is DedicatedServerProcessState.Starting
                        or DedicatedServerProcessState.Running
                        or DedicatedServerProcessState.Restarting
                        or DedicatedServerProcessState.Stopping)
                    {
                        state.State = DedicatedServerProcessState.Stopped;
                        state.LastMessage = "Previously running process not found during supervisor restore.";
                    }
                }
            }
        }
    }

    private void SyncDefinitions()
    {
        var definitions = _catalog.GetServers();

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
                    _states.Add(definition.UniqueName, new ManagedServerState
                    {
                        UniqueName = definition.UniqueName,
                        Definition = Clone(definition),
                        State = DedicatedServerProcessState.Stopped,
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

            state.State = DedicatedServerProcessState.Faulted;
            state.Process = null;
            state.ProcessId = null;
            state.IsRestartPending = false;
            state.LastMessage = message;
            state.StoppedAtUtc = DateTimeOffset.UtcNow;
            ResetHealthTracking(state);
        }

        _logger.LogWarning("Server {UniqueName} faulted: {Message}", uniqueName, message);
        NotifyChanged();
    }

    private void SetStopped(string uniqueName, string message)
    {
        var changed = false;
        lock (_sync)
        {
            if (!_states.TryGetValue(uniqueName, out var state))
                return;

            changed = state.State != DedicatedServerProcessState.Stopped ||
                      state.Process is not null ||
                      state.ProcessId is not null ||
                      state.IsRestartPending ||
                      !string.Equals(state.LastMessage, message, StringComparison.Ordinal);
            if (!changed)
                return;

            state.State = DedicatedServerProcessState.Stopped;
            state.Process = null;
            state.ProcessId = null;
            state.IsRestartPending = false;
            state.LastMessage = message;
            state.StoppedAtUtc = DateTimeOffset.UtcNow;
            ResetHealthTracking(state);
        }

        if (changed)
            NotifyChanged();
    }

    private const string MagnetarLogSource = "Magnetar";

    private (string StandardOutputPath, string StandardErrorPath) PrepareServerLogSlot(string uniqueName, int dsLogFilesToKeep)
    {
        var logDirectory = MagnetarPaths.GetQuasarServerLogDirectory(uniqueName);
        Directory.CreateDirectory(logDirectory);

        var timestamp = DateTimeOffset.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        RotateActiveLogIfPresent(uniqueName, logDirectory, StandardOutputLogName, timestamp);
        RotateActiveLogIfPresent(uniqueName, logDirectory, StandardErrorLogName, timestamp);
        PruneServerLogFiles(uniqueName, dsLogFilesToKeep);

        return (
            Path.Combine(logDirectory, StandardOutputLogName + ActiveLogExtension),
            Path.Combine(logDirectory, StandardErrorLogName + ActiveLogExtension));
    }

    private void RotateActiveLogIfPresent(string uniqueName, string logDirectory, string logName, string timestamp)
    {
        var activePath = Path.Combine(logDirectory, logName + ActiveLogExtension);
        try
        {
            if (!File.Exists(activePath))
                return;

            var activeLog = new FileInfo(activePath);
            if (activeLog.Length == 0)
            {
                activeLog.Delete();
                return;
            }

            var archivePath = ResolveArchivePath(logDirectory, logName, timestamp);
            activeLog.MoveTo(archivePath);
        }
        catch (Exception exception)
        {
            _logger.LogWarning(
                exception,
                "Failed rotating {LogName} log for server {UniqueName}; new output will append to the existing active log.",
                logName,
                uniqueName);
        }
    }

    private static string ResolveArchivePath(string logDirectory, string logName, string timestamp)
    {
        var path = Path.Combine(logDirectory, $"{logName}-{timestamp}{ActiveLogExtension}");
        if (!File.Exists(path))
            return path;

        for (var suffix = 1; suffix < 1000; suffix++)
        {
            path = Path.Combine(logDirectory, $"{logName}-{timestamp}-{suffix}{ActiveLogExtension}");
            if (!File.Exists(path))
                return path;
        }

        return Path.Combine(logDirectory, $"{logName}-{timestamp}-{Guid.NewGuid():N}{ActiveLogExtension}");
    }

    private void PruneServerLogFiles(string uniqueName, int dsLogFilesToKeep)
    {
        var logDirectory = MagnetarPaths.GetQuasarServerLogDirectory(uniqueName);
        if (!Directory.Exists(logDirectory))
            return;

        var keepCount = NormalizeDsLogFilesToKeep(dsLogFilesToKeep);
        var archivedToKeep = Math.Max(0, keepCount - 1);
        PruneRotatedLogFiles(uniqueName, logDirectory, StandardOutputLogName, archivedToKeep);
        PruneRotatedLogFiles(uniqueName, logDirectory, StandardErrorLogName, archivedToKeep);
    }

    private void PruneRotatedLogFiles(string uniqueName, string logDirectory, string logName, int archivedToKeep)
    {
        var directory = new DirectoryInfo(logDirectory);
        var staleArchives = directory
            .EnumerateFiles($"{logName}-*{ActiveLogExtension}", SearchOption.TopDirectoryOnly)
            .OrderByDescending(file => file.LastWriteTimeUtc)
            .ThenByDescending(file => file.Name, StringComparer.OrdinalIgnoreCase)
            .Skip(archivedToKeep)
            .ToList();

        foreach (var archive in staleArchives)
        {
            try
            {
                archive.Delete();
            }
            catch (Exception exception)
            {
                _logger.LogWarning(
                    exception,
                    "Failed deleting old {LogName} log {Path} for server {UniqueName}.",
                    logName,
                    archive.FullName,
                    uniqueName);
            }
        }
    }

    private static int NormalizeDsLogFilesToKeep(int dsLogFilesToKeep)
    {
        if (dsLogFilesToKeep < DedicatedServerDefinition.MinimumDsLogFilesToKeep)
            return DedicatedServerDefinition.DefaultDsLogFilesToKeep;

        return Math.Min(dsLogFilesToKeep, DedicatedServerDefinition.MaximumDsLogFilesToKeep);
    }

    private async Task PumpStandardOutputAsync(StreamReader reader, string path, string uniqueName, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
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

            // Plugin SDK JSON lines now reach the live panel through the agent's
            // network relay (see AgentSocketHandler), which survives Quasar
            // restarts and reconnects to detached server daemons — unlike this
            // stdout pump, which only exists for a child process we started.
            // Skip them here to avoid double entries; still surface ordinary
            // (non-plugin) server output.
            if (PluginLogStream.TryParseSinkLine(uniqueName, line, out _))
            {
                // Handled via the agent relay.
            }
            else if (!string.IsNullOrWhiteSpace(line))
            {
                RecordModDownloadFailure(uniqueName, line);
                _pluginLogStream.Append(BuildMagnetarEntry(uniqueName, line, "Info"));
            }
        }
    }

    private async Task PumpStandardErrorAsync(StreamReader reader, string path, string uniqueName, CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
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
            {
                RecordModDownloadFailure(uniqueName, line);
                _pluginLogStream.Append(BuildMagnetarEntry(uniqueName, line, "Error"));
            }
        }
    }

    private void RecordModDownloadFailure(string uniqueName, string line)
    {
        if (!TryBuildModDownloadFailureMessage(line, out var message))
            return;

        lock (_sync)
        {
            if (!_states.TryGetValue(uniqueName, out var state))
                return;

            if (state.ModDownloadFailures.Any(existing => string.Equals(existing, message, StringComparison.OrdinalIgnoreCase)))
                return;

            if (state.ModDownloadFailures.Count >= MaxModDownloadFailures)
                state.ModDownloadFailures.RemoveAt(0);

            state.ModDownloadFailures.Add(message);
            state.LastMessage = "Mod download failure reported by server output.";
        }
    }

    private static bool TryBuildModDownloadFailureMessage(string line, out string message)
    {
        message = string.Empty;
        var text = line.Trim();
        if (string.IsNullOrWhiteSpace(text))
            return false;

        if (TryNormalizePrefixedLogLine(text, out _, out var parsedMessage))
            text = parsedMessage;

        var lower = text.ToLowerInvariant();
        var mentionsWorkshopMod = lower.Contains("mod") || lower.Contains("workshop") || lower.Contains("publishedfile");
        if (!mentionsWorkshopMod)
            return false;

        var mentionsDownload = lower.Contains("download") || lower.Contains("subscrib") || lower.Contains("publishedfile") || lower.Contains("workshop");
        if (!mentionsDownload)
            return false;

        var isFailure =
            lower.Contains("fail") ||
            lower.Contains("not found") ||
            lower.Contains("unavailable") ||
            lower.Contains("missing") ||
            lower.Contains("denied") ||
            lower.Contains("error");
        if (!isFailure)
            return false;

        message = text.Length <= 300 ? text : $"{text[..300]}...";
        return true;
    }

    private static PluginLogEntry BuildMagnetarEntry(string uniqueName, string line, string level)
    {
        var timestamp = DateTimeOffset.UtcNow;
        var message = line;

        if (TryNormalizePrefixedLogLine(line, out var parsedTimestamp, out var parsedMessage))
        {
            timestamp = parsedTimestamp;
            message = parsedMessage;
        }

        return new PluginLogEntry
        {
            UniqueName = uniqueName,
            TimestampUtc = timestamp,
            Level = level,
            Plugin = MagnetarLogSource,
            Message = message,
        };
    }

    private static bool TryNormalizePrefixedLogLine(
        string line,
        out DateTimeOffset timestampUtc,
        out string message)
    {
        timestampUtc = DateTimeOffset.UtcNow;
        message = line;

        var match = PrefixedLogLinePattern.Match(line);
        if (!match.Success)
            return false;

        var timestampText = match.Groups["timestamp"].Value;
        if (!DateTime.TryParseExact(
                timestampText,
                ["yyyy-MM-dd HH:mm:ss.FFFFFFF", "yyyy-MM-dd HH:mm:ss"],
                CultureInfo.InvariantCulture,
                DateTimeStyles.AllowWhiteSpaces,
                out var localTimestamp))
        {
            return false;
        }

        timestampUtc = new DateTimeOffset(DateTime.SpecifyKind(localTimestamp, DateTimeKind.Local)).ToUniversalTime();
        message = match.Groups["message"].Value.TrimStart();
        return true;
    }

    private static bool IsProcessActive(Process? process)
    {
        if (process is null)
            return false;

        try
        {
            return !process.HasExited;
        }
        catch (InvalidOperationException)
        {
            // "No process is associated with this object." (or ObjectDisposedException,
            // which derives from it) — the process has already exited and been disposed
            // (e.g. its Exited event handler ran concurrently with a stop). That just
            // means it is no longer active.
            return false;
        }
    }

    private Dictionary<string, AgentRuntimeState> BuildAgentLookup()
    {
        return _registry.GetAgents()
            .Where(agent => agent.IsConnected)
            .Where(agent => !string.IsNullOrWhiteSpace(agent.UniqueNameKey))
            .GroupBy(agent => agent.UniqueNameKey, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => group.OrderByDescending(agent => agent.LastSeenUtc).First(),
                StringComparer.OrdinalIgnoreCase);
    }

    private static ServerHealthAssessment EvaluateHealth(
        ManagedServerState state,
        AgentRuntimeState? agent,
        DateTimeOffset now,
        bool disableHealthMonitoring)
    {
        var processActive = IsProcessActive(state.Process);
        var goalState = state.Definition.GoalState;

        if (goalState == DedicatedServerGoalState.Off)
        {
            return processActive
                ? new ServerHealthAssessment(DedicatedServerHealthState.Warning, "Process still running while goal state is OFF.")
                : new ServerHealthAssessment(DedicatedServerHealthState.Healthy, "" /* normal state, no notification needed */);
        }

        if (!processActive)
        {
            // A start/stop/restart the supervisor (or the user) initiated puts the
            // server through transient phases with no process attached yet: the
            // handle is assigned late in StartProcessAsync, and a restart first
            // tears the old process down. Report these as an indeterminate status
            // with no warning/error banner — they are expected, and the corner
            // event notification already tells the user the action is happening.
            // Only a server that is down without an intentional transition in
            // flight (crashed, faulted, or otherwise stopped while the goal is ON)
            // warrants an alert.
            var transitioning = state.StartInProgress
                || state.StopRequested
                || state.State is DedicatedServerProcessState.Starting
                    or DedicatedServerProcessState.Restarting;
            if (transitioning)
                return new ServerHealthAssessment(
                    DedicatedServerHealthState.Unknown,
                    state.State == DedicatedServerProcessState.Restarting
                        ? "Process is restarting."
                        : "Process is starting.");

            return new ServerHealthAssessment(
                DedicatedServerHealthState.Unhealthy,
                "Goal state is ON but the process is not running.");
        }

        if (disableHealthMonitoring)
            // Instance-wide condition, surfaced once at the top of the Dashboard
            // (see HealthMonitoringDisabled) rather than repeated on every card.
            return new ServerHealthAssessment(DedicatedServerHealthState.Unknown, "");

        if (!state.Definition.EnableHealthMonitoring)
            return new ServerHealthAssessment(DedicatedServerHealthState.Unknown, "Health monitoring disabled.");

        var uptime = state.StartedAtUtc.HasValue ? now - state.StartedAtUtc.Value : TimeSpan.Zero;

        if (agent is null || !agent.IsConnected)
        {
            // Count the attach grace from when we started watching for the agent
            // (adoption time for a re-adopted process), not the original start — an
            // adopted long-running server must get time for its agent to reconnect.
            var agentWatch = state.AgentWatchSinceUtc ?? state.StartedAtUtc;
            var agentWait = agentWatch.HasValue ? now - agentWatch.Value : uptime;
            if (agentWait < TimeSpan.FromSeconds(state.Definition.AgentStartupGraceSeconds))
            {
                return new ServerHealthAssessment(
                    DedicatedServerHealthState.Warning,
                    "Waiting for Quasar.Agent to attach.");
            }

            return new ServerHealthAssessment(
                DedicatedServerHealthState.Unhealthy,
                "Quasar.Agent did not attach within the configured startup grace period.");
        }

        var silence = now - agent.LastSeenUtc;
        if (silence > TimeSpan.FromSeconds(state.Definition.AgentHeartbeatTimeoutSeconds))
        {
            return new ServerHealthAssessment(
                DedicatedServerHealthState.Unhealthy,
                $"Quasar.Agent heartbeat stale beyond {state.Definition.AgentHeartbeatTimeoutSeconds}s timeout.");
        }

        var simulationProgress = EvaluateSimulationProgress(state, agent, uptime);
        if (simulationProgress.State != DedicatedServerHealthState.Healthy)
            return simulationProgress;

        if (simulationProgress.SimulationProgressScore.HasValue &&
            simulationProgress.SimulationProgressWindowSeconds.HasValue &&
            simulationProgress.SimulationProgressWindowSeconds.Value > 0)
        {
            state.LastHealthySummary = $"Server healthy. Frame progress score {simulationProgress.SimulationProgressScore.Value:0.00} over {simulationProgress.SimulationProgressWindowSeconds.Value}s.";
        }

        if (state.Definition.RecycleAfterUptimeHours > 0 &&
            uptime >= TimeSpan.FromHours(state.Definition.RecycleAfterUptimeHours))
        {
            return new ServerHealthAssessment(
                DedicatedServerHealthState.Unhealthy,
                $"Process uptime exceeded recycle threshold of {state.Definition.RecycleAfterUptimeHours}h.");
        }

        if (state.Definition.WarnAfterUptimeHours > 0 &&
            uptime >= TimeSpan.FromHours(state.Definition.WarnAfterUptimeHours))
        {
            return new ServerHealthAssessment(
                DedicatedServerHealthState.Warning,
                $"Process uptime exceeded warning threshold of {state.Definition.WarnAfterUptimeHours}h.");
        }

        return new ServerHealthAssessment(
            DedicatedServerHealthState.Healthy,
            string.IsNullOrWhiteSpace(state.LastHealthySummary) ? "Server healthy." : state.LastHealthySummary,
            state.SimulationProgressScore,
            state.SimulationProgressWindowSeconds,
            state.SimulationFramesAdvanced);
    }

    private static ServerHealthAssessment EvaluateSimulationProgress(
        ManagedServerState state,
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
            return new ServerHealthAssessment(
                DedicatedServerHealthState.Unhealthy,
                $"Simulation frame progress score {score:0.00} is below minimum {state.Definition.MinimumSimulationProgressScore:0.00} over {elapsed.TotalSeconds:0.#}s ({frameDelta} frames advanced).",
                score,
                state.SimulationProgressWindowSeconds,
                frameDelta);
        }

        return new ServerHealthAssessment(
            DedicatedServerHealthState.Healthy,
            "Server healthy.",
            score,
            state.SimulationProgressWindowSeconds,
            frameDelta);
    }

    private static ServerHealthAssessment BuildExistingSimulationAssessment(ManagedServerState state, string waitingSummary)
    {
        if (!state.SimulationProgressScore.HasValue ||
            !state.SimulationProgressWindowSeconds.HasValue ||
            !state.SimulationFramesAdvanced.HasValue)
        {
            return new ServerHealthAssessment(DedicatedServerHealthState.Warning, waitingSummary);
        }

        if (state.SimulationProgressScore.Value < state.Definition.MinimumSimulationProgressScore)
        {
            return new ServerHealthAssessment(
                DedicatedServerHealthState.Unhealthy,
                $"Simulation frame progress score {state.SimulationProgressScore.Value:0.00} is below minimum {state.Definition.MinimumSimulationProgressScore:0.00} over {state.SimulationProgressWindowSeconds.Value}s ({state.SimulationFramesAdvanced.Value} frames advanced).",
                state.SimulationProgressScore,
                state.SimulationProgressWindowSeconds,
                state.SimulationFramesAdvanced);
        }

        return new ServerHealthAssessment(
            DedicatedServerHealthState.Healthy,
            "Server healthy.",
            state.SimulationProgressScore,
            state.SimulationProgressWindowSeconds,
            state.SimulationFramesAdvanced);
    }

    private static void SetSimulationBaseline(ManagedServerState state, DateTimeOffset observedAtUtc, ulong simulationFrameCounter)
    {
        state.LastSimulationFrameObservedAtUtc = observedAtUtc;
        state.LastSimulationFrameCounter = simulationFrameCounter;
    }

    private static void ResetHealthTracking(ManagedServerState state)
    {
        state.HealthState = DedicatedServerHealthState.Unknown;
        state.HealthSummary = string.Empty;
        state.LastSimulationFrameCounter = null;
        state.LastSimulationFrameObservedAtUtc = null;
        state.SimulationProgressScore = null;
        state.SimulationProgressWindowSeconds = null;
        state.SimulationFramesAdvanced = null;
        state.LastSimulationProgressEvaluatedAtUtc = null;
        state.LastHealthySummary = string.Empty;
    }

    private static bool CanScheduleHealthRestart(ManagedServerState state, DateTimeOffset now)
    {
        if (!state.LastHealthRecoveryActionUtc.HasValue)
            return true;

        return (now - state.LastHealthRecoveryActionUtc.Value) >= TimeSpan.FromSeconds(Math.Max(30, state.Definition.RestartDelaySeconds));
    }

    private bool CanRunPlannedRestart(ManagedServerState state)
    {
        if (!_options.AvoidSimultaneousScheduledRestarts)
            return true;

        return !_states.Values.Any(current =>
            !string.Equals(current.UniqueName, state.UniqueName, StringComparison.OrdinalIgnoreCase) &&
            current.State is DedicatedServerProcessState.Starting
                or DedicatedServerProcessState.Stopping
                or DedicatedServerProcessState.Restarting);
    }

    private static bool ShouldScheduledRestartFire(ManagedServerState state, DateTimeOffset now, bool consume)
    {
        if (!state.StartedAtUtc.HasValue)
            return false;

        if ((now - state.StartedAtUtc.Value) < TimeSpan.FromMinutes(5))
            return false;

        var nowLocal = now.ToLocalTime();
        foreach (var (scheduledHour, scheduledMinute) in ParseScheduleTimes(state.Definition.DailyRestartTimeLocal))
        {
            var scheduledLocal = new DateTimeOffset(
                nowLocal.Year, nowLocal.Month, nowLocal.Day,
                scheduledHour, scheduledMinute, 0,
                nowLocal.Offset);

            if (nowLocal < scheduledLocal)
                continue;

            if (nowLocal - scheduledLocal > TimeSpan.FromMinutes(5))
                continue;

            var scheduleKey = $"{scheduledLocal:yyyyMMddHHmm}";
            if (string.Equals(state.LastScheduledRestartKey, scheduleKey, StringComparison.Ordinal))
                continue;

            if (consume)
            {
                state.LastScheduledRestartKey = scheduleKey;
                state.LastScheduledRestartUtc = now;
            }

            return true;
        }

        return false;
    }

    private static bool ShouldMaximumUptimeRestartFire(ManagedServerState state, DateTimeOffset now)
    {
        if (!state.StartedAtUtc.HasValue)
            return false;

        if (!TryParseDurationHoursMinutes(state.Definition.MaximumUptime, out var maximumUptime))
            return false;

        if (maximumUptime <= TimeSpan.Zero)
            return false;

        return now - state.StartedAtUtc.Value >= maximumUptime;
    }

    private static IReadOnlyList<(int Hour, int Minute)> ParseScheduleTimes(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return [];

        return value
            .Split([' ', ',', ';', '\n', '\r', '\t'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(TryParseDailyTime)
            .Where(item => item.HasValue)
            .Select(item => item!.Value)
            .Distinct()
            .OrderBy(item => item.Hour)
            .ThenBy(item => item.Minute)
            .ToList();
    }

    private static (int Hour, int Minute)? TryParseDailyTime(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return null;

        var parts = value.Trim().Split(':');
        if (parts.Length != 2)
            return null;

        if (!int.TryParse(parts[0], out var hour) || hour < 0 || hour > 23)
            return null;

        if (!int.TryParse(parts[1], out var minute) || minute < 0 || minute > 59)
            return null;

        return (hour, minute);
    }

    private static bool TryParseDurationHoursMinutes(string value, out TimeSpan duration)
    {
        duration = TimeSpan.Zero;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var parts = value.Trim().Split(':');
        if (parts.Length != 2)
            return false;

        if (!int.TryParse(parts[0], out var hours) || hours < 0)
            return false;

        if (!int.TryParse(parts[1], out var minutes) || minutes < 0 || minutes > 59)
            return false;

        duration = TimeSpan.FromHours(hours) + TimeSpan.FromMinutes(minutes);
        return true;
    }

    private void NotifyChanged()
    {
        SchedulePersistState();
        Changed?.Invoke();
    }

    private void HandleRuntimeWarmupChanged()
    {
        if (!_runtimeWarmup.IsReady || _isStopping)
            return;

        _ = Task.Run(async () =>
        {
            try
            {
                await ReconcileAsync(_shutdown.Token);
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                _logger.LogWarning(exception, "Quasar reconciliation after managed runtime warmup failed.");
            }
        }, CancellationToken.None);
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
                Servers = _states.Values
                    .Select(state => new PersistedManagedServerState
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

    private static int EffectiveMaxRestartAttempts(DedicatedServerDefinition definition) =>
        Math.Max(1, definition.MaxRestartAttempts);

    private static int EffectiveAgentAttachRetryAttempts(DedicatedServerDefinition definition) =>
        Math.Max(1, definition.AgentAttachRetryAttempts);

    private static DedicatedServerDefinition Clone(DedicatedServerDefinition definition)
    {
        return new DedicatedServerDefinition
        {
            UniqueName = definition.UniqueName,
            DisplayName = definition.DisplayName,
            OriginalUniqueName = definition.OriginalUniqueName,
            GoalState = definition.GoalState,
            ExecutablePath = definition.ExecutablePath,
            WorkingDirectory = definition.WorkingDirectory,
            ManagedRuntime = definition.ManagedRuntime,
            DedicatedServerAppDataPath = definition.DedicatedServerAppDataPath,
            MagnetarAppDataPath = definition.MagnetarAppDataPath,
            WorldPath = definition.WorldPath,
            ConfigFilePath = definition.ConfigFilePath,
            ConfigProfileId = definition.ConfigProfileId,
            WorldTemplateId = definition.WorldTemplateId,
            LaunchArguments = definition.LaunchArguments,
            LogLaunchEnvironment = definition.LogLaunchEnvironment,
            ServerPort = definition.ServerPort,
            ServerIP = definition.ServerIP,
            AutoStart = definition.AutoStart,
            EnableHealthMonitoring = definition.EnableHealthMonitoring,
            AutoRestartOnUnhealthy = definition.AutoRestartOnUnhealthy,
            AgentStartupGraceSeconds = definition.AgentStartupGraceSeconds,
            AgentAttachRetryAttempts = definition.AgentAttachRetryAttempts,
            AgentAttachRetryDelaySeconds = definition.AgentAttachRetryDelaySeconds,
            AgentHeartbeatTimeoutSeconds = definition.AgentHeartbeatTimeoutSeconds,
            SimulationProgressWindowSeconds = definition.SimulationProgressWindowSeconds,
            MinimumSimulationProgressScore = definition.MinimumSimulationProgressScore,
            WarnAfterUptimeHours = definition.WarnAfterUptimeHours,
            RecycleAfterUptimeHours = definition.RecycleAfterUptimeHours,
            RestartOnCrash = definition.RestartOnCrash,
            RestartDelaySeconds = definition.RestartDelaySeconds,
            MaxRestartAttempts = definition.MaxRestartAttempts,
            DailyRestartTimeLocal = definition.DailyRestartTimeLocal,
            MaximumUptime = definition.MaximumUptime,
            AvoidSimultaneousScheduledRestarts = definition.AvoidSimultaneousScheduledRestarts,
            StartupProcessPriority = definition.StartupProcessPriority,
            ReadyProcessPriority = definition.ReadyProcessPriority,
            CpuAffinity = definition.CpuAffinity,
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

    private static DedicatedServerRuntimeSnapshot CloneSnapshot(
        ManagedServerState state,
        AgentRuntimeState? agent,
        DateTimeOffset now,
        bool disableHealthMonitoring)
    {
        return new DedicatedServerRuntimeSnapshot
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
            ModDownloadFailures = state.ModDownloadFailures.ToList(),
        };
    }

    private sealed class ManagedServerState
    {
        public string UniqueName { get; set; } = string.Empty;

        public DedicatedServerDefinition Definition { get; set; } = new();

        public DedicatedServerProcessState State { get; set; } = DedicatedServerProcessState.Stopped;

        public DedicatedServerHealthState HealthState { get; set; } = DedicatedServerHealthState.Unknown;

        public string HealthSummary { get; set; } = string.Empty;

        public Process? Process { get; set; }

        public bool StopRequested { get; set; }

        public bool IsRestartPending { get; set; }

        // Set while a StartProcessAsync call is in flight for this server.
        // The OS process handle (Process) is only assigned late in that method,
        // after the potentially slow managed-runtime resolve/copy, so this flag
        // closes the window where two overlapping reconciles would both pass the
        // IsProcessActive guard and launch duplicate processes.
        public bool StartInProgress { get; set; }

        public CancellationTokenSource? StartCancellation { get; set; }

        public int RestartAttempts { get; set; }

        public int AgentAttachRetryAttempts { get; set; }

        public int? ProcessId { get; set; }

        public int? LastExitCode { get; set; }

        public string LastMessage { get; set; } = string.Empty;

        public DateTimeOffset? StartedAtUtc { get; set; }

        // When the supervisor began expecting an agent connection for the current
        // process. Equals StartedAtUtc for a fresh launch, but is reset to "now" when
        // a surviving process is adopted after a worker restart — so the agent-attach
        // grace counts from adoption, not the (possibly hours-old) original start.
        public DateTimeOffset? AgentWatchSinceUtc { get; set; }

        public DateTimeOffset? StoppedAtUtc { get; set; }

        public string StandardOutputLogPath { get; set; } = string.Empty;

        public string StandardErrorLogPath { get; set; } = string.Empty;

        public DateTimeOffset? LastHealthRecoveryActionUtc { get; set; }

        public DateTimeOffset? LastScheduledRestartUtc { get; set; }

        public string LastScheduledRestartKey { get; set; } = string.Empty;

        public DedicatedServerProcessPriority? LastAppliedProcessPriority { get; set; }

        public DedicatedServerProcessPriority? LastFailedProcessPriority { get; set; }

        // Canonical affinity string last successfully applied to the running process, and the
        // last one that failed to apply (so we don't retry it every reconcile). Null means
        // "not yet applied this process lifetime".
        public string? LastAppliedCpuAffinity { get; set; }

        public string? LastFailedCpuAffinity { get; set; }

        public ulong? LastSimulationFrameCounter { get; set; }

        public DateTimeOffset? LastSimulationFrameObservedAtUtc { get; set; }

        public float? SimulationProgressScore { get; set; }

        public int? SimulationProgressWindowSeconds { get; set; }

        public ulong? SimulationFramesAdvanced { get; set; }

        public DateTimeOffset? LastSimulationProgressEvaluatedAtUtc { get; set; }

        public string LastHealthySummary { get; set; } = string.Empty;

        public List<string> ModDownloadFailures { get; } = [];
    }

    private enum ReconcileAction
    {
        Start = 0,
        Stop = 1,
        Restart = 2,
        RetryAttach = 3,
    }

    private readonly record struct ServerHealthAssessment(
        DedicatedServerHealthState State,
        string Summary,
        float? SimulationProgressScore = null,
        int? SimulationProgressWindowSeconds = null,
        ulong? SimulationFramesAdvanced = null);

    private sealed class PersistedSupervisorState
    {
        public DateTimeOffset UpdatedAtUtc { get; set; }

        public List<PersistedManagedServerState> Servers { get; set; } = [];
    }

    private sealed class PersistedManagedServerState
    {
        public string UniqueName { get; set; } = string.Empty;

        public DedicatedServerProcessState State { get; set; } = DedicatedServerProcessState.Stopped;

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
