using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Authorization.Infrastructure;
using Microsoft.Extensions.Options;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;
using Quasar.Services.Audit;
using Quasar.Services.Auth;
using QuasarPolicyNames = Quasar.Services.Auth.QuasarPolicyNames;

namespace Quasar.Services.Plugins;

public sealed record QuasarOperationSettings(string PluginId, string OperationId,
    IReadOnlyList<string> DefaultPolicies, QuasarOperationRule? EffectiveRule, bool Overridden, string? Error);

public sealed class QuasarOperationService : IQuasarOperationDispatcher, IQuasarAgentOperations
{
    // The hard deny list for agent principals, by operation ID prefix under any plugin ID, so a plugin cannot
    // register an agent-reachable operation in these areas: security, RBAC, auth settings, service principals,
    // Quasar updates and their credentials, Workshop credentials, the Discord bot token, LLM provider settings
    // and keys, and the assistant's own settings. It is code, not configuration.
    private static readonly string[] AgentDeniedOperationPrefixes =
    [
        "security.", "rbac.", "auth.", "service-principals.", "update.", "updates.",
        "workshop.credentials", "discord.token", "assistant.settings", "llm.",
    ];

    private readonly Dictionary<string, QuasarOperation> _operations;
    private readonly RbacConfigCatalog _rbac;
    private readonly QuasarRoleMapper _roles;
    private readonly IAuthorizationService _authorization;
    private readonly IAuthorizationPolicyProvider _policies;
    private readonly IOptions<AuthorizationOptions> _declaredPolicies;
    private readonly QuasarAuditLog _audit;
    private readonly ILogger<QuasarOperationService> _logger;

    public QuasarOperationService(IEnumerable<QuasarOperation> operations, RbacConfigCatalog rbac,
        QuasarRoleMapper roles, IAuthorizationService authorization, IAuthorizationPolicyProvider policies,
        IOptions<AuthorizationOptions> declaredPolicies, QuasarAuditLog audit, ILogger<QuasarOperationService> logger)
    {
        _operations = operations.ToDictionary(operation => operation.Key, StringComparer.Ordinal);
        foreach (var operation in _operations.Values)
            Meta(operation); // Refuses invalid metadata at startup.
        _audit = audit;
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
        var approver = context.ApprovedBy is null ? null : _roles.RefreshRoles(context.ApprovedBy);
        var operation = Find(pluginId, operationId);
        var audited = IsAudited(operation, user);
        var entryId = _audit.NewEntryId();
        var denied = await CheckAccessAsync(user, pluginId, operationId, context.Kind, approver);
        if (denied is not null)
        {
            if (audited)
                await _audit.RecordCallAsync(entryId, user, approver, operation!, request, null, QuasarAuditResult.Refused, denied.Error, context.ProposalId);
            return denied;
        }
        var undo = audited ? new QuasarUndoRecorder(_audit.BlobDirectory(entryId)) : null;
        context = context with { User = user, ApprovedBy = approver, Undo = undo, VerifyAccess = async token =>
        {
            token.ThrowIfCancellationRequested();
            var refused = await CheckAccessAsync(user, pluginId, operationId, context.Kind, approver);
            if (refused is not null) throw new QuasarOperationException(refused.StatusCode, refused.Error!);
        }};

        QuasarOperationResult result;
        try { result = await InvokeAsync(operation!, context, request, cancellationToken); }
        catch (Exception error)
        {
            if (audited)
                await _audit.RecordCallAsync(entryId, user, approver, operation!, request, undo, QuasarAuditResult.Failed, error.Message, context.ProposalId);
            throw;
        }
        if (audited)
            await _audit.RecordCallAsync(entryId, user, approver, operation!, request, undo,
                result.Error is null ? QuasarAuditResult.Ok : QuasarAuditResult.Failed, result.Error, context.ProposalId);
        return result;
    }

    private static async Task<QuasarOperationResult> InvokeAsync(QuasarOperation operation, QuasarOperationContext context,
        JsonElement request, CancellationToken cancellationToken)
    {
        using var deadline = context.Kind == QuasarOperationKind.Json ? CancellationTokenSource.CreateLinkedTokenSource(cancellationToken) : null;
        deadline?.CancelAfter(Meta(operation).Deadline ?? QuasarOperationMetadata.DefaultDeadline);
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

    public async Task<bool> IsAvailableToAgentsAsync(string pluginId, string operationId)
    {
        if (Find(pluginId, operationId) is not { } operation) return false;
        var config = _rbac.GetConfig();
        var rule = Rule(operation, config);
        return config.SdkRulesValid && rule?.Enabled == true && await ValidPoliciesAsync(rule.RequiredPolicies)
            && !IsDeniedToAgents(operation, rule);
    }

    public QuasarOperation? Find(string pluginId, string operationId) => _operations.GetValueOrDefault(pluginId + "/" + operationId);

    /// <summary>A transport's check before it dispatches; audits a refused change operation like the dispatcher does.</summary>
    public async Task<QuasarOperationResult?> CheckCallAsync(ClaimsPrincipal caller, string pluginId,
        string operationId, QuasarOperationKind kind)
    {
        var user = _roles.RefreshRoles(caller);
        var denied = await CheckAccessAsync(user, pluginId, operationId, kind);
        var operation = Find(pluginId, operationId);
        if (denied is not null && IsAudited(operation, user))
            await _audit.RecordCallAsync(_audit.NewEntryId(), user, null, operation!, default, null, QuasarAuditResult.Refused, denied.Error, null);
        return denied;
    }

    /// <summary>Checks a call, also against the asker of an assistant and the approver of a proposal.</summary>
    public async Task<QuasarOperationResult?> CheckAccessAsync(ClaimsPrincipal caller, string pluginId,
        string operationId, QuasarOperationKind kind, ClaimsPrincipal? approver = null)
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
        var agent = user.IsQuasarAgent(out var identity) ? identity : null;
        if (agent is not null && IsDeniedToAgents(operation, rule))
            return new(403, Error: "Assistants cannot use this operation.");
        if (!await SatisfiesAsync(user, rule))
            return new(403, Error: "The caller cannot access this SDK operation.");
        // The assistant never does more than its asker could do by hand.
        if (agent?.Kind == QuasarAgentKind.Assistant && !await IsAllowedPersonAsync(agent.Asker, rule))
            return new(403, Error: "The person asking the assistant cannot access this SDK operation.");
        if (approver is not null && !await IsAllowedDeciderAsync(approver, rule))
            return new(403, Error: "The approver cannot access this SDK operation.");

        return null;
    }

    /// <summary>Whether a person may decide on a recorded call of the operation: approve or reject a proposal, or
    /// undo an applied call. The person needs the operation's current rule. No person satisfies CanActAsPlayer, so
    /// CanControlServers stands in for it there (R15): deciding on a player agent's action needs server control.</summary>
    public async Task<bool> CanDecideAsync(ClaimsPrincipal person, QuasarOperation operation)
    {
        var config = _rbac.GetConfig();
        var rule = Rule(operation, config);
        return config.SdkRulesValid && rule?.Enabled == true && await ValidPoliciesAsync(rule.RequiredPolicies)
            && await IsAllowedDeciderAsync(person, rule);
    }

    private Task<bool> IsAllowedDeciderAsync(ClaimsPrincipal person, QuasarOperationRule rule) =>
        IsAllowedPersonAsync(_roles.RefreshRoles(person), new()
        {
            RequiredPolicies = rule.RequiredPolicies!.Select(policy =>
                policy.Equals(QuasarPolicyNames.CanActAsPlayer, StringComparison.OrdinalIgnoreCase)
                    ? QuasarPolicyNames.CanControlServers : policy).ToList(),
        });

    internal static QuasarOperationMetadata Meta(QuasarOperation operation) =>
        operation.Metadata.ResolveFor(operation.Kind, operation.OperationId);

    internal static bool IsDeniedToAgents(QuasarOperation operation, QuasarOperationRule? rule) =>
        Meta(operation).AgentAccess == QuasarAgentAccess.Denied
        || rule?.RequiredPolicies?.Any(policy => policy.Equals(QuasarPolicyNames.CanManageSecurity, StringComparison.OrdinalIgnoreCase)
            || policy.Equals(QuasarPolicyNames.CanShutdownQuasar, StringComparison.OrdinalIgnoreCase)) == true
        || AgentDeniedOperationPrefixes.Any(prefix => operation.OperationId.StartsWith(prefix, StringComparison.Ordinal));

    private static bool IsAudited(QuasarOperation? operation, ClaimsPrincipal user) =>
        operation is not null && Meta(operation).Effect == QuasarOperationEffect.Change && user.Identity?.IsAuthenticated == true;

    private async Task<bool> SatisfiesAsync(ClaimsPrincipal user, QuasarOperationRule rule)
    {
        foreach (var policy in rule.RequiredPolicies!)
            if (!(await _authorization.AuthorizeAsync(user, null, policy)).Succeeded)
                return false;
        return true;
    }

    private async Task<bool> IsAllowedPersonAsync(ClaimsPrincipal? person, QuasarOperationRule rule) =>
        person?.Identity?.IsAuthenticated == true && !person.IsQuasarServicePrincipal() && !person.IsQuasarAgent(out _)
        && await SatisfiesAsync(person, rule);

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

    public async Task SaveAgentRolesAsync(ClaimsPrincipal caller, QuasarAgentKind kind, IReadOnlyList<string> roles,
        CancellationToken token = default)
    {
        var savedRoles = roles.Select(role => role.Trim()).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        await _rbac.UpdateAsync(async config =>
        {
            await RequireSecurityAsync(caller);
            if (savedRoles.Contains(QuasarRoles.Admin, StringComparer.OrdinalIgnoreCase))
                throw new ArgumentException("An assistant or player agent can never hold the admin role.");
            if (savedRoles.Any(role => !QuasarRoles.All.Contains(role)))
                throw new ArgumentException("Select valid Quasar roles.");
            config.AgentRoles[RbacConfig.AgentRoleKey(kind)] = savedRoles;
        }, token);
        _logger.LogInformation("Agent roles changed by {Actor} for {Agent}: {Roles}", Actor(caller), kind, string.Join(",", savedRoles));
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

    internal static QuasarOperationRule? Rule(QuasarOperation operation, RbacConfig config) =>
        config.OperationOverrides.TryGetValue(operation.Key, out var rule)
            ? rule?.Clone() : new() { RequiredPolicies = operation.DefaultPolicies.ToList() };

    private static string Actor(ClaimsPrincipal caller) => caller.FindFirstValue(ClaimTypes.NameIdentifier) ?? "unknown";
}
