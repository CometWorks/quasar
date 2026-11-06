using System.Globalization;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using Quasar.Services.Auth;

namespace Quasar.Services.Players;

/// <summary>
/// The work behind core's player-scoped <c>my.*</c> operations: which server a request means and the agent
/// lookups. The Steam ID always comes from the player-agent principal.
/// </summary>
public sealed class QuasarPlayerTools(
    QuasarServerQueries queries,
    DedicatedServerCatalog? servers = null,
    ClusterCatalog? clusters = null,
    QuasarConfigProfileCatalog? profiles = null
)
{
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

    /// <summary>The named server, else the one the request came from, else the only one online.</summary>
    internal QuasarServerQueries.Target ResolveTarget(
        QuasarAgentIdentity player,
        string? uniqueName
    )
    {
        var name = string.IsNullOrWhiteSpace(uniqueName) ? player.Server : uniqueName.Trim();
        if (name is not null)
            return queries.Find(name)
                ?? throw new QuasarOperationException(404, $"Server '{name}' is not online.");

        var targets = queries.Targets();
        return targets.Count switch
        {
            1 => targets[0],
            0 => throw new QuasarOperationException(409, "No server is online."),
            _ => throw new QuasarOperationException(
                400,
                "Name the server with uniqueName, one of: "
                    + string.Join(", ", targets.Select(target => target.UniqueName))
                    + "."
            ),
        };
    }

    public object ServerInfo(QuasarAgentIdentity player, string? uniqueName)
    {
        var target = ResolveTarget(player, uniqueName);
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
        };
    }

    /// <summary>The GPS of an existing grid the player is a big owner of.</summary>
    public async Task<object> LocateAsync(
        QuasarAgentIdentity player,
        long gridId,
        string? uniqueName,
        CancellationToken token
    )
    {
        var target = ResolveTarget(player, uniqueName);
        var found =
            await queries.FindGridAsync(target, gridId, token)
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

    // A GPS line is colon separated.
    private static string GpsName(string name)
    {
        var clean = new string(
            (name ?? string.Empty).Where(c => c != ':' && !char.IsControl(c)).ToArray()
        ).Trim();
        return clean.Length == 0 ? "Grid" : clean;
    }
}
