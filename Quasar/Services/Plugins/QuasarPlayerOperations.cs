using System.Globalization;
using System.Text.Json;
using Magnetar.Protocol.Transport;
using Quasar.Models;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

/// <summary>Player moderation on standalone servers, through the server's agent. Cluster players are not here.</summary>
public static class QuasarPlayerOperations
{
    public sealed record ServerRequest(string UniqueName = "");

    /// <param name="SteamId">The player's SteamID64, as a string.</param>
    public sealed record PlayerRequest(string UniqueName = "", string SteamId = "");

    /// <param name="Level">None, Scripter, Moderator, SpaceMaster or Admin.</param>
    public sealed record PromotionRequest(
        string UniqueName = "",
        string SteamId = "",
        string Level = ""
    );

    public sealed record OnlinePlayer(
        string SteamId,
        string Name,
        string FactionTag,
        string PromoteLevel,
        int PingMs
    );

    public sealed record KnownPlayer(
        string SteamId,
        string Name,
        DateTimeOffset LastSeenUtc,
        bool Banned,
        string PromoteLevel
    );

    public sealed record PlayerList(
        string UniqueName,
        bool Connected,
        IReadOnlyList<OnlinePlayer> Online,
        IReadOnlyList<KnownPlayer> Known,
        int KnownTotal
    );

    public sealed record PlayerActionResult(string UniqueName, string SteamId, string Message);

    public static readonly string[] PromoteLevels =
    [
        "None",
        "Scripter",
        "Moderator",
        "SpaceMaster",
        "Admin",
    ];

    private const int KnownLimit = 200;
    private static readonly TimeSpan CommandWait = TimeSpan.FromSeconds(15);

    public static void AddQuasarPlayerOperations(this IServiceCollection services)
    {
        services.AddQuasarOperation<ServerRequest, PlayerList>(
            QuasarCoreOperations.PluginId,
            "players.list",
            [QuasarPolicyNames.CanView],
            (caller, input, _) => Task.FromResult(List(caller.Services, input.UniqueName)),
            new()
            {
                Effect = QuasarOperationEffect.Read,
                Description =
                    $"Lists a standalone server's online players (Steam ID, name, faction tag, promote level, ping) and its known players, the {KnownLimit} seen most recently, with the banned flag.",
            }
        );
        var banUndo = new PlayerUndo(
            (record, after) => record?.IsBanned == after.GetBoolean(),
            before =>
                before.GetBoolean()
                    ? (ServerCommandType.BanPlayer, string.Empty)
                    : (ServerCommandType.UnbanPlayer, string.Empty)
        );
        var levelUndo = new PlayerUndo(
            (record, after) =>
                KnownPlayerCatalog.NormalizePromoteLevel(record?.PromoteLevel) == after.GetString(),
            before => (ServerCommandType.SetPlayerPromoteLevel, before.GetString()!)
        );
        Add(
            services,
            "players.kick",
            ServerCommandType.KickPlayer,
            null,
            "Kicks a player from a standalone server. The player can rejoin after the kick cooldown."
        );
        Add(
            services,
            "players.ban",
            ServerCommandType.BanPlayer,
            banUndo,
            "Bans a player from a standalone server and kicks them if online."
        );
        Add(
            services,
            "players.unban",
            ServerCommandType.UnbanPlayer,
            banUndo,
            "Lifts a player's ban on a standalone server."
        );
        Add(
            services,
            "players.promote",
            ServerCommandType.PromotePlayer,
            levelUndo,
            "Raises a player's promote level on a standalone server by one step: None, Scripter, Moderator, SpaceMaster, Admin."
        );
        Add(
            services,
            "players.demote",
            ServerCommandType.DemotePlayer,
            levelUndo,
            "Lowers a player's promote level on a standalone server by one step."
        );
        Add(
            services,
            "players.kick-cooldown.clear",
            ServerCommandType.ClearKickCooldown,
            null,
            "Clears a kicked player's rejoin cooldown on a standalone server."
        );
        services.AddQuasarOperation<PromotionRequest, PlayerActionResult>(
            QuasarCoreOperations.PluginId,
            "players.promotion.set",
            [QuasarPolicyNames.CanControlServers],
            (caller, input, token) =>
            {
                var level =
                    PromoteLevels.FirstOrDefault(candidate =>
                        candidate.Equals(input.Level?.Trim(), StringComparison.OrdinalIgnoreCase)
                    )
                    ?? throw new QuasarOperationException(
                        400,
                        $"Level must be one of {string.Join(", ", PromoteLevels)}."
                    );
                return SendAsync(
                    caller,
                    input.UniqueName,
                    input.SteamId,
                    ServerCommandType.SetPlayerPromoteLevel,
                    level,
                    token
                );
            },
            new()
            {
                Reversal = QuasarReversal.Undo,
                Undo = levelUndo,
                Description =
                    "Sets a player's promote level on a standalone server: None, Scripter, Moderator, SpaceMaster or Admin.",
            }
        );
    }

    private static void Add(
        IServiceCollection services,
        string operationId,
        ServerCommandType command,
        IQuasarUndoHandler? undo,
        string description
    ) =>
        services.AddQuasarOperation<PlayerRequest, PlayerActionResult>(
            QuasarCoreOperations.PluginId,
            operationId,
            [QuasarPolicyNames.CanControlServers],
            (caller, input, token) =>
                SendAsync(caller, input.UniqueName, input.SteamId, command, string.Empty, token),
            new()
            {
                Reversal = undo is null ? QuasarReversal.NothingToReverse : QuasarReversal.Undo,
                Undo = undo,
                Description = description,
            }
        );

    private static PlayerList List(IServiceProvider services, string uniqueName)
    {
        var name = uniqueName?.Trim() ?? string.Empty;
        var agent = FindAgent(services, name);
        if (
            agent is null
            && services.GetRequiredService<DedicatedServerCatalog>().GetServer(name) is null
        )
            throw new QuasarOperationException(404, $"Server '{name}' not found.");
        var known = Known(services, name).OrderByDescending(record => record.LastSeenUtc).ToList();
        return new(
            name,
            agent is not null,
            (agent?.Snapshot?.Players ?? [])
                .Select(player => new OnlinePlayer(
                    player.SteamId.ToString(CultureInfo.InvariantCulture),
                    player.DisplayName,
                    player.FactionTag,
                    player.PromoteLevel,
                    player.PingMs
                ))
                .ToList(),
            known
                .Take(KnownLimit)
                .Select(record => new KnownPlayer(
                    record.SteamId.ToString(CultureInfo.InvariantCulture),
                    record.DisplayName,
                    record.LastSeenUtc,
                    record.IsBanned,
                    KnownPlayerCatalog.NormalizePromoteLevel(record.PromoteLevel)
                ))
                .ToList(),
            known.Count
        );
    }

    private static async Task<PlayerActionResult> SendAsync(
        QuasarOperationContext caller,
        string uniqueName,
        string steamIdText,
        ServerCommandType command,
        string text,
        CancellationToken token
    )
    {
        var name = uniqueName?.Trim() ?? string.Empty;
        if (
            !long.TryParse(
                steamIdText?.Trim(),
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var steamId
            )
            || steamId <= 0
        )
            throw new QuasarOperationException(400, "SteamId must be a SteamID64.");
        caller.Undo!.Server = name;
        var record = FindKnown(caller.Services, name, steamId);
        var before = command switch
        {
            ServerCommandType.BanPlayer or ServerCommandType.UnbanPlayer => (object?)(
                record?.IsBanned ?? false
            ),
            ServerCommandType.PromotePlayer
            or ServerCommandType.DemotePlayer
            or ServerCommandType.SetPlayerPromoteLevel => KnownPlayerCatalog.NormalizePromoteLevel(
                record?.PromoteLevel
            ),
            _ => null,
        };
        object? after = command switch
        {
            ServerCommandType.BanPlayer => true,
            ServerCommandType.UnbanPlayer => false,
            ServerCommandType.PromotePlayer => KnownPlayerCatalog.GetAdjacentPromoteLevel(
                (string)before!,
                1
            ),
            ServerCommandType.DemotePlayer => KnownPlayerCatalog.GetAdjacentPromoteLevel(
                (string)before!,
                -1
            ),
            ServerCommandType.SetPlayerPromoteLevel => text,
            _ => null,
        };
        var message = await SendCommandAsync(caller.Services, name, steamId, command, text, token);
        // Nothing to undo when the action left things as they were.
        if (after is not null && !Equals(before, after))
            caller.Undo.SetData(
                new PlayerChange(
                    name,
                    steamId,
                    JsonSerializer.SerializeToElement(before),
                    JsonSerializer.SerializeToElement(after)
                )
            );
        return new(name, steamId.ToString(CultureInfo.InvariantCulture), message);
    }

    private static async Task<string> SendCommandAsync(
        IServiceProvider services,
        string uniqueName,
        long steamId,
        ServerCommandType command,
        string text,
        CancellationToken token
    )
    {
        var agent =
            FindAgent(services, uniqueName)
            ?? throw new QuasarOperationException(409, $"Server '{uniqueName}' is not connected.");
        var result = await services
            .GetRequiredService<AgentRegistry>()
            .SendCommandAndWaitAsync(
                new ServerCommandEnvelope
                {
                    UniqueName = agent.UniqueNameKey,
                    AgentId = agent.AgentId,
                    ServerId = agent.ServerKey,
                    CommandType = command,
                    Text = text,
                    SteamId = steamId,
                },
                CommandWait,
                token
            );
        if (!result.Success)
            throw new QuasarOperationException(409, result.Message);
        return result.Message;
    }

    private static AgentRuntimeState? FindAgent(IServiceProvider services, string uniqueName) =>
        services
            .GetRequiredService<AgentRegistry>()
            .GetAgents()
            .FirstOrDefault(agent =>
                agent.IsConnected
                && !agent.IsCluster
                && agent.UniqueNameKey.Equals(uniqueName, StringComparison.OrdinalIgnoreCase)
            );

    private static IEnumerable<KnownPlayerRecord> Known(
        IServiceProvider services,
        string uniqueName
    ) =>
        services
            .GetRequiredService<KnownPlayerCatalog>()
            .GetPlayers()
            .Where(record =>
                !record.IsCluster
                && record.UniqueName.Equals(uniqueName, StringComparison.OrdinalIgnoreCase)
            );

    private static KnownPlayerRecord? FindKnown(
        IServiceProvider services,
        string uniqueName,
        long steamId
    ) => Known(services, uniqueName).FirstOrDefault(record => record.SteamId == steamId);

    private sealed record PlayerChange(
        string UniqueName,
        long SteamId,
        JsonElement Before,
        JsonElement After
    );

    // Checks the known-player state the command outcome left and sends the command that restores the previous one.
    private sealed class PlayerUndo(
        Func<KnownPlayerRecord?, JsonElement, bool> unchanged,
        Func<JsonElement, (ServerCommandType Command, string Text)> restore
    ) : IQuasarUndoHandler
    {
        public Task<string?> CheckAsync(QuasarUndoContext context, CancellationToken token)
        {
            var change = context.Data.Deserialize<PlayerChange>(QuasarCoreOperations.Json)!;
            return Task.FromResult(
                unchanged(
                    FindKnown(context.Services, change.UniqueName, change.SteamId),
                    change.After
                )
                    ? null
                    : $"Player {change.SteamId} on '{change.UniqueName}' is no longer {change.After.GetRawText()}."
            );
        }

        public async Task ReverseAsync(QuasarUndoContext context, CancellationToken token)
        {
            var change = context.Data.Deserialize<PlayerChange>(QuasarCoreOperations.Json)!;
            var (command, text) = restore(change.Before);
            await SendCommandAsync(
                context.Services,
                change.UniqueName,
                change.SteamId,
                command,
                text,
                token
            );
        }
    }
}
