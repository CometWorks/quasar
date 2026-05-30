using System.Text.Json;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;

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

    public EntityService(AgentRegistry registry)
    {
        _registry = registry;
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
            return new EntityListResult();

        return JsonSerializer.Deserialize<EntityListResult>(result.Payload, JsonOptions) ?? new EntityListResult();
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
}
