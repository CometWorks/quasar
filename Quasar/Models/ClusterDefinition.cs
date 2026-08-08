using System.Text.Json.Serialization;
using HostContract = Quasar.Host.Contract.V1;

namespace Quasar.Models;

public sealed class ClusterDefinition
{
    public string UniqueName { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public string GatewayUrl { get; set; } = string.Empty;
    public string GatewayAdminTokenEnvironmentVariable { get; set; } = string.Empty;
    public string HostCommandUrl { get; set; } = string.Empty;
    public string HostCommandTokenEnvironmentVariable { get; set; } = string.Empty;
    public string ConfigProfileId { get; set; } = string.Empty;
    public string WorldTemplateId { get; set; } = string.Empty;
    [JsonConverter(typeof(JsonStringEnumConverter<DedicatedServerGoalState>))]
    public DedicatedServerGoalState GoalState { get; set; } = DedicatedServerGoalState.Off;
    public HostContract.GatewaySpec? Gateway { get; set; }
    public int ShutdownGracePeriodSeconds { get; set; } = 60;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public ClusterDefinition Clone() => new()
    {
        UniqueName = UniqueName,
        DisplayName = DisplayName,
        GatewayUrl = GatewayUrl,
        GatewayAdminTokenEnvironmentVariable = GatewayAdminTokenEnvironmentVariable,
        HostCommandUrl = HostCommandUrl,
        HostCommandTokenEnvironmentVariable = HostCommandTokenEnvironmentVariable,
        ConfigProfileId = ConfigProfileId,
        WorldTemplateId = WorldTemplateId,
        GoalState = GoalState,
        Gateway = Gateway is null ? null : Gateway with { Ports = [.. Gateway.Ports] },
        ShutdownGracePeriodSeconds = ShutdownGracePeriodSeconds,
        UpdatedAtUtc = UpdatedAtUtc,
    };
}
