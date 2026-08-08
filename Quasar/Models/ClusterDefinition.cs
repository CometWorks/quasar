namespace Quasar.Models;

public sealed class ClusterDefinition
{
    public string UniqueName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string GatewayUrl { get; set; } = string.Empty;
    public string GatewayAdminTokenEnvironmentVariable { get; set; } = string.Empty;
    public string ConfigProfileId { get; set; } = string.Empty;
    public string WorldTemplateId { get; set; } = string.Empty;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public ClusterDefinition Clone() => new()
    {
        UniqueName = UniqueName,
        DisplayName = DisplayName,
        GatewayUrl = GatewayUrl,
        GatewayAdminTokenEnvironmentVariable = GatewayAdminTokenEnvironmentVariable,
        ConfigProfileId = ConfigProfileId,
        WorldTemplateId = WorldTemplateId,
        UpdatedAtUtc = UpdatedAtUtc,
    };
}
