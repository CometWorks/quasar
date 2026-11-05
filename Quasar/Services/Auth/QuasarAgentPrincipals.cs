using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using Quasar.Plugin.Abstractions.Security;

namespace Quasar.Services.Auth;

public sealed class QuasarAgentPrincipals(
    QuasarRoleMapper roles,
    IAuthorizationService authorization
) : IQuasarAgentPrincipals
{
    public async Task<ClaimsPrincipal> CreateAssistantAsync(
        ClaimsPrincipal asker,
        string conversationId,
        string request
    )
    {
        asker = roles.RefreshRoles(asker);
        if (!await IsPersonAsync(asker, authorization, QuasarPolicyNames.CanUseAssistant))
            throw new UnauthorizedAccessException(
                "Only a signed-in person allowed to use the assistant can ask it."
            );
        return roles.RefreshRoles(
            new ClaimsPrincipal(
                new QuasarAgentIdentity(
                    QuasarAgentKind.Assistant,
                    asker,
                    null,
                    null,
                    null,
                    conversationId,
                    request,
                    []
                )
            )
        );
    }

    // Core only: the Steam ID must come from an authenticated game session or a stored Discord link.
    internal ClaimsPrincipal CreatePlayer(
        string steamId,
        string? playerName,
        string? discordUserId,
        string conversationId,
        string request
    )
    {
        if (!RbacConfigCatalog.IsSteamId64(steamId))
            throw new ArgumentException(
                "The player agent needs a 17-digit SteamID64.",
                nameof(steamId)
            );
        return roles.RefreshRoles(
            new ClaimsPrincipal(
                new QuasarAgentIdentity(
                    QuasarAgentKind.Player,
                    null,
                    steamId,
                    playerName,
                    discordUserId,
                    conversationId,
                    request,
                    []
                )
            )
        );
    }

    /// <summary>True for an authenticated person, not a service or agent principal, who satisfies the policy.</summary>
    internal static async Task<bool> IsPersonAsync(
        ClaimsPrincipal principal,
        IAuthorizationService authorization,
        string policy
    ) =>
        principal.Identity?.IsAuthenticated == true
        && !principal.IsQuasarServicePrincipal()
        && !principal.IsQuasarAgent(out _)
        && (await authorization.AuthorizeAsync(principal, null, policy)).Succeeded;
}
