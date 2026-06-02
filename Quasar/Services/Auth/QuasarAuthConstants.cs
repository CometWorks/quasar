using System.Security.Claims;

namespace Quasar.Services.Auth;

public static class QuasarAuthSchemes
{
    public const string Cookie = "QuasarCookie";
    public const string TrustedNetwork = "QuasarTrustedNetwork";
    public const string Steam = "Steam";
}

public static class QuasarClaimTypes
{
    public const string Provider = "quasar:provider";
    public const string SteamId = "steamid";
    public const string SteamProfileUrl = "steam_profile_url";
}

public static class QuasarRoles
{
    public const string Viewer = "viewer";
    public const string Editor = "editor";
    public const string Admin = "admin";

    public static readonly IReadOnlySet<string> All = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
    {
        Viewer,
        Editor,
        Admin,
    };
}

public static class QuasarPolicyNames
{
    public const string CanView = "CanView";
    public const string CanEditConfigs = "CanEditConfigs";
    public const string CanEditInstances = "CanEditInstances";
    public const string CanControlServers = "CanControlServers";
    public const string CanManageDiscord = "CanManageDiscord";
    public const string CanManageAppearance = "CanManageAppearance";
    public const string CanManageSecurity = "CanManageSecurity";
    public const string CanShutdownQuasar = "CanShutdownQuasar";
}

public static class SteamAuthConstants
{
    public const string OpenIdEndpoint = "https://steamcommunity.com/openid/";
    public const int SpaceEngineersAppId = 244850;
    public const string ClaimedIdPrefix = "https://steamcommunity.com/openid/id/";
    public const string ClaimedIdPrefixHttp = "http://steamcommunity.com/openid/id/";
}

public static class ClaimsPrincipalExtensions
{
    public static string? GetQuasarDisplayName(this ClaimsPrincipal principal)
    {
        var provider = principal.FindFirstValue(QuasarClaimTypes.Provider);
        var steamId = principal.FindFirstValue(QuasarClaimTypes.SteamId);
        if (!string.IsNullOrWhiteSpace(steamId))
            return string.Equals(provider, QuasarAuthSchemes.TrustedNetwork, StringComparison.OrdinalIgnoreCase)
                ? "Trusted network"
                : $"Steam {steamId}";

        return principal.Identity?.Name;
    }
}
