using System.Security.Claims;

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
}
