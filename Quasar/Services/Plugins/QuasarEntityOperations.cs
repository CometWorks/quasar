using System.Globalization;
using Magnetar.Protocol.Model;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;
using static Quasar.Services.Plugins.QuasarCoreOperations;

namespace Quasar.Services.Plugins;

/// <summary>The Entities page's live entity list and deletion, for standalone servers and clusters.</summary>
public static class QuasarEntityOperations
{
    /// <param name="Type">All, Grid, Character, Float (dropped items), Voxel (asteroids and planets) or Other.</param>
    /// <param name="Search">Part of the entity's name, its owner's name or its entity ID.</param>
    /// <param name="OwnerSteamId">Only entities this SteamID64 owns, a grid's big owners included.</param>
    /// <param name="Sort">Size, Pcu or Blocks, largest first.</param>
    /// <param name="Limit">At most this many entities, 1 to 200.</param>
    public sealed record EntityListRequest(
        string UniqueName = "",
        string Type = "All",
        string Search = "",
        string OwnerSteamId = "",
        string Sort = "Size",
        int Limit = 25
    );

    /// <param name="EntityId">The entity ID, as a string.</param>
    public sealed record EntityRequest(string UniqueName = "", string EntityId = "");

    /// <param name="Details">Kind such as Large-grid ship, Player or Asteroid, when known.</param>
    /// <param name="Nodes">Cluster nodes the entity was seen on.</param>
    /// <param name="DeleteBlocked">Why the entity cannot be deleted now; null when it can.</param>
    public sealed record EntityItem(
        string EntityId,
        string Type,
        string? Details,
        string Name,
        int? Blocks,
        int? Pcu,
        int SizeMeters,
        decimal? Quantity,
        string? OwnerName,
        string? OwnerSteamId,
        string Position,
        string? Nodes,
        string? DeleteBlocked
    );

    /// <param name="Matching">Entities matching the type and search, before the owner filter and limit.</param>
    /// <param name="LiveTotal">All live entities of a standalone server.</param>
    /// <param name="Warnings">Why the list may be incomplete, such as unreachable cluster nodes.</param>
    public sealed record EntityList(
        string UniqueName,
        int Matching,
        int? LiveTotal,
        IReadOnlyList<EntityItem> Entities,
        IReadOnlyList<string> Warnings,
        DateTimeOffset CapturedAt
    );

    public sealed record EntityDeleteResult(string UniqueName, EntityItem Entity, string Message);

    private static readonly string[] Types =
    [
        "All",
        "Grid",
        "Character",
        "Float",
        "Voxel",
        "Other",
    ];
    private static readonly string[] Sorts = ["Size", "Pcu", "Blocks"];
    private const int MaxLimit = 200;

    // The agent caps a page at 2000 entities.
    private const int StandalonePage = 2000;
    private static readonly TimeSpan ClusterDeadline = TimeSpan.FromSeconds(150);

    public static void AddQuasarEntityOperations(this IServiceCollection services)
    {
        services.AddQuasarOperation<EntityListRequest, EntityList>(
            QuasarCoreOperations.PluginId,
            "entities.list",
            [QuasarPolicyNames.CanView],
            ListAsync,
            new()
            {
                Effect = QuasarOperationEffect.Read,
                Description =
                    "Lists a standalone server's live entities as the Entities page shows them (ID, type, name, blocks, PCU, size, owner, position), filtered by type, search text and owner, largest first by size, PCU or blocks. For a cluster use cluster.entities.list.",
            }
        );
        services.AddQuasarOperation<EntityRequest, EntityDeleteResult>(
            QuasarCoreOperations.PluginId,
            "entities.delete",
            [QuasarPolicyNames.CanControlServers],
            DeleteAsync,
            new()
            {
                Description =
                    "Permanently deletes one live entity, such as a grid, by its entity ID on a standalone server. It cannot be undone.",
            }
        );
        services.AddQuasarOperation<EntityListRequest, EntityList>(
            QuasarCoreOperations.PluginId,
            "cluster.entities.list",
            // The Entities page needs CanView, which a role override can narrow below ClusterQuery.
            [QuasarPolicyNames.CanView, QuasarPolicyNames.ClusterQuery],
            ListClusterAsync,
            new()
            {
                Effect = QuasarOperationEffect.Read,
                Deadline = ClusterDeadline,
                Description =
                    "Lists a cluster's live entities from every responding node, like entities.list, with the nodes each was seen on. Entities in offline partitions are not listed.",
            }
        );
        services.AddQuasarOperation<EntityRequest, EntityDeleteResult>(
            QuasarCoreOperations.PluginId,
            "cluster.entities.delete",
            [QuasarPolicyNames.CanControlServers, QuasarPolicyNames.ClusterManage],
            DeleteClusterAsync,
            new()
            {
                Deadline = ClusterDeadline,
                Description =
                    "Permanently deletes one live entity by its entity ID on a cluster, through the one node that owns it. Shared or moving entities are refused; it cannot be undone.",
            }
        );
    }

    private static async Task<EntityList> ListAsync(
        QuasarOperationContext caller,
        EntityListRequest input,
        CancellationToken token
    )
    {
        var (type, sort) = Validate(input);
        var agent = RequireAgent(caller.Services, input.UniqueName);
        var result = await Query(() =>
            caller
                .Services.GetRequiredService<EntityService>()
                .QueryAsync(agent, Filter(type, input.Search, StandalonePage), token)
        );
        var warnings = new List<string>();
        if (result.TotalCount > result.Rows.Count && (sort != "Size" || Owner(input) is not null))
            warnings.Add(
                $"Only the {result.Rows.Count} largest of {result.TotalCount} matching entities were checked; narrow the type or search."
            );
        return Shape(agent.UniqueNameKey, result, input, sort, warnings);
    }

    private static async Task<EntityList> ListClusterAsync(
        QuasarOperationContext caller,
        EntityListRequest input,
        CancellationToken token
    )
    {
        var (type, sort) = Validate(input);
        var cluster = RequireCluster(caller, input.UniqueName);
        var result = await Query(() =>
            caller
                .Services.GetRequiredService<EntityService>()
                .QueryClusterAsync(cluster, Filter(type, input.Search, 0), token: token)
        );
        var warnings = result.Warnings.Distinct().ToList();
        if (result.QueriedNodes < result.ExpectedNodes)
            warnings.Insert(
                0,
                $"{result.QueriedNodes} of {result.ExpectedNodes} nodes answered fully."
            );
        return Shape(cluster.UniqueName, result, input, sort, warnings);
    }

    private static async Task<EntityDeleteResult> DeleteAsync(
        QuasarOperationContext caller,
        EntityRequest input,
        CancellationToken token
    )
    {
        var id = ParseId(input.EntityId);
        var agent = RequireAgent(caller.Services, input.UniqueName);
        caller.Undo!.Server = agent.UniqueNameKey;
        var service = caller.Services.GetRequiredService<EntityService>();
        // The live entity by ID, so a proposal approved later never acts on a stale row.
        var result = await Query(() =>
            service.QueryAsync(
                agent,
                Filter("All", id.ToString(CultureInfo.InvariantCulture), StandalonePage),
                token
            )
        );
        var row =
            result.Rows.FirstOrDefault(candidate => candidate.Entity.EntityId == id)
            ?? throw new QuasarOperationException(
                404,
                $"Entity {id} not found on '{agent.UniqueNameKey}'."
            );
        return await RemoveAsync(service, agent.UniqueNameKey, row, token);
    }

    private static async Task<EntityDeleteResult> DeleteClusterAsync(
        QuasarOperationContext caller,
        EntityRequest input,
        CancellationToken token
    )
    {
        var id = ParseId(input.EntityId);
        var cluster = RequireCluster(caller, input.UniqueName);
        caller.Undo!.Server = cluster.UniqueName;
        var service = caller.Services.GetRequiredService<EntityService>();
        var result = await Query(() =>
            service.QueryClusterAsync(
                cluster,
                Filter("All", id.ToString(CultureInfo.InvariantCulture), 0),
                token: token
            )
        );
        var row = result.Rows.FirstOrDefault(candidate => candidate.Entity.EntityId == id);
        if (row is null)
            throw new QuasarOperationException(
                404,
                $"Entity {id} not found on the responding nodes of '{cluster.UniqueName}'."
                    + (
                        result.Warnings.Count > 0
                            ? " " + string.Join(" ", result.Warnings.Distinct())
                            : ""
                    )
            );
        if (row.DeleteUnavailableReason is { } reason)
            throw new QuasarOperationException(409, reason);
        return await RemoveAsync(service, cluster.UniqueName, row, token);
    }

    private static async Task<EntityDeleteResult> RemoveAsync(
        EntityService service,
        string uniqueName,
        EntityQueryRow row,
        CancellationToken token
    )
    {
        var item = Item(row);
        var result = await Query(() => service.DeleteEntityAsync(row, token));
        if (!result.Success)
            throw new QuasarOperationException(
                409,
                string.IsNullOrWhiteSpace(result.Message) ? "Delete failed." : result.Message
            );
        return new(
            uniqueName,
            item,
            string.IsNullOrWhiteSpace(result.Message) ? "Entity deleted." : result.Message
        );
    }

    // Agent and Gateway refusals are the operation declining the call, not a fault.
    private static async Task<T> Query<T>(Func<Task<T>> query)
    {
        try
        {
            return await query();
        }
        catch (InvalidOperationException error)
        {
            throw new QuasarOperationException(409, error.Message);
        }
        catch (ClusterGatewayException error)
        {
            throw new QuasarOperationException(502, error.Message);
        }
        catch (TimeoutException error)
        {
            throw new QuasarOperationException(504, error.Message);
        }
    }

    private static EntityList Shape(
        string uniqueName,
        EntityQueryResult result,
        EntityListRequest input,
        string sort,
        IReadOnlyList<string> warnings
    )
    {
        var owner = Owner(input);
        var rows = result.Rows.Where(row =>
            owner is null
            || row.Entity.OwnerSteamId == owner
            || row.Entity.BigOwnerSteamIds?.Contains(owner.Value) == true
        );
        rows = sort switch
        {
            "Pcu" => rows.OrderByDescending(row => row.Entity.Pcu ?? -1),
            "Blocks" => rows.OrderByDescending(row => row.Entity.BlockCount ?? -1),
            _ => rows.OrderByDescending(row => row.Entity.SizeMeters),
        };
        return new(
            uniqueName,
            result.TotalCount,
            result.TotalEntityCount,
            rows.Take(Math.Clamp(input.Limit, 1, MaxLimit)).Select(Item).ToList(),
            warnings,
            result.CapturedAtUtc
        );
    }

    private static EntityItem Item(EntityQueryRow row)
    {
        var entity = row.Entity;
        var details = EntityPresentation.Details(entity);
        var cluster = row.ViewingSource.Agent.IsCluster;
        return new(
            entity.EntityId.ToString(CultureInfo.InvariantCulture),
            EntityPresentation.TypeLabel(entity.TypeTag),
            details == "—" ? null : details,
            TextSanitizer.CleanGameText(EntityPresentation.Name(entity)),
            entity.BlockCount,
            entity.Pcu,
            (int)Math.Round(entity.SizeMeters),
            EntityPresentation.ItemAmount(entity),
            string.IsNullOrWhiteSpace(entity.OwnerName)
                ? null
                : TextSanitizer.CleanGameText(entity.OwnerName),
            entity.OwnerSteamId is { } steamId and not 0
                ? steamId.ToString(CultureInfo.InvariantCulture)
                : null,
            string.Create(
                CultureInfo.InvariantCulture,
                $"{entity.PositionX:0}, {entity.PositionY:0}, {entity.PositionZ:0}"
            ),
            cluster ? row.NodeLabel : null,
            cluster ? row.DeleteUnavailableReason : null
        );
    }

    private static (string Type, string Sort) Validate(EntityListRequest input) =>
        (Pick(Types, input.Type, "All", "Type"), Pick(Sorts, input.Sort, "Size", "Sort"));

    private static string Pick(string[] values, string? value, string fallback, string field)
    {
        var text = value?.Trim();
        if (string.IsNullOrEmpty(text))
            return fallback;
        return values.FirstOrDefault(candidate =>
                candidate.Equals(text, StringComparison.OrdinalIgnoreCase)
            )
            ?? throw new QuasarOperationException(
                400,
                $"{field} must be one of {string.Join(", ", values)}."
            );
    }

    private static ulong? Owner(EntityListRequest input)
    {
        var text = input.OwnerSteamId?.Trim();
        if (string.IsNullOrEmpty(text))
            return null;
        if (
            !ulong.TryParse(text, NumberStyles.None, CultureInfo.InvariantCulture, out var steamId)
            || steamId == 0
        )
            throw new QuasarOperationException(400, "OwnerSteamId must be a SteamID64.");
        return steamId;
    }

    private static long ParseId(string? text) =>
        long.TryParse(
            text?.Trim(),
            NumberStyles.AllowLeadingSign,
            CultureInfo.InvariantCulture,
            out var id
        )
        && id != 0
            ? id
            : throw new QuasarOperationException(400, "EntityId must be an entity ID.");

    private static EntityListFilter Filter(string type, string? search, int limit) =>
        new()
        {
            TypeTag = type,
            Search = search?.Trim() ?? string.Empty,
            Limit = limit,
        };

    private static AgentRuntimeState RequireAgent(IServiceProvider services, string uniqueName)
    {
        var name = uniqueName?.Trim() ?? string.Empty;
        var agent = services
            .GetRequiredService<AgentRegistry>()
            .GetAgents()
            .FirstOrDefault(candidate =>
                candidate.IsConnected
                && !candidate.IsCluster
                && candidate.UniqueNameKey.Equals(name, StringComparison.OrdinalIgnoreCase)
            );
        if (agent is not null)
            return agent;
        if (services.GetRequiredService<DedicatedServerCatalog>().GetServer(name) is null)
            throw NotFound(services, name, "use cluster.entities.list and cluster.entities.delete");
        throw new QuasarOperationException(409, $"Server '{name}' is not connected.");
    }

    private static ClusterDefinition RequireCluster(
        QuasarOperationContext caller,
        string uniqueName
    )
    {
        var name = uniqueName?.Trim() ?? string.Empty;
        var cluster = caller.Services.GetRequiredService<ClusterCatalog>().GetCluster(name);
        if (cluster is null || !caller.User.CanQueryCluster(cluster.UniqueName))
            throw new QuasarOperationException(404, $"Cluster '{name}' not found.");
        return cluster;
    }
}
