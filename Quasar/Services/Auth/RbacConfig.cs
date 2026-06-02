using System.Text.Json.Serialization;

namespace Quasar.Services.Auth;

public sealed class RbacConfig
{
    public List<SubjectRoleMapping> SubjectRoleMappings { get; set; } = [];
    public List<ClaimRoleMapping> ClaimRoleMappings { get; set; } = [];
    public Dictionary<string, List<string>> PolicyOverrides { get; set; } = new(StringComparer.OrdinalIgnoreCase);

    public RbacConfig Clone() => new()
    {
        SubjectRoleMappings = SubjectRoleMappings.Select(mapping => mapping.Clone()).ToList(),
        ClaimRoleMappings = ClaimRoleMappings.Select(mapping => mapping.Clone()).ToList(),
        PolicyOverrides = PolicyOverrides.ToDictionary(
            pair => pair.Key,
            pair => pair.Value.ToList(),
            StringComparer.OrdinalIgnoreCase),
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

        return normalized;
    }

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
