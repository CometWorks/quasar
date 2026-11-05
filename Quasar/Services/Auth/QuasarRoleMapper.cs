using System.Security.Claims;
using Quasar.Plugin.Abstractions.Security;

namespace Quasar.Services.Auth;

public sealed class QuasarRoleMapper
{
    private readonly QuasarAuthOptions _options;
    private readonly RbacConfigCatalog _rbacConfigCatalog;

    public QuasarRoleMapper(QuasarAuthOptions options, RbacConfigCatalog rbacConfigCatalog)
    {
        _options = options;
        _rbacConfigCatalog = rbacConfigCatalog;
    }

    public bool IsSteamIdAllowed(string steamId)
    {
        return !string.IsNullOrWhiteSpace(steamId);
    }

    public IReadOnlyList<string> GetSteamRoles(string steamId)
    {
        return _rbacConfigCatalog.GetSubjectRoles(QuasarAuthSchemes.Steam, steamId)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    public ClaimsPrincipal CreateSteamPrincipal(string steamId)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, steamId),
            new(ClaimTypes.Name, steamId),
            new(QuasarClaimTypes.Provider, QuasarAuthSchemes.Steam),
            new(QuasarClaimTypes.SteamId, steamId),
            new(QuasarClaimTypes.SteamProfileUrl, $"https://steamcommunity.com/profiles/{steamId}"),
        };

        foreach (var role in GetSteamRoles(steamId))
            claims.Add(new Claim(ClaimTypes.Role, role));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, QuasarAuthSchemes.Steam));
    }

    public ClaimsPrincipal CreateTrustedNetworkPrincipal()
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, QuasarAuthSchemes.TrustedNetwork),
            new(ClaimTypes.Name, "Trusted network"),
            new(QuasarClaimTypes.Provider, QuasarAuthSchemes.TrustedNetwork),
        };

        foreach (var role in _options.TrustedNetworkBypass.Roles)
            claims.Add(new Claim(ClaimTypes.Role, role));

        return new ClaimsPrincipal(new ClaimsIdentity(claims, QuasarAuthSchemes.TrustedNetwork));
    }

    public ClaimsPrincipal RefreshRoles(ClaimsPrincipal principal)
    {
        // An agent keeps its in-process identity type and asker; it gets only its own identity and never admin.
        if (principal.IsQuasarAgent(out var agent))
            return new ClaimsPrincipal(agent.With(GetAgentRoles(agent.Kind),
                agent.Asker is null ? null : RefreshRoles(agent.Asker)));

        var provider = principal.FindFirst(QuasarClaimTypes.Provider)?.Value;
        IReadOnlyList<string>? roles = provider switch
        {
            QuasarAuthSchemes.Steam => GetSteamRoles(
                principal.FindFirst(QuasarClaimTypes.SteamId)?.Value ??
                principal.FindFirst(ClaimTypes.NameIdentifier)?.Value ?? string.Empty),
            QuasarAuthSchemes.TrustedNetwork => _options.TrustedNetworkBypass.Roles,
            // Claims that only look like an agent's grant nothing.
            QuasarAgentIdentity.Provider => [],
            _ => null,
        };

        return roles is null ? principal : ReplaceRoles(principal, roles);
    }

    public IReadOnlyList<string> GetAgentRoles(QuasarAgentKind kind) =>
        _rbacConfigCatalog.GetConfig().AgentRoles.GetValueOrDefault(RbacConfig.AgentRoleKey(kind), [])
            .Where(role => !string.Equals(role, QuasarRoles.Admin, StringComparison.OrdinalIgnoreCase))
            .ToList();

    internal static ClaimsPrincipal ReplaceRoles(ClaimsPrincipal principal, IEnumerable<string> roles)
    {
        var identities = principal.Identities
            .Select(identity => new ClaimsIdentity(
                identity.Claims.Where(claim => claim.Type != identity.RoleClaimType),
                identity.AuthenticationType,
                identity.NameClaimType,
                identity.RoleClaimType))
            .ToList();
        var roleIdentity = identities.FirstOrDefault(identity => identity.IsAuthenticated) ?? identities.FirstOrDefault();

        if (roleIdentity is null)
        {
            roleIdentity = new ClaimsIdentity();
            identities.Add(roleIdentity);
        }

        foreach (var role in roles.Distinct(StringComparer.OrdinalIgnoreCase))
            roleIdentity.AddClaim(new Claim(roleIdentity.RoleClaimType, role));

        return new ClaimsPrincipal(identities);
    }
}
