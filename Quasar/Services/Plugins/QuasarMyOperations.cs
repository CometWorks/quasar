using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Players;
using QuasarPolicyNames = Quasar.Services.Auth.QuasarPolicyNames;

namespace Quasar.Services.Plugins;

/// <summary>
/// Core's player agent operations; UI plugins can add more under the same policy. None takes a player: the
/// Steam ID is the calling player agent's, from the authenticated game session or the stored Discord link. Only
/// the player agent satisfies <see cref="QuasarPolicyNames.CanActAsPlayer"/>.
/// </summary>
public static class QuasarMyOperations
{
    public sealed record ServerRequest(string? UniqueName = null);

    public sealed record GridRequest(long GridId, string? UniqueName = null);

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
                Task.FromResult(
                    Tools(context).ServerInfo(QuasarPlayerTools.Player(context), input.UniqueName)
                ),
            Read with
            {
                Description =
                    "Returns the name, status, online player count, description and message of the day of the player's server. Without uniqueName it uses the server the player asked from.",
            }
        );
        services.AddQuasarOperation<GridRequest, object>(
            plugin,
            "my.grid.locate",
            PlayerPolicy,
            (context, input, token) =>
                Tools(context)
                    .LocateAsync(
                        QuasarPlayerTools.Player(context),
                        input.GridId,
                        input.UniqueName,
                        token
                    ),
            Read with
            {
                Description =
                    "Returns a GPS line (GPS:name:x:y:z:) for one of the player's grids in the world. Refused unless the player is among its biggest owners. Without uniqueName it uses the server the player asked from.",
            }
        );
    }

    private static QuasarPlayerTools Tools(QuasarOperationContext context) =>
        context.Services.GetRequiredService<QuasarPlayerTools>();
}
