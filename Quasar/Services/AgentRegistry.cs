using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Quasar.Services.Analytics;

namespace Quasar.Services;

public sealed class AgentRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, AgentRuntimeState> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServerCommandEnvelope> _pendingCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, TaskCompletionSource<ServerCommandResult>> _pendingResults = new(StringComparer.OrdinalIgnoreCase);
    private readonly KnownPlayerCatalog _knownPlayers;
    private readonly MetricsStoreService _metricsStore;
    private readonly ProfilerStoreService _profilerStore;
    private readonly PluginStatsStoreService _pluginStatsStore;

    public AgentRegistry(KnownPlayerCatalog knownPlayers, MetricsStoreService metricsStore, ProfilerStoreService profilerStore, PluginStatsStoreService pluginStatsStore)
    {
        _knownPlayers = knownPlayers;
        _metricsStore = metricsStore;
        _profilerStore = profilerStore;
        _pluginStatsStore = pluginStatsStore;
    }

    public event Action? Changed;

    public IReadOnlyList<AgentRuntimeState> GetAgents()
    {
        lock (_sync)
        {
            return _agents.Values
                .Select(state => state.Clone())
                .OrderBy(state => state.HostDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(state => state.ServerDisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
    }

    public void PruneDisconnectedByUniqueName(string uniqueName)
    {
        if (string.IsNullOrWhiteSpace(uniqueName))
            return;

        var changed = false;
        lock (_sync)
        {
            foreach (var agentId in _agents.Values
                         .Where(state => !state.IsConnected &&
                             string.Equals(state.UniqueNameKey, uniqueName, StringComparison.OrdinalIgnoreCase))
                         .Select(state => state.AgentId)
                         .ToList())
            {
                _agents.Remove(agentId);
                changed = true;
            }
        }

        if (changed)
            NotifyChanged();
    }

    public void UpsertHello(
        AgentHello hello,
        string connectionId,
        Func<AgentWireMessage, CancellationToken, Task> sender)
    {
        lock (_sync)
        {
            var state = GetOrCreateState(hello.AgentId);
            if (state.ConnectionId != connectionId)
            {
                state.Snapshot = null;
                state.CommandResults.Clear();
                foreach (var id in _pendingCommands.Where(p => p.Value.AgentId == hello.AgentId).Select(p => p.Key).ToArray())
                {
                    _pendingCommands.Remove(id);
                    if (_pendingResults.Remove(id, out var pending))
                        pending.TrySetException(new InvalidOperationException("Agent connection was replaced."));
                }
            }
            state.ConnectionId = connectionId;
            state.IsConnected = true;
            state.LastSeenUtc = DateTimeOffset.UtcNow;
            state.Hello = hello;
            state.Sender = sender;
        }

        NotifyChanged();
    }

    public void UpdateSnapshot(AgentSnapshot snapshot, string connectionId)
    {
        AgentSnapshot latestSnapshot;
        string telemetryKey;

        lock (_sync)
        {
            if (!_agents.TryGetValue(snapshot.AgentId, out var state) || !state.IsConnected
                || state.ConnectionId != connectionId || state.Hello is not { } hello
                || snapshot.ClusterMode != hello.ClusterMode || snapshot.UniqueName != hello.UniqueName)
                return;
            if (hello.ClusterMode && (snapshot.ClusterId != hello.ClusterId || snapshot.ClusterSlot != hello.ClusterSlot
                || (hello.ClusterNodeId.Length > 0 && snapshot.ClusterNodeId != hello.ClusterNodeId)
                || (hello.ClusterEpoch > 0 && snapshot.ClusterEpoch != hello.ClusterEpoch)
                || (state.Snapshot?.ClusterEpoch is > 0 && snapshot.ClusterEpoch != state.Snapshot.ClusterEpoch)))
                return;
            state.LastSeenUtc = DateTimeOffset.UtcNow;
            state.Snapshot = snapshot;
            state.LastSnapshotReceivedUtc = DateTimeOffset.UtcNow;
            latestSnapshot = state.Snapshot;
            telemetryKey = state.TelemetryKey;
        }

        if (!latestSnapshot.ClusterMode) _knownPlayers.ObserveSnapshot(latestSnapshot);
        if (!string.IsNullOrWhiteSpace(snapshot.UniqueName))
        {
            var sample = MetricSampleFactory.FromSnapshot(snapshot);
            _metricsStore.Enqueue(telemetryKey, in sample);

            if (snapshot.Profiler is not null)
                _profilerStore.Enqueue(telemetryKey, snapshot.Profiler);

            if (snapshot.PluginStats is not null)
                _pluginStatsStore.Enqueue(telemetryKey, snapshot.PluginStats);
        }

        NotifyChanged();
    }

    public void TouchConnection(string connectionId)
    {
        if (string.IsNullOrWhiteSpace(connectionId))
            return;

        lock (_sync)
        {
            foreach (var state in _agents.Values.Where(state =>
                         state.IsConnected &&
                         string.Equals(state.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase)))
            {
                state.LastSeenUtc = DateTimeOffset.UtcNow;
            }
        }
    }

    public void UpdateCommandResult(ServerCommandResult result, string connectionId)
    {
        ServerCommandEnvelope? command = null;
        TaskCompletionSource<ServerCommandResult>? awaiter = null;

        lock (_sync)
        {
            if (!_agents.TryGetValue(result.AgentId, out var state) || !state.IsConnected || state.ConnectionId != connectionId
                || !_pendingCommands.TryGetValue(result.CommandId, out var expected) || expected.AgentId != result.AgentId)
                return;
            state.LastSeenUtc = DateTimeOffset.UtcNow;
            state.CommandResults.Insert(0, result);
            if (state.CommandResults.Count > 20)
                state.CommandResults.RemoveRange(20, state.CommandResults.Count - 20);

            if (!string.IsNullOrWhiteSpace(result.CommandId))
            {
                _pendingCommands.TryGetValue(result.CommandId, out command);
                _pendingCommands.Remove(result.CommandId);

                if (_pendingResults.TryGetValue(result.CommandId, out awaiter))
                    _pendingResults.Remove(result.CommandId);
            }
        }

        awaiter?.TrySetResult(result);

        if (command is not null)
            _knownPlayers.ApplyCommandOutcome(command, result);

        NotifyChanged();
    }

    public void MarkDisconnected(string connectionId)
    {
        var changed = false;
        var disconnectedAgentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var abandonedAwaiters = new List<TaskCompletionSource<ServerCommandResult>>();

        lock (_sync)
        {
            foreach (var state in _agents.Values.Where(state =>
                         string.Equals(state.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase)))
            {
                state.IsConnected = false;
                state.LastSeenUtc = DateTimeOffset.UtcNow;
                state.Sender = null;
                disconnectedAgentIds.Add(state.AgentId);
                changed = true;
            }

            if (disconnectedAgentIds.Count > 0)
            {
                foreach (var commandId in _pendingCommands
                             .Where(entry => disconnectedAgentIds.Contains(entry.Value.AgentId))
                             .Select(entry => entry.Key)
                             .ToList())
                {
                    _pendingCommands.Remove(commandId);

                    if (_pendingResults.TryGetValue(commandId, out var awaiter))
                    {
                        _pendingResults.Remove(commandId);
                        abandonedAwaiters.Add(awaiter);
                    }
                }
            }
        }

        foreach (var awaiter in abandonedAwaiters)
            awaiter.TrySetException(new InvalidOperationException("Agent disconnected before responding to the command."));

        if (changed)
            NotifyChanged();
    }

    public async Task SendCommandAsync(ServerCommandEnvelope command, CancellationToken cancellationToken = default)
    {
        Func<AgentWireMessage, CancellationToken, Task>? sender;

        lock (_sync)
        {
            if (!_agents.TryGetValue(command.AgentId, out var state) || state.Sender is null || !state.IsConnected)
                throw new InvalidOperationException($"Agent '{command.AgentId}' is not connected.");

            sender = state.Sender;
            _pendingCommands[command.CommandId] = CloneCommand(command);
        }

        try
        {
            await sender(new AgentWireMessage
            {
                Kind = WireMessageKind.Command,
                Command = command,
            }, cancellationToken);
        }
        catch
        {
            lock (_sync)
            {
                _pendingCommands.Remove(command.CommandId);
            }

            throw;
        }
    }

    /// <summary>
    /// Sends an arbitrary wire message to a connected agent. Used for
    /// fire-and-forget control messages such as plugin config updates that do
    /// not flow through the command/result pipeline.
    /// </summary>
    public async Task SendToAgentAsync(string agentId, AgentWireMessage message, CancellationToken cancellationToken = default,
        string? expectedConnectionId = null)
    {
        Func<AgentWireMessage, CancellationToken, Task>? sender;

        lock (_sync)
        {
            if (!_agents.TryGetValue(agentId, out var state) || state.Sender is null || !state.IsConnected)
                throw new InvalidOperationException($"Agent '{agentId}' is not connected.");

            if (state.IsCluster && message.Kind == WireMessageKind.PluginConfigUpdate)
                throw new InvalidOperationException("Cluster plugin configuration must be activated for the whole cluster.");
            if (expectedConnectionId is not null && state.ConnectionId != expectedConnectionId)
                throw new InvalidOperationException("Agent connection changed; reload its configuration before applying edits.");
            sender = state.Sender;
        }

        await sender(message, cancellationToken);
    }

    /// <summary>
    /// Sends a command and awaits the matching <see cref="ServerCommandResult"/> from the agent.
    /// Used by request/response commands such as <see cref="ServerCommandType.ListEntities"/>.
    /// Throws <see cref="TimeoutException"/> if the agent does not respond in time, or
    /// <see cref="InvalidOperationException"/> if the agent is not connected / disconnects.
    /// </summary>
    public async Task<ServerCommandResult> SendCommandAndWaitAsync(
        ServerCommandEnvelope command,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        Func<AgentWireMessage, CancellationToken, Task>? sender;
        var completion = new TaskCompletionSource<ServerCommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);

        lock (_sync)
        {
            if (!_agents.TryGetValue(command.AgentId, out var state) || state.Sender is null || !state.IsConnected)
                throw new InvalidOperationException($"Agent '{command.AgentId}' is not connected.");

            sender = state.Sender;
            _pendingCommands[command.CommandId] = CloneCommand(command);
            _pendingResults[command.CommandId] = completion;
        }

        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutCts.CancelAfter(timeout);

        await using var registration = timeoutCts.Token.Register(() =>
        {
            if (cancellationToken.IsCancellationRequested)
                completion.TrySetCanceled(cancellationToken);
            else
                completion.TrySetException(new TimeoutException(
                    $"Agent '{command.AgentId}' did not respond within {timeout.TotalSeconds:0}s."));
        });

        try
        {
            await sender(new AgentWireMessage
            {
                Kind = WireMessageKind.Command,
                Command = command,
            }, cancellationToken);

            return await completion.Task;
        }
        finally
        {
            lock (_sync)
            {
                _pendingCommands.Remove(command.CommandId);
                _pendingResults.Remove(command.CommandId);
            }
        }
    }

    private AgentRuntimeState GetOrCreateState(string agentId)
    {
        agentId = string.IsNullOrWhiteSpace(agentId) ? Guid.NewGuid().ToString("N") : agentId;

        if (!_agents.TryGetValue(agentId, out var state))
        {
            state = new AgentRuntimeState
            {
                AgentId = agentId,
            };
            _agents.Add(agentId, state);
        }

        return state;
    }

    public bool TryGetUniqueName(string connectionId, out string uniqueName)
    {
        uniqueName = string.Empty;
        if (string.IsNullOrWhiteSpace(connectionId))
            return false;

        lock (_sync)
        {
            var state = _agents.Values.FirstOrDefault(current =>
                string.Equals(current.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase));

            if (state is null || !state.IsConnected || state.IsCluster || string.IsNullOrWhiteSpace(state.UniqueNameKey))
                return false;

            uniqueName = state.UniqueNameKey;
            return true;
        }
    }

    public bool IsCurrentConnection(string agentId, string connectionId)
    {
        lock (_sync) return _agents.TryGetValue(agentId, out var state)
            && state.IsConnected && state.ConnectionId == connectionId;
    }

    public bool TryGetTelemetryKey(string connectionId, out string key)
    {
        lock (_sync)
        {
            var state = _agents.Values.FirstOrDefault(s => s.IsConnected && s.ConnectionId == connectionId);
            key = state?.TelemetryKey ?? string.Empty;
            return state is not null;
        }
    }

    private void NotifyChanged()
    {
        Changed?.Invoke();
    }

    private static ServerCommandEnvelope CloneCommand(ServerCommandEnvelope command)
    {
        return new ServerCommandEnvelope
        {
            CommandId = command.CommandId,
            UniqueName = command.UniqueName,
            AgentId = command.AgentId,
            ServerId = command.ServerId,
            CommandType = command.CommandType,
            Text = command.Text,
            SteamId = command.SteamId,
            Payload = command.Payload,
            IssuedAtUtc = command.IssuedAtUtc,
        };
    }
}

public sealed class AgentRuntimeState
{
    public string AgentId { get; set; } = string.Empty;

    public string ConnectionId { get; set; } = string.Empty;

    public bool IsConnected { get; set; }

    public DateTimeOffset LastSeenUtc { get; set; }

    public DateTimeOffset LastSnapshotReceivedUtc { get; set; }

    public AgentHello? Hello { get; set; }

    public AgentSnapshot? Snapshot { get; set; }

    public List<ServerCommandResult> CommandResults { get; set; } = new();

    public Func<AgentWireMessage, CancellationToken, Task>? Sender { get; set; }

    public bool IsCluster => Snapshot?.ClusterMode ?? Hello?.ClusterMode ?? false;
    public string ClusterId => Snapshot?.ClusterId ?? Hello?.ClusterId ?? string.Empty;
    public string ClusterSlot => Snapshot?.ClusterSlot ?? Hello?.ClusterSlot ?? string.Empty;
    public string ClusterNodeId => Snapshot?.ClusterNodeId ?? Hello?.ClusterNodeId ?? string.Empty;
    public long ClusterEpoch => Snapshot?.ClusterEpoch ?? Hello?.ClusterEpoch ?? 0;
    public bool HasClusterIdentity => IsCluster && ClusterId.Length > 0 && ClusterSlot.Length > 0
        && ClusterNodeId.Length > 0 && ClusterEpoch > 0;
    public string TelemetryKey => !IsCluster ? UniqueNameKey : "cluster-" + Convert.ToHexString(
        System.Security.Cryptography.SHA256.HashData(System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(
            new { ClusterId, ClusterSlot, ClusterNodeId, ClusterEpoch, Process = HasClusterIdentity ? null : AgentId }))).ToLowerInvariant();

    public string UniqueNameKey => Snapshot?.UniqueName ?? Hello?.UniqueName ?? ServerKey;

    public string HostKey => Snapshot?.HostId ?? Hello?.HostId ?? string.Empty;

    public string ServerKey => Snapshot?.ServerId ?? Hello?.ServerId ?? AgentId;

    public string HostDisplayName => Snapshot?.HostName ?? Hello?.HostName ?? "Unknown host";

    public string ServerDisplayName => Snapshot?.ServerName ?? Hello?.ServerName ?? "Unknown server";

    public string WorldDisplayName => Snapshot?.WorldName ?? Hello?.WorldName ?? "Unknown world";

    public AgentRuntimeState Clone()
    {
        return new AgentRuntimeState
        {
            AgentId = AgentId,
            ConnectionId = ConnectionId,
            IsConnected = IsConnected,
            LastSeenUtc = LastSeenUtc,
            LastSnapshotReceivedUtc = LastSnapshotReceivedUtc,
            Hello = Hello,
            Snapshot = Snapshot,
            CommandResults = new List<ServerCommandResult>(CommandResults),
            Sender = Sender,
        };
    }
}
