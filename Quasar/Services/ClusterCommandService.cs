using System.Text.Json;
using System.Text.Json.Serialization;
using Quasar.Models;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services;

public sealed class ClusterCommandService
{
    private readonly ClusterCatalog _catalog;
    private readonly ClusterOperationStore _operations;
    private readonly ClusterGatewayClient _client;
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) },
        RespectRequiredConstructorParameters = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
    };

    public ClusterCommandService(ClusterCatalog catalog, ClusterOperationStore operations, ClusterGatewayClient client)
    {
        _catalog = catalog;
        _operations = operations;
        _client = client;
    }

    public Task<ClusterOperation> SetGoalAsync(string uniqueName, ClusterGoalRequest request,
        string idempotencyKey, string actor, CancellationToken cancellationToken = default)
    {
        var cluster = _catalog.GetCluster(uniqueName) ?? throw Invalid("unknown_cluster", "Cluster was not found.");
        if (cluster.Gateway is null || string.IsNullOrWhiteSpace(cluster.HostCommandUrl))
            throw Invalid("lifecycle_unconfigured", "This cluster is observed only; configure process lifecycle before changing its goal.");
        if (!Enum.IsDefined(request.Goal)) throw Invalid("invalid_goal", "Cluster goal must be On or Off.");
        return _operations.ExecuteAsync(uniqueName, "cluster.goal.set", idempotencyKey, actor, request,
            async token =>
            {
                ClusterDefinition updated = await _catalog.SetGoalStateAsync(uniqueName, request.Goal, token);
                return new Admin.AdminEnvelope<ClusterGoalResult>(Admin.AdminProtocol.Version,
                    DateTimeOffset.UtcNow,
                    new ClusterGoalResult(updated.UniqueName, updated.GoalState, updated.UpdatedAtUtc));
            }, cancellationToken);
    }

    public Task<ClusterOperation> SubmitAsync(string uniqueName, ClusterAdminCommand command,
        string idempotencyKey, string actor, CancellationToken token = default)
    {
        if (_catalog.GetCluster(uniqueName) is null) throw Invalid("unknown_cluster", "Cluster was not found.");
        return _catalog.WithLifecycleAsync(uniqueName,
            cluster => SubmitCoreAsync(cluster, command, idempotencyKey, actor, token), token);
    }

    private Task<ClusterOperation> SubmitCoreAsync(ClusterDefinition cluster, ClusterAdminCommand command,
        string idempotencyKey, string actor, CancellationToken token)
    {
        if (cluster.Update is { Phase: not ClusterUpdatePhase.Complete } || cluster.PendingDeploymentHash is not null || cluster.PendingRestoreHash is not null)
            throw Invalid("deployment_pending", "Complete the managed update or restore before submitting cluster commands.");
        string action = command.Action?.Trim().ToLowerInvariant() ?? "";
        string method = "POST";
        string route;
        object body;
        try
        {
            switch (action)
            {
                case "save-all": route = "save-all"; body = Read(new Admin.SaveAllRequest()); break;
                case "shutdown":
                    route = "shutdown";
                    var shutdown = Read(new Admin.ShutdownRequest());
                    if (!double.IsFinite(shutdown.GraceSeconds) || !double.IsFinite(shutdown.ForceAfterSeconds)
                        || shutdown.GraceSeconds is < 0 or > 3600 || shutdown.ForceAfterSeconds is < 0 or > 3600)
                        throw Invalid("invalid_shutdown", "Shutdown timing must be between 0 and 3600 seconds.");
                    body = shutdown; break;
                case "gateway-restart":
                    if (cluster.Gateway is null || cluster.GoalState != DedicatedServerGoalState.On)
                        throw Invalid("gateway_restart_unconfigured", "Gateway restart needs a configured running supervisor.");
                    if (_operations.HasPendingShutdown(cluster.UniqueName))
                        throw Invalid("shutdown_pending", "Wait for the pending shutdown before restarting the Gateway.");
                    route = "gateway/restart"; body = new { }; break;
                case "config-set":
                    method = "PUT"; route = "config";
                    var config = Read<Admin.AdminConfigUpdate>();
                    if (config.ExpectedRevision < 0 || config.Slots is null || config.Slots.Any(slot => slot is null
                        || string.IsNullOrWhiteSpace(slot.SlotKey) || string.IsNullOrWhiteSpace(slot.Host)
                        || !Enum.IsDefined(slot.Role) || !Enum.IsDefined(slot.Goal)))
                        throw Invalid("invalid_config", "Configuration needs a revision and valid node slots.");
                    body = config; break;
                case "node-close": route = "nodes/" + Target() + "/close"; body = Read(new Admin.CloseNodeRequest()); break;
                case "node-kill":
                    route = "nodes/" + Target() + "/kill";
                    var kill = Read<Admin.KillNodeRequest>();
                    if (string.IsNullOrWhiteSpace(kill.Node) || kill.Epoch <= 0)
                        throw Invalid("invalid_incarnation", "Force removal requires an exact node and positive epoch.");
                    body = kill; break;
                case "wa-move": route = "world-authority/move"; body = Read(new Admin.WorldAuthorityMoveRequest()); break;
                case "kick": route = "clients/" + PlayerTarget() + "/kick"; body = Read(new Admin.KickClientRequest()); break;
                case "ban": route = "admission/bans"; body = new Admin.BanRequest(PlayerTarget()); break;
                case "unban": method = "DELETE"; route = "admission/bans/" + PlayerTarget(); body = new { }; break;
                case "chat":
                    route = "chat";
                    var chat = Read<Admin.ChatSendRequest>();
                    if (chat.Sender == 0 || string.IsNullOrWhiteSpace(chat.Message) || chat.Message.Length > 4096)
                        throw Invalid("invalid_chat", "Chat requires a sender identity and a message of 1–4096 characters.");
                    body = chat; break;
                case "world-export":
                    var export = Read(new Admin.WorldExportRequest());
                    if (export.TtlHours is { } ttl && (!double.IsFinite(ttl) || ttl <= 0 || ttl > 168))
                        throw Invalid("invalid_export", "Export retention must be between zero and 168 hours.");
                    route = "world-exports"; body = export; break;
                case "artifact-release": method = "DELETE"; route = "artifacts/" + Target(); body = new { }; break;
                case "handover-config-set":
                    var values = Read<Dictionary<string, string?>>();
                    if (values.Count is < 1 or > 256 || values.Any(p => string.IsNullOrWhiteSpace(p.Key) || p.Key.Length > 128 || p.Value?.Length > 512))
                        throw Invalid("invalid_config", "Handover settings exceed supported limits.");
                    method = "PUT"; route = "handover-config"; body = values; break;
                case "trigger":
                    if (command.Target is not ("balance" or "save" or "split-sweep" or "membership-eval" or "conceal-eval" or "compact" or "catalog-gc" or "rotation-check"))
                        throw Invalid("invalid_trigger", "Unknown maintenance task.");
                    var scope = Read(new ClusterMaintenanceScope());
                    if (scope.Node?.Length > 200 || scope.Partition == 0) throw Invalid("invalid_scope", "Invalid maintenance scope.");
                    route = "triggers/" + command.Target + "?unthrottled=" + (scope.Unthrottled ? "true" : "false")
                        + (scope.Node is null ? "" : "&node=" + Uri.EscapeDataString(scope.Node))
                        + (scope.Partition is null ? "" : "&partition=" + scope.Partition.Value);
                    body = new { }; break;
                default: throw Invalid("unsupported_command", "Unknown cluster command.");
            }
        }
        catch (JsonException error) { throw Invalid("invalid_arguments", error.Message); }
        return _operations.ExecuteGatewayAsync(cluster, "cluster." + action, method, route, body,
            idempotencyKey, actor, _client, token);

        T Read<T>(T? fallback = default) where T : class
        {
            if (command.Parameters is not { ValueKind: not JsonValueKind.Null and not JsonValueKind.Undefined } json)
                return fallback ?? throw Invalid("invalid_arguments", "Command parameters are required.");
            return json.Deserialize<T>(JsonOptions) ?? throw Invalid("invalid_arguments", "Command parameters are required.");
        }
        string Target() => string.IsNullOrWhiteSpace(command.Target) || command.Target.Length > 256
            ? throw Invalid("invalid_target", "A target is required (maximum 256 characters).") : Uri.EscapeDataString(command.Target);
        ulong PlayerTarget() => ulong.TryParse(command.Target, out ulong id) && id > 0
            ? id : throw Invalid("invalid_target", "A numeric player identifier is required.");
    }

    private static ClusterOperationConflictException Invalid(string code, string message) => new(400, code, message);
}

public sealed record ClusterGoalRequest(
    [property: System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<DedicatedServerGoalState>))]
    DedicatedServerGoalState Goal);
public sealed record ClusterGoalResult(string ClusterId,
    [property: System.Text.Json.Serialization.JsonConverter(typeof(System.Text.Json.Serialization.JsonStringEnumConverter<DedicatedServerGoalState>))]
    DedicatedServerGoalState Goal, DateTimeOffset UpdatedAt);

public sealed record ClusterAdminCommand(string Action, JsonElement? Parameters = null, string? Target = null)
{
    public static ClusterAdminCommand Create(string action, object? parameters = null, string? target = null) =>
        new(action, parameters is null ? null : JsonSerializer.SerializeToElement(parameters,
            new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new JsonStringEnumConverter() } }), target);
}

public sealed record ClusterMaintenanceScope(string? Node = null, ulong? Partition = null, bool Unthrottled = false);
