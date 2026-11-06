using System.Text.Json.Serialization;
using Quasar.Plugin.Abstractions.Operations;
using Quasar.Plugin.Abstractions.Security;

namespace Quasar.Services.Auth;

public sealed class RbacConfig
{
    public List<SubjectRoleMapping> SubjectRoleMappings { get; set; } = [];
    public List<ClaimRoleMapping> ClaimRoleMappings { get; set; } = [];
    public Dictionary<string, List<string>> PolicyOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public Dictionary<string, QuasarOperationRule?> OperationOverrides { get; set; } = new(StringComparer.Ordinal);
    // Roles of the agent principals, keyed "assistant", "player" and "monitor". Never admin.
    public Dictionary<string, List<string>> AgentRoles { get; set; } = DefaultAgentRoles();
    // Discord users who may use /assistant, as the Quasar subject they act as.
    public List<DiscordSubjectMapping> DiscordSubjects { get; set; } = [];
    [JsonIgnore] internal bool SdkRulesValid { get; set; } = true;

    public RbacConfig Clone() => new()
    {
        SubjectRoleMappings = SubjectRoleMappings.Select(mapping => mapping.Clone()).ToList(),
        ClaimRoleMappings = ClaimRoleMappings.Select(mapping => mapping.Clone()).ToList(),
        PolicyOverrides = PolicyOverrides.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList(),
            StringComparer.OrdinalIgnoreCase),
        OperationOverrides = OperationOverrides.ToDictionary(pair => pair.Key, pair => pair.Value?.Clone(), StringComparer.Ordinal),
        AgentRoles = (AgentRoles ?? DefaultAgentRoles()).ToDictionary(pair => pair.Key, pair => pair.Value?.ToList() ?? [], StringComparer.OrdinalIgnoreCase),
        DiscordSubjects = (DiscordSubjects ?? []).Select(mapping => mapping.Clone()).ToList(),
        SdkRulesValid = SdkRulesValid,
    };

    public static RbacConfig Normalize(RbacConfig? config)
    {
        var normalized = config?.Clone() ?? new RbacConfig();
        normalized.SubjectRoleMappings = normalized.SubjectRoleMappings
            .Select(SubjectRoleMapping.Normalize)
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.Provider) &&
                              !string.IsNullOrWhiteSpace(mapping.Subject) &&
                              mapping.Roles.Count > 0)
            .DistinctBy(mapping => $"{mapping.Provider}\u001f{mapping.Subject}", StringComparer.OrdinalIgnoreCase)
            .OrderBy(mapping => mapping.Provider, StringComparer.OrdinalIgnoreCase)
            .ThenBy(mapping => mapping.Subject, StringComparer.OrdinalIgnoreCase)
            .ToList();

        normalized.ClaimRoleMappings = normalized.ClaimRoleMappings
            .Select(ClaimRoleMapping.Normalize)
            .Where(mapping => !string.IsNullOrWhiteSpace(mapping.Provider) &&
                              !string.IsNullOrWhiteSpace(mapping.Claim) &&
                              !string.IsNullOrWhiteSpace(mapping.Value) &&
                              mapping.Roles.Count > 0)
            .ToList();

        normalized.PolicyOverrides = normalized.PolicyOverrides
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key))
            .ToDictionary(
                pair => pair.Key.Trim(),
                pair => NormalizeRoles(pair.Value),
                StringComparer.OrdinalIgnoreCase);

        var agentRoles = normalized.AgentRoles;
        normalized.AgentRoles = DefaultAgentRoles().ToDictionary(
            pair => pair.Key,
            pair => NormalizeRoles(agentRoles.GetValueOrDefault(pair.Key) ?? pair.Value)
                .Where(role => role != QuasarRoles.Admin).ToList(),
            StringComparer.OrdinalIgnoreCase);

        normalized.DiscordSubjects = normalized.DiscordSubjects
            .Select(DiscordSubjectMapping.Normalize)
            .Where(mapping => ulong.TryParse(mapping.DiscordUserId, out _) && !string.IsNullOrWhiteSpace(mapping.Subject))
            .DistinctBy(mapping => mapping.DiscordUserId, StringComparer.Ordinal)
            .OrderBy(mapping => mapping.DiscordUserId, StringComparer.Ordinal)
            .ToList();

        return normalized;
    }

    public static string AgentRoleKey(QuasarAgentKind kind) => kind switch
    {
        QuasarAgentKind.Assistant => "assistant",
        QuasarAgentKind.Monitor => "monitor",
        _ => "player",
    };

    private static Dictionary<string, List<string>> DefaultAgentRoles() => new(StringComparer.OrdinalIgnoreCase)
    {
        [AgentRoleKey(QuasarAgentKind.Assistant)] = [QuasarRoles.Editor],
        [AgentRoleKey(QuasarAgentKind.Player)] = [],
        [AgentRoleKey(QuasarAgentKind.Monitor)] = [QuasarRoles.Editor],
    };

    internal static List<string> NormalizeRoles(IEnumerable<string>? roles) =>
        roles?
            .Where(role => !string.IsNullOrWhiteSpace(role))
            .Select(role => role.Trim().ToLowerInvariant())
            .Where(QuasarRoles.All.Contains)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
}

public sealed class SubjectRoleMapping
{
    public string Provider { get; set; } = QuasarAuthSchemes.Steam;
    public string Subject { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = [];

    [JsonIgnore]
    public string RoleText
    {
        get => string.Join(", ", Roles);
        set => Roles = ParseRoles(value);
    }

    public SubjectRoleMapping Clone() => new()
    {
        Provider = Provider,
        Subject = Subject,
        Roles = Roles.ToList(),
    };

    public static SubjectRoleMapping Normalize(SubjectRoleMapping mapping) => new()
    {
        Provider = string.IsNullOrWhiteSpace(mapping.Provider) ? QuasarAuthSchemes.Steam : mapping.Provider.Trim(),
        Subject = mapping.Subject.Trim(),
        Roles = RbacConfig.NormalizeRoles(mapping.Roles),
    };

    public static List<string> ParseRoles(string? value) =>
        RbacConfig.NormalizeRoles((value ?? string.Empty).Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries));
}

/// <summary>A Discord user acting as a Quasar subject for /assistant. Only Steam subjects can be mapped in v1.</summary>
public sealed class DiscordSubjectMapping
{
    public string DiscordUserId { get; set; } = string.Empty;
    public string Provider { get; set; } = QuasarAuthSchemes.Steam;
    public string Subject { get; set; } = string.Empty;

    public DiscordSubjectMapping Clone() => new()
    {
        DiscordUserId = DiscordUserId,
        Provider = Provider,
        Subject = Subject,
    };

    public static DiscordSubjectMapping Normalize(DiscordSubjectMapping mapping) => new()
    {
        DiscordUserId = (mapping.DiscordUserId ?? string.Empty).Trim(),
        Provider = string.IsNullOrWhiteSpace(mapping.Provider) ? QuasarAuthSchemes.Steam : mapping.Provider.Trim(),
        Subject = (mapping.Subject ?? string.Empty).Trim(),
    };
}

public sealed class ClaimRoleMapping
{
    public string Provider { get; set; } = "Oidc";
    public string Claim { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
    public List<string> Roles { get; set; } = [];

    public ClaimRoleMapping Clone() => new()
    {
        Provider = Provider,
        Claim = Claim,
        Value = Value,
        Roles = Roles.ToList(),
    };

    public static ClaimRoleMapping Normalize(ClaimRoleMapping mapping) => new()
    {
        Provider = string.IsNullOrWhiteSpace(mapping.Provider) ? "Oidc" : mapping.Provider.Trim(),
        Claim = mapping.Claim.Trim(),
        Value = mapping.Value.Trim(),
        Roles = RbacConfig.NormalizeRoles(mapping.Roles),
    };
}
