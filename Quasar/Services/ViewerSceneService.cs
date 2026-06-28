using System.Text.Json;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;

namespace Quasar.Services;

/// <summary>
/// Requests render scenes from connected agents. This service never exposes Space
/// Engineers assets, raw model bytes, or textures. Optional voxel geometry is
    /// bounded to the selected grid or requested context scene.
/// </summary>
public sealed class ViewerSceneService
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan SceneTimeout = TimeSpan.FromSeconds(30);

    private readonly AgentRegistry _registry;

    public ViewerSceneService(AgentRegistry registry)
    {
        _registry = registry;
    }

    public async Task<EntityRenderScene> GetEntitySceneAsync(
        string agentId,
        long entityId,
        bool includeVoxels,
        bool includeContext,
        CancellationToken cancellationToken = default)
    {
        var agent = _registry.GetAgents().FirstOrDefault(candidate =>
            candidate.IsConnected &&
            string.Equals(candidate.AgentId, agentId, StringComparison.OrdinalIgnoreCase));
        if (agent is null)
            throw new InvalidOperationException("The selected server is not connected.");

        var payload = JsonSerializer.Serialize(new EntityRenderSceneRequest
        {
            EntityId = entityId,
            IncludeVoxels = includeVoxels,
            IncludeContext = includeContext,
        }, JsonOptions);
        var command = new ServerCommandEnvelope
        {
            UniqueName = agent.UniqueNameKey,
            AgentId = agent.AgentId,
            ServerId = agent.ServerKey,
            CommandType = ServerCommandType.GetEntityRenderScene,
            Payload = payload,
        };

        var result = await _registry.SendCommandAndWaitAsync(command, SceneTimeout, cancellationToken);
        if (!result.Success)
        {
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Message)
                ? "The agent could not capture the viewer scene."
                : result.Message);
        }

        if (string.IsNullOrWhiteSpace(result.Payload))
            throw new InvalidOperationException("The agent returned an empty viewer scene.");

        return JsonSerializer.Deserialize<EntityRenderScene>(result.Payload, JsonOptions)
               ?? throw new InvalidOperationException("The agent returned an invalid viewer scene.");
    }
}
