using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Magnetar.Protocol.Model;
using Magnetar.Protocol.Transport;
using Quasar.Plugin.Abstractions.Companion;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using Quasar.Services.Auth;

namespace Quasar.Services.Assistant;

/// <summary>
/// The work behind the player-scoped <c>my.*</c> operations: which server a request means, the Grid Backups
/// companion calls and the agent lookups. The Steam ID always comes from the player-agent principal.
/// </summary>
public sealed class QuasarPlayerTools(
    AgentRegistry registry,
    IQuasarCompanionChannel companion,
    DedicatedServerCatalog? servers = null,
    ClusterCatalog? clusters = null,
    QuasarConfigProfileCatalog? profiles = null,
    ClusterPlayerService? clusterPlayers = null
)
{
    /// <summary>Grid Backups' plugin ID (<c>GridBackups.xml</c>).</summary>
    public const string GridBackupsPluginId = "9AC2F6A8-B8A7-4250-8814-BADE62C5CF3C";

    public const string PlacementOriginal = "original";
    public const string PlacementNearPlayer = "near-player";
    public const string PlacementNearPlayerStation = "near-player-station";
    public const double DefaultForeignExclusionMeters = 2000;
    public const double DefaultMaxSearchMeters = 10000;

    /// <summary>Leaves the restore operation's 300 s deadline some room to record the result.</summary>
    internal static readonly TimeSpan RestoreTimeout = TimeSpan.FromSeconds(280);
    private static readonly TimeSpan AgentTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    /// <summary>A standalone server, or a cluster as one target with its regular nodes.</summary>
    internal sealed record Target(
        string UniqueName,
        string DisplayName,
        bool IsCluster,
        IReadOnlyList<AgentRuntimeState> Agents
    )
    {
        public IReadOnlyList<AgentRuntimeState> GridBackupNodes =>
            Agents.Where(HasGridBackups).ToList();
    }

    /// <summary>The calling player agent; refuses anyone else, including a player agent without a Steam ID.</summary>
    internal static QuasarAgentIdentity Player(QuasarOperationContext context)
    {
        if (
            !context.User.IsQuasarAgent(out var agent)
            || agent.Kind != QuasarAgentKind.Player
            || !RbacConfigCatalog.IsSteamId64(agent.PlayerSteamId ?? string.Empty)
        )
            throw new QuasarOperationException(
                403,
                "Only the player agent of a linked or in-game player can use this operation."
            );
        return agent;
    }

    internal static bool HasGridBackups(AgentRuntimeState agent) =>
        agent.Snapshot?.Plugins.Any(plugin =>
            plugin.IsLoaded
            && string.Equals(
                plugin.PluginId,
                GridBackupsPluginId,
                StringComparison.OrdinalIgnoreCase
            )
        ) == true;

    internal static bool IsWorldAuthority(AgentRuntimeState agent)
    {
        var role = agent.Snapshot?.ClusterNodeRole ?? agent.Hello?.ClusterNodeRole ?? string.Empty;
        return role.Equals("WA", StringComparison.OrdinalIgnoreCase)
            || role.Equals("WorldAuthority", StringComparison.OrdinalIgnoreCase);
    }

    internal IReadOnlyList<Target> Targets()
    {
        var agents = registry
            .GetAgents()
            .Where(agent => agent.IsConnected && agent.Snapshot is not null)
            .ToList();
        var standalone = agents
            .Where(agent => !agent.IsCluster)
            .GroupBy(agent => agent.UniqueNameKey, StringComparer.OrdinalIgnoreCase)
            .Select(group => new Target(
                group.Key,
                group.First().ServerDisplayName,
                false,
                group.Take(1).ToList()
            ));
        var clustered = agents
            .Where(agent =>
                agent.IsCluster && agent.ClusterId.Length > 0 && !IsWorldAuthority(agent)
            )
            .GroupBy(agent => agent.ClusterId, StringComparer.OrdinalIgnoreCase)
            .Select(group => new Target(
                group.Key,
                clusters?.GetCluster(group.Key)?.DisplayName ?? group.Key,
                true,
                group.OrderBy(agent => agent.ClusterNodeId, StringComparer.Ordinal).ToList()
            ));
        return standalone
            .Concat(clustered)
            .OrderBy(target => target.UniqueName, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>The named server, else the one the request came from, else the only candidate.</summary>
    internal Target ResolveTarget(QuasarAgentIdentity player, string? uniqueName, bool gridBackups)
    {
        var targets = Targets();
        var name = string.IsNullOrWhiteSpace(uniqueName) ? player.Server : uniqueName.Trim();
        if (name is not null)
        {
            if (name.StartsWith("cluster:", StringComparison.OrdinalIgnoreCase))
                name = name["cluster:".Length..];
            var target =
                targets.FirstOrDefault(candidate =>
                    string.Equals(candidate.UniqueName, name, StringComparison.OrdinalIgnoreCase)
                ) ?? throw new QuasarOperationException(404, $"Server '{name}' is not online.");
            if (gridBackups && target.GridBackupNodes.Count == 0)
                throw new QuasarOperationException(
                    409,
                    $"Grid Backups is not loaded on {target.DisplayName}, so its grid tools are not available there."
                );
            return target;
        }

        var candidates = targets
            .Where(target => !gridBackups || target.GridBackupNodes.Count > 0)
            .ToList();
        return candidates.Count switch
        {
            1 => candidates[0],
            0 => throw new QuasarOperationException(
                409,
                gridBackups ? "No online server has Grid Backups loaded." : "No server is online."
            ),
            _ => throw new QuasarOperationException(
                400,
                "Name the server with uniqueName, one of: "
                    + string.Join(", ", candidates.Select(target => target.UniqueName))
                    + "."
            ),
        };
    }

    public object ServerInfo(QuasarAgentIdentity player, string? uniqueName)
    {
        var target = ResolveTarget(player, uniqueName, gridBackups: false);
        var profileId = target.IsCluster
            ? clusters?.GetCluster(target.UniqueName)?.ConfigProfileId
            : servers?.GetServer(target.UniqueName)?.ConfigProfileId;
        var settings = string.IsNullOrWhiteSpace(profileId)
            ? null
            : profiles?.GetProfile(profileId)?.RootSettings;
        return new
        {
            uniqueName = target.UniqueName,
            name = target.DisplayName,
            cluster = target.IsCluster,
            status = target.Agents.Any(agent => agent.Snapshot?.IsRunning == true)
                ? "running"
                : "starting or stopped",
            playersOnline = target
                .Agents.SelectMany(agent => agent.Snapshot?.Players ?? [])
                .Select(p => p.SteamId)
                .Distinct()
                .Count(),
            description = settings?.ServerDescription ?? string.Empty,
            messageOfTheDay = settings?.MessageOfTheDay ?? string.Empty,
            gridTools = target.GridBackupNodes.Count > 0,
        };
    }

    /// <summary>Grid Backups <c>list</c>. On a cluster the storage is shared, but <c>present</c> only sees the node
    /// asked, so every node is asked and a grid is present when any node has it.</summary>
    public async Task<JsonNode?> GridsAsync(
        QuasarAgentIdentity player,
        string? uniqueName,
        CancellationToken token
    )
    {
        var target = ResolveTarget(player, uniqueName, gridBackups: true);
        var request = new { steamId = player.PlayerSteamId };
        var answers = await Task.WhenAll(
            target.GridBackupNodes.Select(async node =>
            {
                try
                {
                    return (
                        Answer: (JsonElement?)await CallAsync(node, "list", request, token),
                        Error: (Exception?)null
                    );
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    return (null, error);
                }
            })
        );
        var answered = answers
            .Where(answer => answer.Answer is not null)
            .Select(answer => answer.Answer!.Value)
            .ToList();
        if (answered.Count == 0)
            throw new QuasarOperationException(502, answers.First().Error!.Message);

        var result = JsonNode.Parse(answered[0].GetRawText())!;
        if (answered.Count > 1 && result["grids"] is JsonArray grids)
        {
            var present = answered
                .SelectMany(answer =>
                    answer.TryGetProperty("grids", out var list)
                        ? list.EnumerateArray()
                        : Enumerable.Empty<JsonElement>()
                )
                .Where(grid =>
                    grid.TryGetProperty("present", out var flag)
                    && flag.ValueKind == JsonValueKind.True
                )
                .Select(grid => grid.GetProperty("gridId").GetInt64())
                .ToHashSet();
            foreach (var grid in grids.OfType<JsonObject>())
                grid["present"] = grid["gridId"] is { } id && present.Contains(id.GetValue<long>());
        }

        if (answered.Count < answers.Length)
            result["presentIncomplete"] = true;
        return result;
    }

    public async Task<JsonElement> DescribeAsync(
        QuasarAgentIdentity player,
        long gridId,
        string? uniqueName,
        CancellationToken token
    )
    {
        var target = ResolveTarget(player, uniqueName, gridBackups: true);
        return await CallAsync(
            target.GridBackupNodes[0],
            "describe",
            new { steamId = player.PlayerSteamId, gridId },
            token
        );
    }

    public async Task<JsonElement> LossesAsync(
        QuasarAgentIdentity player,
        DateTimeOffset? sinceUtc,
        string? uniqueName,
        CancellationToken token
    )
    {
        var target = ResolveTarget(player, uniqueName, gridBackups: true);
        return await CallAsync(
            target.GridBackupNodes[0],
            "losses",
            new { steamId = player.PlayerSteamId, sinceUtc },
            token
        );
    }

    /// <summary>The GPS of an existing grid the player is a big owner of.</summary>
    public async Task<object> LocateAsync(
        QuasarAgentIdentity player,
        long gridId,
        string? uniqueName,
        CancellationToken token
    )
    {
        var target = ResolveTarget(player, uniqueName, gridBackups: false);
        var found =
            await FindGridAsync(target, gridId, token)
            ?? throw new QuasarOperationException(404, "That grid is not in the world.");
        var grid = found.Grid;
        // Older agents report only the first big owner.
        var owners =
            grid.BigOwnerSteamIds
            ?? (grid.OwnerSteamId is { } owner ? new List<ulong> { owner } : new List<ulong>());
        if (
            !owners.Any(owner =>
                owner.ToString(CultureInfo.InvariantCulture) == player.PlayerSteamId
            )
        )
            throw new QuasarOperationException(
                403,
                "You are not among the majority owners of that grid."
            );

        var name = GpsName(grid.DisplayName);
        var gps = string.Create(
            CultureInfo.InvariantCulture,
            $"GPS:{name}:{grid.PositionX:F2}:{grid.PositionY:F2}:{grid.PositionZ:F2}:"
        );
        return new
        {
            gridId,
            name = grid.DisplayName,
            gps,
        };
    }

    /// <summary>Grid Backups <c>restore</c> on the node that should spawn the grid. Throws 409 with
    /// <c>refused: reason</c> or <c>no-spot: reason</c> when nothing was restored.</summary>
    public async Task<JsonElement> RestoreAsync(
        QuasarAgentIdentity player,
        RestoreRequest request,
        QuasarUndoRecorder? undo,
        CancellationToken token
    )
    {
        var placement = (request.Placement ?? PlacementOriginal).Trim().ToLowerInvariant();
        if (
            placement
            is not (PlacementOriginal or PlacementNearPlayer or PlacementNearPlayerStation)
        )
            throw new QuasarOperationException(
                400,
                "Placement must be original, near-player or near-player-station."
            );

        var target = ResolveTarget(player, request.UniqueName, gridBackups: true);
        // Set before anything can refuse, so a refused restore names its server in the audit log.
        if (undo is not null)
            undo.Server = target.UniqueName;
        var exclusion = request.ForeignExclusionMeters ?? DefaultForeignExclusionMeters;
        var maxSearch = request.MaxSearchMeters ?? DefaultMaxSearchMeters;
        var node = await RestoreNodeAsync(
            target,
            player,
            request.GridId,
            placement,
            maxSearch,
            token
        );
        var answer = await CallAsync(
            node,
            "restore",
            new
            {
                steamId = player.PlayerSteamId,
                gridId = request.GridId,
                placement,
                emptyInventories = request.EmptyInventories,
                foreignExclusionMeters = exclusion,
                maxSearchMeters = maxSearch,
            },
            token,
            RestoreTimeout
        );

        var status = answer.TryGetProperty("status", out var value) ? value.GetString() : null;
        if (status != "restored")
        {
            var reason = answer.TryGetProperty("reason", out var why) ? why.GetString() : null;
            throw new QuasarOperationException(
                409,
                $"{status ?? "refused"}: {reason ?? "Grid Backups gave no reason."}"
            );
        }

        if (undo is not null)
        {
            undo.SetData(
                new RestoreUndoData(
                    target.UniqueName,
                    target.IsCluster ? node.ClusterNodeId : null,
                    answer.TryGetProperty("backupId", out var backup) ? backup.GetString() : null,
                    request.GridId,
                    answer.TryGetProperty("entityIds", out var ids)
                        ? ids.EnumerateArray().Select(id => id.GetInt64()).ToArray()
                        : []
                )
            );
        }

        return answer;
    }

    /// <summary>Near-player modes run on the player's node. <c>original</c> on a cluster runs on the node
    /// that owns entities nearest the backup position, else the player's node, else any node.</summary>
    internal async Task<AgentRuntimeState> RestoreNodeAsync(
        Target target,
        QuasarAgentIdentity player,
        long gridId,
        string placement,
        double maxSearchMeters,
        CancellationToken token
    )
    {
        var nodes = target.GridBackupNodes;
        if (!target.IsCluster)
            return nodes[0];

        var playerNode = PlayerNode(target, player.PlayerSteamId!);
        if (placement != PlacementOriginal)
            return playerNode
                ?? throw new QuasarOperationException(
                    409,
                    "refused: You need to be online on the cluster for a restore near you."
                );

        var describe = await CallAsync(
            nodes[0],
            "describe",
            new { steamId = player.PlayerSteamId, gridId },
            token
        );
        if (BackupPosition(describe) is not { } position)
            return playerNode ?? nodes[0];

        var payload = JsonSerializer.Serialize(
            new PositionOwnerRequest
            {
                X = position.X,
                Y = position.Y,
                Z = position.Z,
                RadiusMeters = Math.Max(1, maxSearchMeters),
            },
            Json
        );
        var answers = await Task.WhenAll(
            nodes.Select(async node =>
            {
                try
                {
                    var result = await CommandAsync(
                        node,
                        ServerCommandType.ResolvePositionOwner,
                        payload,
                        token
                    );
                    return (
                        Node: node,
                        Owner: result.Success
                            ? JsonSerializer.Deserialize<PositionOwnerResult>(result.Payload, Json)
                            : null
                    );
                }
                catch (Exception error) when (error is not OperationCanceledException)
                {
                    return (node, null);
                }
            })
        );
        return Nearest(answers) ?? playerNode ?? nodes[0];
    }

    internal static AgentRuntimeState? Nearest(
        IEnumerable<(AgentRuntimeState Node, PositionOwnerResult? Owner)> answers
    ) =>
        answers
            .Where(answer => answer.Owner?.Found == true)
            .OrderBy(answer => answer.Owner!.DistanceMeters)
            .Select(answer => answer.Node)
            .FirstOrDefault();

    /// <summary>The position of the backup a restore would use: the newest taken before the latest loss.</summary>
    internal static (double X, double Y, double Z)? BackupPosition(JsonElement describe)
    {
        if (
            !describe.TryGetProperty("backups", out var backups)
            || backups.ValueKind != JsonValueKind.Array
        )
            return null;
        DateTimeOffset? lostAt =
            describe.TryGetProperty("loss", out var loss)
            && loss.ValueKind == JsonValueKind.Object
            && loss.TryGetProperty("timeUtc", out var time)
                ? time.GetDateTimeOffset()
                : null;
        foreach (var backup in backups.EnumerateArray())
        {
            if (
                lostAt is { } limit
                && backup.TryGetProperty("takenUtc", out var taken)
                && taken.GetDateTimeOffset() >= limit
            )
                continue;
            if (!backup.TryGetProperty("position", out var position))
                return null;
            return (
                position.GetProperty("x").GetDouble(),
                position.GetProperty("y").GetDouble(),
                position.GetProperty("z").GetDouble()
            );
        }

        return null;
    }

    private AgentRuntimeState? PlayerNode(Target target, string steamId)
    {
        var node = clusterPlayers
            ?.GetSnapshot(target.UniqueName)
            ?.Clients.FirstOrDefault(client =>
                client.ClientId.ToString(CultureInfo.InvariantCulture) == steamId
            )
            ?.Node;
        return node is not null
            ? target.GridBackupNodes.FirstOrDefault(agent => agent.ClusterNodeId == node)
            : target.GridBackupNodes.FirstOrDefault(agent =>
                agent.Snapshot?.Players.Any(p =>
                    p.SteamId.ToString(CultureInfo.InvariantCulture) == steamId
                ) == true
            );
    }

    /// <summary>Which node has each grid now; ids no node has are left out.</summary>
    internal async Task<Dictionary<long, AgentRuntimeState>> FindGridsAsync(
        string server,
        IEnumerable<long> gridIds,
        CancellationToken token
    )
    {
        var target =
            Targets()
                .FirstOrDefault(candidate =>
                    string.Equals(candidate.UniqueName, server, StringComparison.OrdinalIgnoreCase)
                )
            ?? throw new InvalidOperationException($"Server '{server}' is not online.");
        var found = new Dictionary<long, AgentRuntimeState>();
        foreach (var gridId in gridIds.Distinct())
            if (await FindGridAsync(target, gridId, token) is { } grid)
                found[gridId] = grid.Node;
        return found;
    }

    private async Task<(AgentRuntimeState Node, EntitySummary Grid)?> FindGridAsync(
        Target target,
        long gridId,
        CancellationToken token
    )
    {
        var filter = JsonSerializer.Serialize(
            new EntityListFilter
            {
                TypeTag = "Grid",
                Search = gridId.ToString(CultureInfo.InvariantCulture),
                Limit = 50,
            },
            Json
        );
        var answers = await Task.WhenAll(
            target.Agents.Select(async node =>
            {
                var result = await CommandAsync(
                    node,
                    ServerCommandType.ListEntities,
                    filter,
                    token
                );
                if (!result.Success)
                    throw new InvalidOperationException(result.Message);
                var grid = JsonSerializer
                    .Deserialize<EntityListResult>(result.Payload, Json)
                    ?.Entities.FirstOrDefault(entity => entity.EntityId == gridId);
                return (Node: node, Grid: grid);
            })
        );
        // On a cluster a copy can exist on a node that does not own it; the owner's view wins.
        return answers
            .Where(answer => answer.Grid is not null)
            .OrderBy(answer => answer.Grid!.ClusterResidence is null or "Local" ? 0 : 1)
            .Select(answer => ((AgentRuntimeState, EntitySummary)?)(answer.Node, answer.Grid!))
            .FirstOrDefault();
    }

    internal async Task<JsonElement> CallAsync(
        AgentRuntimeState node,
        string operation,
        object request,
        CancellationToken token,
        TimeSpan? timeout = null
    ) =>
        timeout is { } wait && companion is QuasarCompanionChannel channel
            ? await channel.SendAsync<object, JsonElement>(
                node.AgentId,
                GridBackupsPluginId,
                operation,
                request,
                wait,
                token
            )
            : await companion.SendAsync<object, JsonElement>(
                node.AgentId,
                GridBackupsPluginId,
                operation,
                request,
                token
            );

    private Task<ServerCommandResult> CommandAsync(
        AgentRuntimeState node,
        ServerCommandType type,
        string payload,
        CancellationToken token
    ) =>
        registry.SendCommandAndWaitAsync(
            new ServerCommandEnvelope
            {
                UniqueName = node.UniqueNameKey,
                AgentId = node.AgentId,
                ServerId = node.ServerKey,
                CommandType = type,
                Payload = payload,
            },
            AgentTimeout,
            token
        );

    // A GPS line is colon separated.
    private static string GpsName(string name)
    {
        var clean = new string(
            (name ?? string.Empty).Where(c => c != ':' && !char.IsControl(c)).ToArray()
        ).Trim();
        return clean.Length == 0 ? "Grid" : clean;
    }

    public sealed record RestoreRequest(
        long GridId,
        string? Placement = null,
        bool EmptyInventories = false,
        double? ForeignExclusionMeters = null,
        double? MaxSearchMeters = null,
        string? UniqueName = null
    );

    internal sealed record RestoreUndoData(
        string Server,
        string? Node,
        string? BackupId,
        long GridId,
        long[] EntityIds
    );
}
