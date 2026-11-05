using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Options;

namespace Quasar.Services.Auth;

/// <summary>Evaluates runtime role overrides without caching a stale authorization policy.</summary>
public sealed class QuasarAuthorizationPolicyProvider(IOptions<AuthorizationOptions> options, RbacConfigCatalog rbac)
    : DefaultAuthorizationPolicyProvider(options)
{
    public override bool AllowsCachingPolicies => false;

    public override async Task<AuthorizationPolicy?> GetPolicyAsync(string policyName)
    {
        var declared = await base.GetPolicyAsync(policyName);
        if (declared is null || !rbac.GetConfig().PolicyOverrides.TryGetValue(policyName, out var roles))
            return declared;

        var builder = new AuthorizationPolicyBuilder(declared.AuthenticationSchemes.ToArray()).RequireAuthenticatedUser();
        // Role overrides never replace scope, ownership, or other non-role requirements.
        builder.AddRequirements(declared.Requirements.Where(requirement => requirement is not RolesAuthorizationRequirement).ToArray());
        if (roles.Count == 0 || !declared.Requirements.Any(requirement => requirement is RolesAuthorizationRequirement))
            builder.RequireAssertion(_ => false);
        else
            builder.RequireRole(roles);
        return builder.Build();
    }
}
