using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Magnetar.Protocol.Runtime;
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
    private readonly string _snapshotDirectory;
    private readonly object _sync = new();
    private readonly Dictionary<string, (string ConnectionId, List<PluginConfigData> Plugins)> _byAgent = new(StringComparer.OrdinalIgnoreCase);

    public PluginConfigService(AgentRegistry registry, ILogger<PluginConfigService> logger)
        : this(registry, logger, Path.Combine(MagnetarPaths.GetQuasarDirectory(), "PluginConfigSnapshots")) { }

    internal PluginConfigService(AgentRegistry registry, ILogger<PluginConfigService> logger, string snapshotDirectory)
    {
        _registry = registry;
        _logger = logger;
        _snapshotDirectory = snapshotDirectory;
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
            // Recheck after acquiring the cache lock: queued messages from replaced
            // connections must not overwrite a newer snapshot.
            if (!_registry.IsCurrentConnection(snapshot.AgentId, connectionId)) return;
            var plugins = (snapshot.Plugins ?? []).Select(Clone).ToList();
            _byAgent[snapshot.AgentId] = (connectionId, plugins);
            if (_registry.TryGetUniqueName(connectionId, out var server))
            {
                var stored = new LastKnownPluginConfigSnapshot(server, snapshot.AgentId, DateTimeOffset.UtcNow, plugins.ToArray());
                try { Persist(stored); }
                catch (Exception error) when (error is IOException or UnauthorizedAccessException)
                {
                    // Config values can contain credentials; never include payloads or exception details.
                    _logger.LogWarning("Could not preserve plugin configuration snapshot for conversion.");
                }
            }
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

    /// <summary>Historical, authenticated standalone snapshot; never evidence of a live connection.</summary>
    public LastKnownPluginConfigSnapshot? GetLastKnownConfigsForServer(string uniqueName)
    {
        if (string.IsNullOrWhiteSpace(uniqueName)) return null;
        lock (_sync)
        {
            var snapshot = ReadSnapshot(SnapshotPath(uniqueName));
            return string.Equals(snapshot?.ServerUniqueName, uniqueName, StringComparison.OrdinalIgnoreCase) ? snapshot : null;
        }
    }

    /// <summary>Historical standalone snapshot for an Agent incarnation, including its capture time.</summary>
    public LastKnownPluginConfigSnapshot? GetLastKnownConfigsForAgent(string agentId)
    {
        if (string.IsNullOrWhiteSpace(agentId)) return null;
        lock (_sync)
        {
            if (!Directory.Exists(_snapshotDirectory)) return null;
            return Directory.EnumerateFiles(_snapshotDirectory, "*.json")
                .Select(ReadSnapshot).Where(s => s is not null && string.Equals(s.AgentId, agentId, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(s => s!.CapturedAtUtc).FirstOrDefault();
        }
    }

    private string SnapshotPath(string server) => Path.Combine(_snapshotDirectory,
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(server.ToUpperInvariant()))) + ".json");

    private static LastKnownPluginConfigSnapshot? ReadSnapshot(string path)
    {
        if (!File.Exists(path)) return null;
        return JsonSerializer.Deserialize<LastKnownPluginConfigSnapshot>(File.ReadAllBytes(path))
            ?? throw new InvalidDataException("Stored plugin configuration snapshot is empty.");
    }

    private void Persist(LastKnownPluginConfigSnapshot snapshot)
    {
        // Atomic replacement, with private directory and file modes because configuration may contain secrets.
        if (OperatingSystem.IsWindows()) Directory.CreateDirectory(_snapshotDirectory);
        else Directory.CreateDirectory(_snapshotDirectory, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        string destination = SnapshotPath(snapshot.ServerUniqueName);
        string temporary = destination + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            var options = new FileStreamOptions { Mode = FileMode.CreateNew, Access = FileAccess.Write, Share = FileShare.None };
            if (!OperatingSystem.IsWindows()) options.UnixCreateMode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
            using (var file = new FileStream(temporary, options))
            {
                JsonSerializer.Serialize(file, snapshot);
                file.Flush(flushToDisk: true);
            }
            File.Move(temporary, destination, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void NotifyChanged() => Changed?.Invoke();

    private static PluginConfigData Clone(PluginConfigData data) => new()
    {
        PluginId = data.PluginId,
        DisplayName = data.DisplayName,
        ConfigType = data.ConfigType,
        ConfigJson = data.ConfigJson,
    };
}

/// <summary>A reviewable historical snapshot, not live Agent state.</summary>
public sealed record LastKnownPluginConfigSnapshot(string ServerUniqueName, string AgentId, DateTimeOffset CapturedAtUtc, PluginConfigData[] Plugins);
