using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;

namespace Quasar.Services;

public sealed class AgentRegistry
{
    private readonly object _sync = new();
    private readonly Dictionary<string, AgentRuntimeState> _agents = new(StringComparer.OrdinalIgnoreCase);

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
        lock (_sync)
        {
            var state = GetOrCreateState(ResolveAgentId(snapshot.AgentId, connectionId));
            state.ConnectionId = connectionId;
            state.IsConnected = true;
            state.LastSeenUtc = DateTimeOffset.UtcNow;
            state.Snapshot = snapshot;
        }

        NotifyChanged();
    }

    public void UpdateCommandResult(ServerCommandResult result)
    {
        lock (_sync)
        {
            var state = GetOrCreateState(result.AgentId);
            state.LastSeenUtc = DateTimeOffset.UtcNow;
            state.CommandResults.Insert(0, result);
            if (state.CommandResults.Count > 20)
                state.CommandResults.RemoveRange(20, state.CommandResults.Count - 20);
        }

        NotifyChanged();
    }

    public void MarkDisconnected(string connectionId)
    {
        var changed = false;

        lock (_sync)
        {
            foreach (var state in _agents.Values.Where(state =>
                         string.Equals(state.ConnectionId, connectionId, StringComparison.OrdinalIgnoreCase)))
            {
                state.IsConnected = false;
                state.LastSeenUtc = DateTimeOffset.UtcNow;
                state.Sender = null;
                changed = true;
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
        }

        await sender(new AgentWireMessage
        {
            Kind = WireMessageKind.Command,
            Command = command,
        }, cancellationToken);
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
