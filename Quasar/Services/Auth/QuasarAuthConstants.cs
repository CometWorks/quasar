using System.Security.Claims;

namespace Quasar.Services.Auth;

public static class QuasarAuthSchemes
{
    public const string Cookie = "QuasarCookie";
    public const string TrustedNetwork = "QuasarTrustedNetwork";
    public const string ServicePrincipal = "QuasarServicePrincipal";
    public const string Steam = "Steam";
}

public static class QuasarClaimTypes
{
    public const string Provider = "quasar:provider";
    public const string SteamId = "steamid";
    public const string SteamProfileUrl = "steam_profile_url";
    public const string Scope = "quasar:scope";
    public const string Cluster = "quasar:cluster";
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
    public const string ClusterQuery = "ClusterQuery";
    public const string ClusterManage = "ClusterManage";
    public const string CanView = "CanView";
    public const string CanEditConfigs = "CanEditConfigs";
    public const string CanEditServers = "CanEditServers";
    public const string CanControlServers = "CanControlServers";
    public const string CanManageDiscord = "CanManageDiscord";
    public const string CanManageAppearance = "CanManageAppearance";
    public const string CanManageSecurity = "CanManageSecurity";
    public const string CanShutdownQuasar = "CanShutdownQuasar";
}

public static class QuasarScopes
{
    public const string ClusterQuery = "cluster.query";
    public const string ClusterManage = "cluster.manage";
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
    public static bool IsQuasarServicePrincipal(this ClaimsPrincipal principal) =>
        string.Equals(principal.FindFirstValue(QuasarClaimTypes.Provider),
            QuasarAuthSchemes.ServicePrincipal, StringComparison.Ordinal);

    public static bool CanQueryCluster(this ClaimsPrincipal principal, string uniqueName) =>
        !principal.IsQuasarServicePrincipal()
        || principal.FindAll(QuasarClaimTypes.Cluster).Any(claim => claim.Value == "*"
            || string.Equals(claim.Value, uniqueName, StringComparison.OrdinalIgnoreCase));

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
