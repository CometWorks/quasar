using System.Text.Json;
using System.Text.Json.Nodes;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Assistant;
using static Quasar.Services.Assistant.QuasarPlayerTools;
using QuasarPolicyNames = Quasar.Services.Auth.QuasarPolicyNames;

namespace Quasar.Services.Plugins;

/// <summary>
/// The player agent's operations (R6, R13). None takes a player: the Steam ID is the calling player agent's,
/// from the authenticated game session or the stored Discord link. Only the player agent satisfies
/// <see cref="QuasarPolicyNames.CanActAsPlayer"/>.
/// </summary>
public static class QuasarMyOperations
{
    public sealed record ServerRequest(string? UniqueName = null);

    public sealed record GridRequest(long GridId, string? UniqueName = null);

    public sealed record LossesRequest(DateTimeOffset? SinceUtc = null, string? UniqueName = null);

    private static readonly string[] PlayerPolicy = [QuasarPolicyNames.CanActAsPlayer];
    private static readonly QuasarOperationMetadata Read = new()
    {
        Effect = QuasarOperationEffect.Read,
        Area = "my",
    };

    public static void AddQuasarMyOperations(this IServiceCollection services)
    {
        const string plugin = QuasarCoreOperations.PluginId;
        services.AddQuasarOperation<ServerRequest, object>(
            plugin,
            "my.server.info",
            PlayerPolicy,
            (context, input, token) =>
                Task.FromResult(Tools(context).ServerInfo(Player(context), input.UniqueName)),
            Read with
            {
                Description =
                    "Returns the name, status, online player count, description and message of the day of the player's server, and whether grid tools work there. Without uniqueName it uses the server the player asked from.",
            }
        );
        services.AddQuasarOperation<ServerRequest, JsonNode?>(
            plugin,
            "my.grids",
            PlayerPolicy,
            (context, input, token) =>
                Tools(context).GridsAsync(Player(context), input.UniqueName, token),
            Read with
            {
                Description =
                    "Lists the player's grids that have backups, newest backup first, with whether each is in the world now. Without uniqueName it uses the server the player asked from.",
            }
        );
        services.AddQuasarOperation<GridRequest, JsonElement>(
            plugin,
            "my.grid.backups",
            PlayerPolicy,
            (context, input, token) =>
                Tools(context)
                    .DescribeAsync(Player(context), input.GridId, input.UniqueName, token),
            Read with
            {
                Description =
                    "Returns the backups Grid Backups keeps of one of the player's grids, newest first, with their time and position. Without uniqueName it uses the server the player asked from.",
            }
        );
        services.AddQuasarOperation<LossesRequest, JsonElement>(
            plugin,
            "my.grid.losses",
            PlayerPolicy,
            (context, input, token) =>
                Tools(context)
                    .LossesAsync(Player(context), input.SinceUtc, input.UniqueName, token),
            Read with
            {
                Description =
                    "Lists the player's lost grids, newest first, with the time and cause (destroyed, ground, deleted, cleanup or hangar), optionally only since sinceUtc. Without uniqueName it uses the server the player asked from.",
            }
        );
        services.AddQuasarOperation<GridRequest, object>(
            plugin,
            "my.grid.locate",
            PlayerPolicy,
            (context, input, token) =>
                Tools(context).LocateAsync(Player(context), input.GridId, input.UniqueName, token),
            Read with
            {
                Description =
                    "Returns a GPS line (GPS:name:x:y:z:) for one of the player's grids in the world. Refused unless the player is among its biggest owners. Without uniqueName it uses the server the player asked from.",
            }
        );
        services.AddQuasarOperation<RestoreRequest, JsonElement>(
            plugin,
            "my.grid.restore",
            PlayerPolicy,
            (context, input, token) =>
                Tools(context).RestoreAsync(Player(context), input, context.Undo, token),
            new()
            {
                Effect = QuasarOperationEffect.Change,
                Area = "my",
                Reversal = QuasarReversal.Undo,
                Undo = new RestoreUndo(),
                Deadline = QuasarOperationMetadata.MaximumDeadline,
                Description =
                    "Restores one of the player's grids that is gone from the world, from its newest backup before the loss. Placement is original (default), near-player or near-player-station; the near-player ones need the player in game. Defaults: 2000 m clear of other players' grids, search up to 10000 m for a free spot, inventories kept. Without uniqueName it uses the server the player asked from.",
            }
        );
    }

    private static QuasarPlayerTools Tools(QuasarOperationContext context) =>
        context.Services.GetRequiredService<QuasarPlayerTools>();

    /// <summary>Removes the restored grids that still exist, through Grid Backups' <c>remove-restored</c> on the
    /// node that has them now. The player's restore quota counts only restores that were not reversed.</summary>
    internal sealed class RestoreUndo : IQuasarUndoHandler
    {
        public async Task<string?> CheckAsync(QuasarUndoContext context, CancellationToken token)
        {
            var data = context.Data.Deserialize<RestoreUndoData>(JsonOptions)!;
            var found = await context
                .Services.GetRequiredService<QuasarPlayerTools>()
                .FindGridsAsync(data.Server, data.EntityIds, token);
            var gone = data.EntityIds.Where(id => !found.ContainsKey(id)).ToList();
            return gone.Count == 0
                ? null
                : "These restored grids are no longer in the world: "
                    + string.Join(", ", gone)
                    + ".";
        }

        public async Task ReverseAsync(QuasarUndoContext context, CancellationToken token)
        {
            var data = context.Data.Deserialize<RestoreUndoData>(JsonOptions)!;
            var tools = context.Services.GetRequiredService<QuasarPlayerTools>();
            var found = await tools.FindGridsAsync(data.Server, data.EntityIds, token);
            foreach (var group in found.GroupBy(pair => pair.Value.AgentId))
                await tools.CallAsync(
                    group.First().Value,
                    "remove-restored",
                    new { entityIds = group.Select(pair => pair.Key).ToArray() },
                    token
                );
        }

        private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
    }
}
