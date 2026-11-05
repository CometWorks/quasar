using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Options;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Services.Auth;

namespace Quasar.Services.Plugins;

public sealed record QuasarOperationSettings(string PluginId, string OperationId,
    IReadOnlyList<string> DefaultPolicies, QuasarOperationRule? EffectiveRule, bool Overridden, string? Error);

public sealed class QuasarOperationService : IQuasarOperationDispatcher
{
    private readonly Dictionary<string, QuasarOperation> _operations;
    private readonly RbacConfigCatalog _rbac;
    private readonly QuasarRoleMapper _roles;
    private readonly IAuthorizationService _authorization;
    private readonly IAuthorizationPolicyProvider _policies;
    private readonly IOptions<AuthorizationOptions> _declaredPolicies;
    private readonly ILogger<QuasarOperationService> _logger;

    public QuasarOperationService(IEnumerable<QuasarOperation> operations, RbacConfigCatalog rbac,
        QuasarRoleMapper roles, IAuthorizationService authorization, IAuthorizationPolicyProvider policies,
        IOptions<AuthorizationOptions> declaredPolicies, ILogger<QuasarOperationService> logger)
    {
        _operations = operations.ToDictionary(operation => operation.Key, StringComparer.Ordinal);
        _rbac = rbac;
        _roles = roles;
        _authorization = authorization;
        _policies = policies;
        _declaredPolicies = declaredPolicies;
        _logger = logger;
    }

    public async Task<QuasarOperationResult> DispatchAsync(QuasarOperationContext context, string pluginId,
        string operationId, JsonElement request, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var user = _roles.RefreshRoles(context.User);
        var denied = await CheckAccessAsync(user, pluginId, operationId, context.Kind);
        if (denied is not null) return denied;
        var operation = _operations[pluginId + "/" + operationId];
        context = context with { User = user, VerifyAccess = async token =>
        {
            token.ThrowIfCancellationRequested();
            var refused = await CheckAccessAsync(user, pluginId, operationId, context.Kind);
            if (refused is not null) throw new QuasarOperationException(refused.StatusCode, refused.Error!);
        }};

        using var deadline = context.Kind == QuasarOperationKind.Json ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        deadline?.CancelAfter(TimeSpan.FromSeconds(30));
        try
        {
            var value = await operation.InvokeAsync(context, request, deadline?.Token ?? cancellationToken);
            return new(200, value);
        }
        catch (QuasarOperationException error) { return new(error.StatusCode, Error: error.Message); }
        catch (JsonException) { return new(400, Error: "Invalid operation request."); }
        catch (ArgumentException) { return new(400, Error: "Invalid operation input."); }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        { return new(504, Error: "The operation deadline expired; a write outcome may be unknown."); }
    }

    public QuasarOperation? Find(string pluginId, string operationId) => _operations.GetValueOrDefault(pluginId + "/" + operationId);

    public async Task<QuasarOperationResult?> CheckAccessAsync(ClaimsPrincipal caller, string pluginId,
        string operationId, QuasarOperationKind kind)
    {
        var user = _roles.RefreshRoles(caller);
        if (user.Identity?.IsAuthenticated != true) return new(401, Error: "Authentication is required.");
        if (user.IsQuasarServicePrincipal()) return new(403, Error: "Service credentials are limited to their existing machine API.");
        if (!_operations.TryGetValue(pluginId + "/" + operationId, out var operation))
            return new(404, Error: "SDK operation not found.");
        if (operation.Kind != kind) return new(400, Error: "Use the operation's registered transport primitive.");
        var config = _rbac.GetConfig();
        var rule = Rule(operation, config);
        if (!config.SdkRulesValid || rule?.Enabled != true || !await ValidPoliciesAsync(rule.RequiredPolicies))
            return new(403, Error: "SDK operation is disabled or its authorization rule is invalid.");
        foreach (var policy in rule.RequiredPolicies!)
            if (!(await _authorization.AuthorizeAsync(user, null, policy)).Succeeded)
                return new(403, Error: "The caller cannot access this SDK operation.");

        return null;
    }

    public async Task<IReadOnlyList<QuasarOperationSettings>> GetSettingsAsync(ClaimsPrincipal caller)
    {
        await RequireSecurityAsync(caller);
        var config = _rbac.GetConfig();
        var result = new List<QuasarOperationSettings>();
        foreach (var operation in _operations.Values.OrderBy(operation => operation.Key, StringComparer.Ordinal))
        {
            var rule = Rule(operation, config);
            var error = !config.SdkRulesValid || !await ValidPoliciesAsync(rule?.RequiredPolicies)
                ? "Invalid authorization configuration; access is denied." : null;
            result.Add(new(operation.PluginId, operation.OperationId, operation.DefaultPolicies, rule,
                config.OperationOverrides.ContainsKey(operation.Key), error));
        }
        return result;
    }

    public async Task SaveRuleAsync(ClaimsPrincipal caller, string pluginId, string operationId,
        QuasarOperationRule? rule, CancellationToken token = default)
    {
        var key = pluginId + "/" + operationId;
        var savedRule = rule?.Clone();
        await _rbac.UpdateAsync(async config =>
        {
            await RequireSecurityAsync(caller);
            if (!_operations.ContainsKey(key)) throw new ArgumentException("SDK operation not found.");
            if (savedRule is not null && !await ValidPoliciesAsync(savedRule.RequiredPolicies))
                throw new ArgumentException("Select at least one registered authorization policy.");
            if (savedRule is null) config.OperationOverrides.Remove(key);
            else config.OperationOverrides[key] = savedRule;
        }, token);
        _logger.LogInformation("SDK authorization changed by {Actor} for {Operation}: enabled={Enabled}, policies={Policies}",
            Actor(caller), key, savedRule?.Enabled, savedRule is null ? "defaults" : string.Join(",", savedRule.RequiredPolicies!));
    }

    public async Task SavePolicyRolesAsync(ClaimsPrincipal caller, string policy, IReadOnlyList<string> roles,
        CancellationToken token = default)
    {
        var savedRoles = roles.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await _rbac.UpdateAsync(async config =>
        {
            await RequireSecurityAsync(caller);
            var declared = _declaredPolicies.Value.GetPolicy(policy);
            if (declared is null || !declared.Requirements.Any(requirement => requirement is RolesAuthorizationRequirement)
                || savedRoles.Any(role => !QuasarRoles.All.Contains(role)))
                throw new ArgumentException("Select a role-based policy and valid Quasar roles.");
            if (policy.Equals(QuasarPolicyNames.CanManageSecurity, StringComparison.OrdinalIgnoreCase)
                && !savedRoles.Contains(QuasarRoles.Admin, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("Security administration must retain the admin role.");
            config.PolicyOverrides[policy] = savedRoles;
        }, token);
        _logger.LogInformation("Policy role grants changed by {Actor} for {Policy}: {Roles}", Actor(caller), policy, string.Join(",", savedRoles));
    }

    private async Task RequireSecurityAsync(ClaimsPrincipal caller)
    {
        var user = _roles.RefreshRoles(caller);
        if (user.Identity?.IsAuthenticated != true || !(await _authorization.AuthorizeAsync(user, null, QuasarPolicyNames.CanManageSecurity)).Succeeded)
            throw new UnauthorizedAccessException("Security administration permission is required.");
    }

    private async Task<bool> ValidPoliciesAsync(IReadOnlyList<string>? policies)
    {
        if (policies is not { Count: > 0 and <= 16 }) return false;
        foreach (var policy in policies)
            if (string.IsNullOrWhiteSpace(policy) || policy.Length > 128 || await _policies.GetPolicyAsync(policy) is null)
                return false;
        return true;
    }

    private static QuasarOperationRule? Rule(QuasarOperation operation, RbacConfig config) =>
        config.OperationOverrides.TryGetValue(operation.Key, out var rule)
            ? rule?.Clone() : new() { RequiredPolicies = operation.DefaultPolicies.ToList() };

    private static string Actor(ClaimsPrincipal caller) => caller.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
}
