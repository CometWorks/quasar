namespace Quasar.Services.Auth;

public sealed class QuasarAuthOptions
{
    public bool Enabled { get; set; } = true;
    public bool RequireHttpsForPublicAccess { get; set; } = true;
    public string DefaultProvider { get; set; } = QuasarAuthSchemes.Steam;
    public TrustedNetworkBypassOptions TrustedNetworkBypass { get; set; } = new();
    public SteamAuthOptions Steam { get; set; } = new();
    public ExternalProviderOptions ExternalProviders { get; set; } = new();
    public WorkshopOptions Workshop { get; set; } = new();

    public static QuasarAuthOptions Create(IConfiguration configuration)
    {
        var options = configuration.GetSection("Quasar:Auth").Get<QuasarAuthOptions>() ?? new QuasarAuthOptions();
        options.Normalize();
        return options;
    }

    private void Normalize()
    {
        DefaultProvider = string.IsNullOrWhiteSpace(DefaultProvider)
            ? QuasarAuthSchemes.Steam
            : DefaultProvider.Trim();

        TrustedNetworkBypass ??= new TrustedNetworkBypassOptions();
        Steam ??= new SteamAuthOptions();
        ExternalProviders ??= new ExternalProviderOptions();
        Workshop ??= new WorkshopOptions();

        TrustedNetworkBypass.Normalize();
        Steam.Normalize();
        ExternalProviders.Normalize();
        Workshop.Normalize();
    }
}

public sealed class TrustedNetworkBypassOptions
{
    public bool AllowLoopback { get; set; } = true;
    public bool AllowSameSubnet { get; set; } = true;
    public List<string> TrustedProxies { get; set; } = [];
    public List<string> Roles { get; set; } = [QuasarRoles.Admin];

    public void Normalize()
    {
        TrustedProxies = NormalizeStrings(TrustedProxies);
        Roles = NormalizeRoles(Roles, [QuasarRoles.Admin]);
    }

    private static List<string> NormalizeStrings(IEnumerable<string>? values) =>
        values?
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];

    private static List<string> NormalizeRoles(IEnumerable<string>? values, IEnumerable<string> fallback)
    {
        var roles = NormalizeStrings(values)
            .Where(QuasarRoles.All.Contains)
            .ToList();

        return roles.Count > 0 ? roles : fallback.ToList();
    }
}

public sealed class SteamAuthOptions
{
    public bool Enabled { get; set; } = true;

    public void Normalize()
    {
    }
}

public sealed class ExternalProviderOptions
{
    public OidcProviderOptions Oidc { get; set; } = new();

    public void Normalize()
    {
        Oidc ??= new OidcProviderOptions();
        Oidc.Normalize();
    }
}

public sealed class OidcProviderOptions
{
    public bool Enabled { get; set; }
    public string Authority { get; set; } = string.Empty;
    public string ClientId { get; set; } = string.Empty;
    public string ClientSecret { get; set; } = string.Empty;
    public List<string> Scopes { get; set; } = ["openid", "profile", "email"];
    public string NameClaim { get; set; } = "name";
    public string SubjectClaim { get; set; } = "sub";
    public string EmailClaim { get; set; } = "email";
    public string RoleClaim { get; set; } = "roles";

    public void Normalize()
    {
        Authority = Authority.Trim();
        ClientId = ClientId.Trim();
        ClientSecret = ClientSecret.Trim();
        Scopes = Scopes
            .Where(scope => !string.IsNullOrWhiteSpace(scope))
            .Select(scope => scope.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (Scopes.Count == 0)
            Scopes = ["openid", "profile", "email"];

        NameClaim = NormalizeClaimName(NameClaim, "name");
        SubjectClaim = NormalizeClaimName(SubjectClaim, "sub");
        EmailClaim = NormalizeClaimName(EmailClaim, "email");
        RoleClaim = NormalizeClaimName(RoleClaim, "roles");
    }

    private static string NormalizeClaimName(string value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
}

public sealed class WorkshopOptions
{
    public bool Enabled { get; set; } = true;
    public int AppId { get; set; } = SteamAuthConstants.SpaceEngineersAppId;
    public string WebApiKey { get; set; } = string.Empty;
    public int PopularLimit { get; set; } = 50;
    public int SearchLimit { get; set; } = 50;
    public List<string> RequiredTags { get; set; } = ["Mod"];
    public string MatchingFileType { get; set; } = "Items";
    public string PopularQueryType { get; set; } = "RankedByTotalUniqueSubscriptions";
    public string SearchQueryType { get; set; } = "RankedByTextSearch";
    public int CacheMaxAgeSeconds { get; set; } = 300;
    public int SearchDebounceMilliseconds { get; set; } = 350;

    public void Normalize()
    {
        AppId = AppId <= 0 ? SteamAuthConstants.SpaceEngineersAppId : AppId;
        WebApiKey = WebApiKey.Trim();
        PopularLimit = Math.Clamp(PopularLimit, 1, 50);
        SearchLimit = Math.Clamp(SearchLimit, 1, 50);
        RequiredTags = RequiredTags
            .Where(tag => !string.IsNullOrWhiteSpace(tag))
            .Select(tag => tag.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        MatchingFileType = string.IsNullOrWhiteSpace(MatchingFileType) ? "Items" : MatchingFileType.Trim();
        PopularQueryType = string.IsNullOrWhiteSpace(PopularQueryType) ? "RankedByTotalUniqueSubscriptions" : PopularQueryType.Trim();
        SearchQueryType = string.IsNullOrWhiteSpace(SearchQueryType) ? "RankedByTextSearch" : SearchQueryType.Trim();
        CacheMaxAgeSeconds = Math.Clamp(CacheMaxAgeSeconds, 30, 3600);
        SearchDebounceMilliseconds = Math.Clamp(SearchDebounceMilliseconds, 100, 2000);
    }
}
