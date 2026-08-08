using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Net.Http.Headers;

namespace Quasar.Services.Auth;

public sealed class ServicePrincipalAuthenticator
{
    private readonly QuasarAuthOptions _options;
    private readonly Func<string, string?> _readEnvironmentVariable;

    public ServicePrincipalAuthenticator(QuasarAuthOptions options)
        : this(options, Environment.GetEnvironmentVariable)
    {
    }

    internal ServicePrincipalAuthenticator(
        QuasarAuthOptions options,
        Func<string, string?> readEnvironmentVariable)
    {
        _options = options;
        _readEnvironmentVariable = readEnvironmentVariable;
    }

    public bool HasBearerAuthorization(HttpRequest request) =>
        request.Headers.TryGetValue(HeaderNames.Authorization, out var values)
        && values.Any(value => value?.StartsWith("Bearer", StringComparison.OrdinalIgnoreCase) == true);

    public bool TryAuthenticate(HttpContext context)
    {
        if (!TryReadBearerToken(context.Request, out string? presented))
            return false;

        ServicePrincipalOptions? match = null;
        foreach (ServicePrincipalOptions candidate in _options.ServicePrincipals)
        {
            string? configured = _readEnvironmentVariable(candidate.TokenEnvironmentVariable);
            if (configured is not { Length: >= 32 } || !TokenEquals(presented, configured))
                continue;
            if (match != null)
                return false;
            match = candidate;
        }
        if (match == null)
            return false;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, match.Name),
            new(ClaimTypes.Name, match.Name),
            new(QuasarClaimTypes.Provider, QuasarAuthSchemes.ServicePrincipal),
        };
        claims.AddRange(match.Scopes.Select(scope => new Claim(QuasarClaimTypes.Scope, scope)));
        claims.AddRange(match.Clusters.Select(cluster => new Claim(QuasarClaimTypes.Cluster, cluster)));
        context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, QuasarAuthSchemes.ServicePrincipal));
        return true;
    }

    private static bool TryReadBearerToken(HttpRequest request, out string token)
    {
        token = string.Empty;
        if (!request.Headers.TryGetValue(HeaderNames.Authorization, out var values) || values.Count != 1)
            return false;
        string value = values[0] ?? string.Empty;
        const string prefix = "Bearer ";
        if (!value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            return false;
        token = value[prefix.Length..].Trim();
        return token.Length != 0;
    }

    private static bool TokenEquals(string presented, string configured) =>
        CryptographicOperations.FixedTimeEquals(
            SHA256.HashData(Encoding.UTF8.GetBytes(presented)),
            SHA256.HashData(Encoding.UTF8.GetBytes(configured)));
}
