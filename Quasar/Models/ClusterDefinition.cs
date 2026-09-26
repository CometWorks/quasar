using System.Security.Cryptography;
using System.Text.Json;
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
    // Candidate for future provisioning, never an instruction to activate a running cluster.
    public ClusterPackageSelection? PackageSelection { get; set; }
    public string? DependencyManifestSha256 { get; set; }
    public int ShutdownGracePeriodSeconds { get; set; } = 60;
    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;
    public ClusterShutdownProof? ShutdownProof { get; set; }
    public ClusterActiveRevision? ActiveDeployment { get; set; }
    public string? PendingDeploymentHash { get; set; }
    public string? PendingRestoreHash { get; set; }
    public string? LastRestoreHash { get; set; }
    public ClusterActiveRevision? PreviousDeployment { get; set; }
    public ClusterUpdate? Update { get; set; }
    public Quasar.Services.ClusterPreparationRequest? Preparation { get; set; }
    public Quasar.Services.ClusterDeploymentRequest? PreparedDeployment { get; set; }
    public bool PreparedForSelectedRelease { get; set; }

    internal string GetLifecycleId() => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            UniqueName, GoalState, UpdatedAtUtc, GatewayUrl, HostCommandUrl, Gateway,
            ConfigProfileId, WorldTemplateId, ShutdownGracePeriodSeconds,
        }))).ToLowerInvariant();

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
        PackageSelection = PackageSelection,
        DependencyManifestSha256 = DependencyManifestSha256,
        ShutdownGracePeriodSeconds = ShutdownGracePeriodSeconds,
        UpdatedAtUtc = UpdatedAtUtc,
        ShutdownProof = ShutdownProof,
        ActiveDeployment = ActiveDeployment,
        PendingDeploymentHash = PendingDeploymentHash,
        PendingRestoreHash = PendingRestoreHash,
        LastRestoreHash = LastRestoreHash,
        PreviousDeployment = PreviousDeployment,
        Update = Update,
        Preparation = Preparation,
        PreparedDeployment = PreparedDeployment,
        PreparedForSelectedRelease = PreparedForSelectedRelease,
    };
}

public sealed record ClusterPackageSelection(long Revision, string Version, string Sha256,
    string Commit, string IdempotencyKey);

// Recorded before Gateway teardown, so a lost Host response does not erase the
// authoritative clean-Down observation. Valid only for the exact lifecycle identity.
public sealed record ClusterShutdownProof(string LifecycleId, DateTimeOffset CleanShutdownAt,
    HostContract.GatewayStopFence StopFence);

public sealed record ClusterActiveRevision(string Revision, ClusterHostRevision[] Hosts, DateTimeOffset ActivatedAt);
public sealed record ClusterHostRevision(string HostId, string CommandUrl, string TokenEnvironmentVariable,
    HostContract.HostActiveDeployment Deployment);

public enum ClusterUpdatePhase { Stopping, Activating, Starting, Complete }
public sealed record ClusterUpdate(Guid Id, Quasar.Services.ClusterDeploymentRequest Deployment,
    ClusterActiveRevision Previous, bool Rollback, ClusterUpdatePhase Phase, DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt, string? LastError = null);
