using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Quasar.Services.Analytics;

namespace Quasar.Services;

public sealed class AgentRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, AgentRuntimeState> _agents = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ServerCommandEnvelope> _pendingCommands = new(StringComparer.OrdinalIgnoreCase);
    private readonly KnownPlayerCatalog _knownPlayers;
    private readonly MetricsStoreService _metricsStore;

    public AgentRegistry(KnownPlayerCatalog knownPlayers, MetricsStoreService metricsStore)
    {
        _knownPlayers = knownPlayers;
        _metricsStore = metricsStore;
    }

    public event Action? Changed;

    public IReadOnlyList<AgentRuntimeState> GetAgents()
    {
        lock (_sync)
        {
            return _agents.Values
                .Select(state => state.Clone())
                .OrderBy(state => state.NodeDisplayName, StringComparer.OrdinalIgnoreCase)
                .ThenBy(state => state.ServerDisplayName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
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
        if (!string.IsNullOrWhiteSpace(snapshot.InstanceId))
        {
            var sample = MetricSampleFactory.FromSnapshot(snapshot);
            _metricsStore.Enqueue(snapshot.InstanceId, in sample);
        }

        NotifyChanged();
    }

    public void UpdateCommandResult(ServerCommandResult result)
    {
        ServerCommandEnvelope? command = null;

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
            }
        }

        if (command is not null)
            _knownPlayers.ApplyCommandOutcome(command, result);

        NotifyChanged();
    }

    public void MarkDisconnected(string connectionId)
    {
        var changed = false;
        var disconnectedAgentIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

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
                }
            }
        }

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
            InstanceId = command.InstanceId,
            AgentId = command.AgentId,
            ServerId = command.ServerId,
            CommandType = command.CommandType,
            Text = command.Text,
            SteamId = command.SteamId,
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

    public string InstanceKey => Snapshot?.InstanceId ?? Hello?.InstanceId ?? ServerKey;

    public string NodeKey => Snapshot?.NodeId ?? Hello?.NodeId ?? string.Empty;

    public string ServerKey => Snapshot?.ServerId ?? Hello?.ServerId ?? AgentId;

    public string NodeDisplayName => Snapshot?.NodeName ?? Hello?.NodeName ?? "Unknown node";

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
