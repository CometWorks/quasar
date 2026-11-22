using System.Text.Json;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Client.Clusters;

// Carry inner JSON as text across JS interop to preserve 64-bit grid, revision and partition identifiers.
public sealed record ClusterPanelRequest(string UniqueName, string? Data = null)
{
    public static ClusterPanelRequest For<T>(string uniqueName, T args) => new(uniqueName, JsonSerializer.Serialize(args));
    public T Arguments<T>() => JsonSerializer.Deserialize<T>(Data ?? "null") ?? throw new JsonException("Missing cluster operation arguments.");
}

public sealed record PartitionTarget(string Node, ulong Partition, double Hours = 0);
public sealed record LobbyBlueprintTarget(long ExpectedRevision, Admin.LobbyPosition Position);
public sealed record PanelOperation(string OperationId, string Kind, string State, PanelOperationError? Error);
public sealed record PanelOperationError(string Code, string Message);

internal static class ClusterPanelSdk
{
    internal static async Task<T> ReadClusterAsync<T>(this DashboardSdk sdk, string operation, string cluster,
        CancellationToken token = default, object? args = null)
    {
        var response = await sdk.ReadTextAsync(operation, ClusterPanelRequest.For(cluster, args), token);
        return JsonSerializer.Deserialize<T>(response) ?? throw new JsonException("The cluster response was empty.");
    }

    internal static bool SupportsLobby(Admin.AdminCapabilities capabilities, bool manage = false) =>
        capabilities.Features?.TryGetValue("lobbyAnchor", out var version) == true && version == "v1"
        && capabilities.Operations?.Contains(manage ? "PUT /admin/v1/lobby" : "GET /admin/v1/lobby", StringComparer.Ordinal) == true;

    internal static bool SupportsOffline(Admin.AdminCapabilities capabilities) =>
        capabilities.Features?.TryGetValue("offlinePartitions", out var version) == true && version == "v1";
}

public sealed record PanelCommand(string Action, JsonElement? Parameters = null, string? Target = null)
{
    public static PanelCommand Create(string action, object? parameters = null, string? target = null) =>
        new(action, parameters is null ? null : JsonSerializer.SerializeToElement(parameters, new JsonSerializerOptions(JsonSerializerDefaults.Web)
            { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } }), target);
}
