using System.Text.Json;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Quasar.Models;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services;

/// <summary>
/// Issues live entity queries and deletions to a connected agent over the existing
/// command/result WebSocket channel and marshals the structured JSON payloads.
/// </summary>
public sealed class EntityService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan QueryTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan DeleteTimeout = TimeSpan.FromSeconds(15);

    private readonly AgentRegistry _registry;
    private readonly ClusterGatewayClient? _gateway;

    public EntityService(AgentRegistry registry, ClusterGatewayClient? gateway = null)
    {
        _registry = registry;
        _gateway = gateway;
    }

    public async Task<EntityQueryResult> QueryAsync(AgentRuntimeState agent, EntityListFilter filter, CancellationToken token = default)
    {
        var result = await GetEntitiesAsync(agent, filter, token);
        return new(result.Entities.Select(entity => new EntityQueryRow([new(agent, agent.TelemetryKey, entity)])).ToArray(),
            result.TotalCount, result.TotalEntityCount, result.CapturedAtUtc, [], 1, 1);
    }

    public async Task<EntityQueryResult> QueryClusterAsync(ClusterDefinition cluster, EntityListFilter filter,
        IProgress<string>? progress = null, CancellationToken token = default)
    {
        if (_gateway is null)
            throw new InvalidOperationException("Cluster entity discovery is unavailable.");
        progress?.Report("Checking current cluster nodes…");
        var status = await _gateway.GetStatusAsync(cluster, token);
        var nodes = status.Data.Nodes.Where(node => node.State == Admin.NodeState.Active).ToArray();
        var agents = _registry.GetAgents();
        var warnings = new List<string>();
        var sources = new List<AgentRuntimeState>();
        foreach (var node in nodes)
        {
            var agent = ClusterFleetService.MatchAgent(cluster.UniqueName, node, agents);
            if (!node.LeaseFresh || node.LeaseExpires <= DateTimeOffset.UtcNow || agent is not { IsConnected: true })
                warnings.Add($"{node.SlotKey ?? node.Node}: no verified, connected agent. Its entities are missing from this list.");
            else
                sources.Add(agent);
        }
        if (nodes.Length == 0)
            warnings.Add("No active cluster nodes are available. Only live, loaded entities can be listed.");

        using var gate = new SemaphoreSlim(4);
        var completed = 0;
        var results = await Task.WhenAll(sources.Select(async agent =>
        {
            await gate.WaitAsync(token);
            try
            {
                var result = await QueryAllPagesAsync(agent, filter, token);
                progress?.Report($"Checked {Interlocked.Increment(ref completed)} of {sources.Count} nodes.");
                return result;
            }
            finally { gate.Release(); }
        }));
        warnings.AddRange(results.SelectMany(result => result.Warnings));
        var rows = results.SelectMany(result => result.Entities)
            .GroupBy(observation => observation.Entity.EntityId)
            .Select(group => new EntityQueryRow(group.ToArray()))
            .OrderByDescending(row => row.Entity.SizeMeters).ThenBy(row => row.Entity.EntityId).ToArray();
        var conflicts = rows.Count(row => row.Observations.Count(source => source.Entity.ClusterResidence == "Local") > 1);
        if (conflicts > 0)
            warnings.Add($"{conflicts} entities have conflicting node ownership. Deletion is disabled for those entities; refresh after the cluster settles.");
        return new(rows, rows.Length, null, DateTimeOffset.UtcNow, warnings,
            results.Count(result => result.Warnings.Count == 0), nodes.Length);
    }

    private async Task<NodeQueryResult> QueryAllPagesAsync(AgentRuntimeState agent, EntityListFilter filter, CancellationToken token)
    {
        var entities = new Dictionary<long, EntityObservation>();
        var warnings = new List<string>();
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(token);
        deadline.CancelAfter(TimeSpan.FromMinutes(2));
        try
        {
            var offset = 0;
            int? total = null;
            do
            {
                var page = await GetEntitiesAsync(agent, new() { TypeTag = filter.TypeTag, Search = filter.Search, Offset = offset, Limit = 2000 }, deadline.Token);
                total ??= page.TotalCount;
                if (page.TotalCount != total)
                    warnings.Add($"{agent.ClusterSlot}: entities changed while loading. Refresh for an updated list.");
                var before = entities.Count;
                foreach (var entity in page.Entities)
                    entities.TryAdd(entity.EntityId, new(agent, agent.TelemetryKey, entity));
                if (entities.Count - before != page.Entities.Count)
                    warnings.Add($"{agent.ClusterSlot}: entities moved or changed while paging. Refresh for an updated list.");
                offset += page.Entities.Count;
                if (entities.Count == before && offset < total)
                    throw new InvalidOperationException("Entity paging stopped making progress.");
            } while (offset < total);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw; }
        catch (Exception error)
        {
            warnings.Add($"{agent.ClusterSlot}: entity discovery was incomplete ({(error is OperationCanceledException ? "query timed out" : error.Message)}).");
        }
        if (!IsCurrent(agent.AgentId, agent.TelemetryKey))
        {
            entities.Clear();
            warnings.Add($"{agent.ClusterSlot}: the node disconnected or was replaced while loading. Refresh to query its replacement.");
        }
        return new(entities.Values.ToArray(), warnings);
    }

    public bool IsCurrent(string agentId, string telemetryKey) => _registry.GetAgents().Any(agent => agent.IsConnected
        && agent.AgentId == agentId && agent.TelemetryKey == telemetryKey);

    public async Task<ServerCommandResult> DeleteEntityAsync(EntityQueryRow row, CancellationToken token = default)
    {
        var source = row.DeletionSource ?? throw new InvalidOperationException(row.DeleteUnavailableReason);
        if (!IsCurrent(source.Agent.AgentId, source.TelemetryKey))
            throw new InvalidOperationException("The entity's node disconnected or was replaced. Refresh the list before deleting.");
        return await DeleteEntityAsync(source.Agent, row.Entity.EntityId, token);
    }

    public async Task<EntityListResult> GetEntitiesAsync(
        AgentRuntimeState agent,
        EntityListFilter filter,
        CancellationToken cancellationToken = default)
    {
        var command = BuildCommand(agent, ServerCommandType.ListEntities, JsonSerializer.Serialize(filter, JsonOptions));
        var result = await _registry.SendCommandAndWaitAsync(command, QueryTimeout, cancellationToken);

        if (!result.Success)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Message)
                ? "The agent could not list entities."
                : result.Message);
        }

        if (string.IsNullOrWhiteSpace(result.Payload))
            throw new InvalidOperationException("The agent returned no entity data.");

        var page = JsonSerializer.Deserialize<EntityListResult>(result.Payload, JsonOptions);
        if (page?.Entities is null || page.TotalCount < 0)
            throw new InvalidOperationException("The agent returned invalid entity data.");
        return page;
    }

    public async Task<ServerCommandResult> DeleteEntityAsync(
        AgentRuntimeState agent,
        long entityId,
        CancellationToken cancellationToken = default)
    {
        var payload = JsonSerializer.Serialize(new EntityDeleteRequest { EntityId = entityId }, JsonOptions);
        var command = BuildCommand(agent, ServerCommandType.DeleteEntity, payload);
        return await _registry.SendCommandAndWaitAsync(command, DeleteTimeout, cancellationToken);
    }

    private static ServerCommandEnvelope BuildCommand(AgentRuntimeState agent, ServerCommandType commandType, string payload)
    {
        return new ServerCommandEnvelope
        {
            UniqueName = agent.UniqueNameKey,
            AgentId = agent.AgentId,
            ServerId = agent.ServerKey,
            CommandType = commandType,
            Payload = payload,
        };
    }

    private sealed record NodeQueryResult(IReadOnlyList<EntityObservation> Entities, IReadOnlyList<string> Warnings);
}

public sealed record EntityObservation(AgentRuntimeState Agent, string TelemetryKey, EntitySummary Entity);

public sealed record EntityQueryRow(IReadOnlyList<EntityObservation> Observations)
{
    public EntityObservation? DeletionSource
    {
        get
        {
            if (Observations.Count == 1 && !Observations[0].Agent.IsCluster)
                return Observations[0];
            var owners = Observations.Where(source => source.Entity.ClusterResidence == "Local").ToArray();
            return owners.Length == 1 ? owners[0] : null;
        }
    }
    public EntityObservation ViewingSource => DeletionSource ?? Observations[0];
    public EntitySummary Entity => ViewingSource.Entity;
    public string NodeLabel => string.Join(", ", Observations.Select(source =>
        source.Agent.ClusterSlot == "world-authority" ? "World Authority" : source.Agent.ClusterSlot).Distinct().Order());
    public string? DeleteUnavailableReason => DeletionSource is not null ? null
        : Observations.Any(source => source.Entity.ClusterResidence is null or "Unknown")
            ? "Entity ownership is unavailable. Update the node agents and refresh before deleting."
            : "No single node currently owns this entity. It may be shared or moving; refresh before deleting.";
}

public sealed record EntityQueryResult(IReadOnlyList<EntityQueryRow> Rows, int TotalCount, int? TotalEntityCount,
    DateTimeOffset CapturedAtUtc, IReadOnlyList<string> Warnings, int QueriedNodes, int ExpectedNodes);
