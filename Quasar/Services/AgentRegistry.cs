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

    public AgentRegistry(KnownPlayerCatalog knownPlayers, MetricsStoreService metricsStore, ProfilerStoreService profilerStore)
    {
        _knownPlayers = knownPlayers;
        _metricsStore = metricsStore;
        _profilerStore = profilerStore;
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

        lock (_sync)
        {
            var state = GetOrCreateState(ResolveAgentId(snapshot.AgentId, connectionId));
            state.ConnectionId = connectionId;
            state.IsConnected = true;
            state.LastSeenUtc = DateTimeOffset.UtcNow;
            state.Snapshot = snapshot;
            latestSnapshot = state.Snapshot;
        }

        _knownPlayers.ObserveSnapshot(latestSnapshot);
        if (!string.IsNullOrWhiteSpace(snapshot.UniqueName))
        {
            var sample = MetricSampleFactory.FromSnapshot(snapshot);
            _metricsStore.Enqueue(snapshot.UniqueName, in sample);

            if (snapshot.Profiler is not null)
                _profilerStore.Enqueue(snapshot.UniqueName, snapshot.Profiler);
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

    public void UpdateCommandResult(ServerCommandResult result)
    {
        ServerCommandEnvelope? command = null;
        TaskCompletionSource<ServerCommandResult>? awaiter = null;

        lock (_sync)
        {
            var state = GetOrCreateState(result.AgentId);
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
    public async Task SendToAgentAsync(string agentId, AgentWireMessage message, CancellationToken cancellationToken = default)
    {
        Func<AgentWireMessage, CancellationToken, Task>? sender;

        lock (_sync)
        {
            if (!_agents.TryGetValue(agentId, out var state) || state.Sender is null || !state.IsConnected)
                throw new InvalidOperationException($"Agent '{agentId}' is not connected.");

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

            if (state is null || string.IsNullOrWhiteSpace(state.UniqueNameKey))
                return false;

            uniqueName = state.UniqueNameKey;
            return true;
        }
    }

    private string ResolveAgentId(string? agentId, string connectionId)
    {
        if (!string.IsNullOrWhiteSpace(agentId))
            return agentId;

        var existing = _agents.Values.FirstOrDefault(state =>
            string.Equals(state.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase));

        return existing?.AgentId ?? connectionId;
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

    public AgentHello? Hello { get; set; }

    public AgentSnapshot? Snapshot { get; set; }

    public List<ServerCommandResult> CommandResults { get; set; } = new();

    public Func<AgentWireMessage, CancellationToken, Task>? Sender { get; set; }

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
            Hello = Hello,
            Snapshot = Snapshot,
            CommandResults = new List<ServerCommandResult>(CommandResults),
            Sender = Sender,
        };
    }
}
