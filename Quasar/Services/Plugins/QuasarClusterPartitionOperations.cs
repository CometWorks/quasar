using System.Text;
using System.Text.Json;
using Quasar.Client.Clusters;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using Admin = CometWorks.ClusterGateway.AdminContract.V1;

namespace Quasar.Services.Plugins;

internal static partial class QuasarClusterDetailOperations
{
    private static readonly string[] Maintenance = [QuasarPolicyNames.ClusterQuery, QuasarPolicyNames.ClusterMaintenance];
    private static readonly string[] Administer = [QuasarPolicyNames.ClusterQuery, QuasarPolicyNames.ClusterMaintenance, QuasarPolicyNames.ClusterManage];

    private static void AddClusterPartitions(this IServiceCollection services)
    {
        RegisterRead(services, "cluster.partitions", "Lists partitions in the selected cluster.", Query, (g, c, _, t) => g.GetPartitionsAsync(c, t));
        RegisterRead(services, "cluster.capabilities", "Lists the selected gateway's supported operations.", Query, (g, c, _, t) => g.GetCapabilitiesAsync(c, t));
        RegisterRead(services, "cluster.offline.catalog", "Lists offline partitions known to the cluster gateway.", Query, (g, c, _, t) => g.GetOfflinePartitionsAsync(c, t));
        RegisterRead(services, "cluster.offline.cache", "Returns cached offline partition data.", Query, (g, c, _, t) => g.GetOfflineCacheAsync(c, t));
        RegisterRead(services, "cluster.lobby", "Returns the configured lobby and its revision.", Query, (g, c, _, t) => g.GetLobbyAsync(c, t));
        RegisterRead(services, "cluster.offline.details", "Returns details of the selected offline partition.", Query, (gateway, cluster, input, token) =>
        {
            var target = input.Arguments<PartitionTarget>();
            return gateway.GetOfflineDetailsAsync(cluster, target.Node, target.Partition, token);
        });
        RegisterRead(services, "cluster.offline.predict", "Previews the selected offline partition's behavior.", Maintenance, (gateway, cluster, input, token) =>
        {
            var target = input.Arguments<PartitionTarget>();
            return gateway.GetOfflinePredictionAsync(cluster, target.Node, target.Partition, target.Hours, token);
        });
        RegisterCommand(services, "cluster.offline.profile", "Updates the selected offline partition profile.", Maintenance, input =>
        {
            var target = input.Arguments<PartitionTarget>();
            ClusterGatewayClient.ValidateOfflineTarget(target.Node, target.Partition);
            return ClusterAdminCommand.Create("trigger", new ClusterMaintenanceScope(target.Node, target.Partition), "profile");
        });
        RegisterCommand(services, "cluster.offline.clear", "Clears the selected offline partition cache.", Maintenance, input =>
            ClusterAdminCommand.Create("offline-cache-clear", input.Arguments<Admin.OfflineCacheClearRequest>()));
        RegisterCommand(services, "cluster.lobby.set", "Submits a revision-checked lobby update.", Administer, input =>
        {
            var update = input.Arguments<Admin.LobbyUpdate>();
            if (update.BlueprintXml is not null || update.Position is not null)
                throw new QuasarOperationException(400, "Upload the blueprint file to create a station.");
            return LobbyCommand(update);
        });
        services.AddQuasarUpload<ClusterPanelRequest, PanelOperation>(Plugin, "cluster.lobby.upload", Administer, Admin.LobbyUpdate.MaxBlueprintBytes,
            (caller, input, stream, _) =>
            {
                var cluster = Cluster(caller, input.UniqueName);
                var target = input.Arguments<LobbyBlueprintTarget>();
                return Task.FromResult(new QuasarUpload<PanelOperation>(async token =>
                {
                    using var reader = new StreamReader(stream, new UTF8Encoding(false, true), leaveOpen: true);
                    string blueprint;
                    try { blueprint = await reader.ReadToEndAsync(token); }
                    catch (DecoderFallbackException) { throw new QuasarOperationException(400, "Choose a UTF-8 grid blueprint."); }
                    await caller.EnsureAuthorizedAsync(token);
                    cluster = Cluster(caller, input.UniqueName);
                    var command = LobbyCommand(new(target.ExpectedRevision, BlueprintXml: blueprint, Position: target.Position));
                    return await Submit(caller, cluster, command, token);
                }));
            }, Change with { Description = "Uploads a lobby blueprint against the reviewed revision." });
        services.AddQuasarOperation<ClusterPanelRequest, PanelOperation>(Plugin, "cluster.panel.operation", Query, (caller, input, _) =>
        {
            var cluster = Cluster(caller, input.UniqueName);
            var operation = caller.Services.GetRequiredService<ClusterOperationStore>().Get(input.Arguments<string>());
            if (operation is null || !string.Equals(operation.Cluster, cluster.UniqueName, StringComparison.OrdinalIgnoreCase))
                throw new QuasarOperationException(404, "Operation not found.");
            return Task.FromResult(Project(operation));
        }, Read with { Description = "Returns the caller-visible status of a cluster operation by ID." });
    }

    private static void RegisterRead<T>(IServiceCollection services, string name, string description, string[] policies,
        Func<ClusterGatewayClient, ClusterDefinition, ClusterPanelRequest, CancellationToken, Task<Admin.AdminEnvelope<T>>> read)
    {
        services.AddQuasarRead<ClusterPanelRequest>(Plugin, name, policies, async (caller, input, token) =>
        {
            var cluster = Cluster(caller, input.UniqueName);
            try
            {
                var response = await read(caller.Services.GetRequiredService<ClusterGatewayClient>(), cluster, input, token);
                return JsonRead(response.Data);
            }
            catch (ClusterGatewayException e) { throw new QuasarOperationException((int)e.StatusCode, e.Code + ": " + e.Message); }
        }, Read with { Description = description });
    }

    private static QuasarRead JsonRead<T>(T data)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(data);
        return new(_ => Task.FromResult<Stream>(new MemoryStream(bytes, false)), "application/json", Length: bytes.Length);
    }

    private static void RegisterCommand(IServiceCollection services, string name, string description, string[] policies, Func<ClusterPanelRequest, ClusterAdminCommand> command) =>
        services.AddQuasarOperation<ClusterPanelRequest, PanelOperation>(Plugin, name, policies, (caller, input, token) =>
            Submit(caller, Cluster(caller, input.UniqueName), command(input), token), Change with { Description = description });

    private static ClusterAdminCommand LobbyCommand(Admin.LobbyUpdate update) =>
        update.ValidationError() is { } error ? throw new QuasarOperationException(400, error) : ClusterAdminCommand.Create("lobby-set", update);

    private static async Task<PanelOperation> Submit(QuasarOperationContext caller, ClusterDefinition cluster, ClusterAdminCommand command, CancellationToken token)
    {
        try
        {
            var operation = await caller.Services.GetRequiredService<ClusterCommandService>().SubmitAsync(cluster.UniqueName, command,
                Guid.NewGuid().ToString("N"), caller.User.Identity?.Name ?? "web-ui", token);
            return Project(operation);
        }
        catch (ClusterGatewayException e) { throw new QuasarOperationException((int)e.StatusCode, e.Code + ": " + e.Message); }
        catch (ClusterOperationConflictException e) { throw new QuasarOperationException(e.StatusCode, e.Message); }
        catch (ClusterOperationStoreUnavailableException e) { throw new QuasarOperationException(503, e.Message); }
    }

    private static PanelOperation Project(ClusterOperation operation) =>
        new(operation.OperationId, operation.Kind, operation.State.ToString(), operation.Error is { } e ? new(e.Code, e.Message) : null);
}
