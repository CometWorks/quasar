using System.Text.Json;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Quasar.Plugin.Abstractions.Companion;

namespace Quasar.Services;

public sealed class QuasarCompanionChannel : IQuasarCompanionChannel
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(30);

    private readonly AgentRegistry _registry;

    public QuasarCompanionChannel(AgentRegistry registry)
    {
        _registry = registry;
    }

    public async Task<TResponse> SendAsync<TRequest, TResponse>(
        string serverId,
        string companionPluginId,
        string operation,
        TRequest request,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(serverId))
            throw new ArgumentException("Server id is required.", nameof(serverId));
        if (string.IsNullOrWhiteSpace(companionPluginId))
            throw new ArgumentException("Companion plugin id is required.", nameof(companionPluginId));
        if (string.IsNullOrWhiteSpace(operation))
            throw new ArgumentException("Companion operation is required.", nameof(operation));

        var agent = ResolveConnectedAgent(serverId.Trim())
                    ?? throw new InvalidOperationException($"No connected agent matched server id '{serverId}'.");
        var correlationId = Guid.NewGuid().ToString("N");
        var companionRequest = new CompanionPluginRequest
        {
            PluginId = companionPluginId.Trim(),
            Operation = operation.Trim(),
            CorrelationId = correlationId,
            PayloadJson = JsonSerializer.Serialize(request, JsonOptions),
        };

        var command = new ServerCommandEnvelope
        {
            UniqueName = agent.UniqueNameKey,
            AgentId = agent.AgentId,
            ServerId = agent.ServerKey,
            CommandType = ServerCommandType.PluginRequest,
            Payload = JsonSerializer.Serialize(companionRequest, JsonOptions),
        };

        var result = await _registry.SendCommandAndWaitAsync(command, RequestTimeout, cancellationToken);
        if (!result.Success)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(result.Message)
                ? "The companion plugin request failed."
                : result.Message);

        if (string.IsNullOrWhiteSpace(result.Payload))
            throw new InvalidOperationException("The companion plugin returned an empty response.");

        var companionResponse = JsonSerializer.Deserialize<CompanionPluginResponse>(result.Payload, JsonOptions)
                                ?? throw new InvalidOperationException("The companion plugin returned an invalid response.");
        if (!string.Equals(companionResponse.CorrelationId, correlationId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The companion plugin returned a mismatched correlation id.");
        if (!companionResponse.Success)
            throw new InvalidOperationException(string.IsNullOrWhiteSpace(companionResponse.Error)
                ? "The companion plugin request failed."
                : companionResponse.Error);
        if (string.IsNullOrWhiteSpace(companionResponse.PayloadJson))
            return default!;

        return JsonSerializer.Deserialize<TResponse>(companionResponse.PayloadJson, JsonOptions)
               ?? throw new InvalidOperationException("The companion plugin returned an invalid payload.");
    }

    private AgentRuntimeState? ResolveConnectedAgent(string serverId)
    {
        var agents = _registry.GetAgents().Where(agent => agent.IsConnected).ToList();

        return agents.FirstOrDefault(agent => string.Equals(agent.AgentId, serverId, StringComparison.OrdinalIgnoreCase))
               ?? agents.FirstOrDefault(agent => string.Equals(agent.ServerKey, serverId, StringComparison.OrdinalIgnoreCase))
               ?? agents.FirstOrDefault(agent => string.Equals(agent.UniqueNameKey, serverId, StringComparison.OrdinalIgnoreCase));
    }
}
