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
    public ClusterActivationProof? ActivationProof { get; set; }
    public ClusterActiveRevision? ActiveDeployment { get; set; }
    public string? PendingDeploymentHash { get; set; }
    public string? PendingRestoreHash { get; set; }
    public string? LastRestoreHash { get; set; }
    public ClusterActiveRevision? PreviousDeployment { get; set; }
    public ClusterUpdate? Update { get; set; }
    public Quasar.Services.ClusterPreparationRequest? Preparation { get; set; }
    public Quasar.Services.ClusterDeploymentRequest? PreparedDeployment { get; set; }
    public bool PreparedForSelectedRelease { get; set; }
    public ClusterPreparedProfile? PreparedProfile { get; set; }
    public ClusterContentUpdateQueue? QueuedContentUpdates { get; set; }

    [JsonIgnore]
    public bool HasPreparedUpdate => ActiveDeployment is { } active && PreparedDeployment is { } prepared
        && prepared.ExpectedRevision == active.Revision && prepared.Revision != active.Revision;

    internal string GetLifecycleId() => Convert.ToHexString(SHA256.HashData(
        JsonSerializer.SerializeToUtf8Bytes(new
        {
            UniqueName, GoalState, UpdatedAtUtc, GatewayUrl, HostCommandUrl, Gateway,
            ConfigProfileId, WorldTemplateId, ShutdownGracePeriodSeconds,
        }))).ToLowerInvariant();

    internal bool HasCleanShutdownProof() => ShutdownProof?.LifecycleId == GetLifecycleId();

    internal bool HasActivationProof() => ActivationProof?.LifecycleId == GetLifecycleId();

    // Nothing of this lifecycle runs: it shut down cleanly, or was activated and has not started since.
    // requireRun also refuses a first deployment that never ran, so there is no saved cluster state yet.
    internal bool IsProvenStopped(bool requireRun = false) => HasCleanShutdownProof()
        || HasActivationProof() && (!requireRun || ActivationProof!.CleanShutdownAt is not null);

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
        ActivationProof = ActivationProof,
        ActiveDeployment = ActiveDeployment,
        PendingDeploymentHash = PendingDeploymentHash,
        PendingRestoreHash = PendingRestoreHash,
        LastRestoreHash = LastRestoreHash,
        PreviousDeployment = PreviousDeployment,
        Update = Update,
        Preparation = Preparation,
        PreparedDeployment = PreparedDeployment,
        PreparedForSelectedRelease = PreparedForSelectedRelease,
        PreparedProfile = PreparedProfile,
        QueuedContentUpdates = QueuedContentUpdates is { } queued ? queued with { Items = [.. queued.Items] } : null,
    };
}

// Requested observations only; these are not downloaded or prepared content pins.
public sealed record ClusterContentUpdateQueue(string DeploymentRevision, DateTimeOffset QueuedAt,
    ClusterContentUpdateTarget[] Items);
public sealed record ClusterContentUpdateTarget(string Kind, string Id, string Name, string TargetVersion,
    DateTimeOffset ObservedAt);

public sealed record ClusterPackageSelection(long Revision, string Version, string Sha256,
    string Commit, string IdempotencyKey);
public sealed record ClusterPreparedProfile(string ProfileId, string DeploymentRevision, string? ProfileSha256 = null);

// Recorded before Gateway teardown, so a lost Host response does not erase the
// authoritative clean-Down observation. Valid only for the exact lifecycle identity.
public sealed record ClusterShutdownProof(string LifecycleId, DateTimeOffset CleanShutdownAt,
    HostContract.GatewayStopFence StopFence);

// Written with the activation that follows a clean Down, or with the first deployment. Every way of
// running the Gateway changes the lifecycle identity first, so while it matches, the activated Gateway
// has not run. CleanShutdownAt is the clean Down it replaced, null for a first deployment.
public sealed record ClusterActivationProof(string LifecycleId, DateTimeOffset? CleanShutdownAt);

public sealed record ClusterActiveRevision(string Revision, ClusterHostRevision[] Hosts, DateTimeOffset ActivatedAt,
    string? PackageVersion = null)
{
    public ClusterDeploymentProvenance? Provenance { get; init; }
}
public sealed record ClusterDeploymentProvenance(ClusterPackageSelection PackageSelection,
    string DependencyManifestSha256, Quasar.Services.ClusterPreparationRequest Preparation);
public sealed record ClusterHostRevision(string HostId, string CommandUrl, string TokenEnvironmentVariable,
    HostContract.HostActiveDeployment Deployment);

public enum ClusterUpdatePhase { Stopping, Activating, Starting, Complete, Reverting }
public sealed record ClusterUpdate(Guid Id, Quasar.Services.ClusterDeploymentRequest Deployment,
    ClusterActiveRevision Previous, bool Rollback, ClusterUpdatePhase Phase, DateTimeOffset StartedAt,
    DateTimeOffset UpdatedAt, string? LastError = null, bool BackupRequired = false, DateTimeOffset? PhaseStartedAt = null,
    DedicatedServerGoalState? GoalBefore = null, Guid? Backup = null, string? Failure = null, Guid? RevertRestoreId = null,
    Quasar.Services.ClusterRestoreRequest? Revert = null,
    // An update despite a recorded dirty shutdown: the Dirty list the operator accepted, and whether
    // its fenced stop of the Gateway has happened.
    string[]? DirtyShutdown = null, bool FencedStop = false);
