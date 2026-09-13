using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;

namespace Quasar.Services.PluginSdk;

/// <summary>
/// Caches the plugin configurations reported by connected agents and routes
/// edits back to them. Mirrors the Discord catalog/service pattern: a hosted
/// service that subscribes to <see cref="AgentRegistry"/> changes, holds state
/// keyed by agent id, and raises <see cref="Changed"/> for Blazor reactivity.
/// </summary>
public sealed class PluginConfigService : IHostedService
{
    private readonly AgentRegistry _registry;
    private readonly ILogger<PluginConfigService> _logger;
    private readonly object _sync = new();
    private readonly Dictionary<string, (string ConnectionId, List<PluginConfigData> Plugins)> _byAgent = new(StringComparer.OrdinalIgnoreCase);

    public PluginConfigService(AgentRegistry registry, ILogger<PluginConfigService> logger)
    {
        _registry = registry;
        _logger = logger;
    }

    public event Action? Changed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        _registry.Changed += HandleRegistryChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        _registry.Changed -= HandleRegistryChanged;
        return Task.CompletedTask;
    }

    /// <summary>
    /// Records the configs reported by an agent. Called from the agent
    /// WebSocket handler when a <c>plugin-config-snapshot</c> arrives.
    /// </summary>
    public void IngestSnapshot(PluginConfigSnapshot snapshot, string connectionId)
    {
        if (snapshot is null || !_registry.IsCurrentConnection(snapshot.AgentId, connectionId))
            return;

        lock (_sync)
        {
            _byAgent[snapshot.AgentId] = (connectionId, snapshot.Plugins ?? new List<PluginConfigData>());
        }

        _logger.LogDebug("Ingested {Count} plugin config(s) from agent {AgentId}.",
            snapshot.Plugins?.Count ?? 0, snapshot.AgentId);

        NotifyChanged();
    }

    /// <summary>All configurable plugins for the given agent (empty if unknown).</summary>
    public IReadOnlyList<PluginConfigData> GetConfigsForAgent(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return Array.Empty<PluginConfigData>();

        lock (_sync)
        {
            return _byAgent.TryGetValue(agentId, out var item) && _registry.IsCurrentConnection(agentId, item.ConnectionId)
                ? item.Plugins.Select(Clone).ToList()
                : Array.Empty<PluginConfigData>();
        }
    }

    /// <summary>Returns true when the given agent has reported at least one configurable plugin.</summary>
    public bool HasConfigs(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId))
            return false;

        lock (_sync)
        {
            return _byAgent.TryGetValue(agentId, out var item) && _registry.IsCurrentConnection(agentId, item.ConnectionId) && item.Plugins.Count > 0;
        }
    }

    /// <summary>Sends a new values document for a plugin back to its agent.</summary>
    public async Task UpdatePluginConfigAsync(
        string agentId,
        string pluginId,
        string valuesJson,
        string connectionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(agentId) || string.IsNullOrWhiteSpace(pluginId))
            return;

        await _registry.SendToAgentAsync(agentId, new AgentWireMessage
        {
            Kind = WireMessageKind.PluginConfigUpdate,
            PluginConfigUpdateRequest = new PluginConfigUpdateRequest
            {
                PluginId = pluginId,
                ValuesJson = valuesJson ?? string.Empty,
            },
        }, cancellationToken, expectedConnectionId: connectionId);
    }

    private void HandleRegistryChanged()
    {
        // Drop cached configs for agents that are no longer connected so the
        // editor does not show stale state from a disconnected server.
        var connected = new HashSet<string>(
            _registry.GetAgents().Where(agent => agent.IsConnected).Select(agent => agent.AgentId),
            StringComparer.OrdinalIgnoreCase);

        var removed = false;
        lock (_sync)
        {
            foreach (var agentId in _byAgent.Keys.Where(id => !connected.Contains(id)).ToList())
            {
                _byAgent.Remove(agentId);
                removed = true;
            }
        }

        if (removed)
            NotifyChanged();
    }

    private void NotifyChanged() => Changed?.Invoke();

    private static PluginConfigData Clone(PluginConfigData data) => new()
    {
        PluginId = data.PluginId,
        DisplayName = data.DisplayName,
        ConfigJson = data.ConfigJson,
    };
}
