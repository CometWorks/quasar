using System.Diagnostics.CodeAnalysis;
using System.Security.Claims;

namespace Quasar.Plugin.Abstractions.Security;

public enum QuasarAgentKind
{
    Assistant,
    Player,
}

/// <summary>
/// An agent principal's identity. Only Quasar core builds one, in process. Agent checks test this CLR type,
/// so nothing deserialized from a cookie, bearer token, header or the SDK wire can be an agent.
/// </summary>
public sealed class QuasarAgentIdentity : ClaimsIdentity
{
    public const string Provider = "QuasarAgent";
    public const string ProviderClaimType = "quasar:provider";

    internal QuasarAgentIdentity(
        QuasarAgentKind kind,
        ClaimsPrincipal? asker,
        string? playerSteamId,
        string? playerName,
        string? discordUserId,
        string conversationId,
        string request,
        IEnumerable<string> roles
    )
        : base(Claims(kind, roles), Provider)
    {
        Kind = kind;
        Asker = asker;
        PlayerSteamId = playerSteamId;
        PlayerName = playerName;
        DiscordUserId = discordUserId;
        ConversationId = conversationId;
        Request = request;
    }

    public QuasarAgentKind Kind { get; }

    /// <summary>The person the assistant acts for; null for the player agent.</summary>
    public ClaimsPrincipal? Asker { get; }

    public string? PlayerSteamId { get; }
    public string? PlayerName { get; }
    public string? DiscordUserId { get; }
    public string ConversationId { get; }

    /// <summary>The text that led to the call, for the audit log.</summary>
    public string Request { get; }

    public string Subject => Kind == QuasarAgentKind.Assistant ? "agent:assistant" : "agent:player";

    internal QuasarAgentIdentity With(IEnumerable<string> roles, ClaimsPrincipal? asker) =>
        new(Kind, asker, PlayerSteamId, PlayerName, DiscordUserId, ConversationId, Request, roles);

    // Keeps the agent type when ASP.NET Core clones a principal.
    public override ClaimsIdentity Clone() =>
        With(FindAll(RoleClaimType).Select(claim => claim.Value), Asker);

    private static IEnumerable<Claim> Claims(QuasarAgentKind kind, IEnumerable<string> roles) =>
        [
            new(
                ClaimTypes.NameIdentifier,
                kind == QuasarAgentKind.Assistant ? "agent:assistant" : "agent:player"
            ),
            new(ClaimTypes.Name, kind == QuasarAgentKind.Assistant ? "Assistant" : "Player agent"),
            new(ProviderClaimType, Provider),
            .. roles.Select(role => new Claim(ClaimTypes.Role, role)),
        ];
}

public static class QuasarAgentPrincipalExtensions
{
    public static bool IsQuasarAgent(
        this ClaimsPrincipal principal,
        [NotNullWhen(true)] out QuasarAgentIdentity? agent
    )
    {
        agent = principal.Identities.OfType<QuasarAgentIdentity>().FirstOrDefault();
        return agent is not null;
    }
}

/// <summary>Builds agent principals. The player agent is built by core only, from an authenticated game session or a stored Discord link.</summary>
public interface IQuasarAgentPrincipals
{
    /// <summary>Throws <see cref="UnauthorizedAccessException"/> unless the asker is an authenticated person who satisfies
    /// <see cref="QuasarPolicyNames.CanUseAssistant"/>.</summary>
    Task<ClaimsPrincipal> CreateAssistantAsync(
        ClaimsPrincipal asker,
        string conversationId,
        string request
    );
}
