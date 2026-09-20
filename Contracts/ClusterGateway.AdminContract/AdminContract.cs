namespace CometWorks.ClusterGateway.AdminContract.V1;

public static class AdminProtocol
{
    public const int Version = 1;
    public const string RoutePrefix = "/admin/v1";

    /// <summary>
    /// Every operation of the admin contract, exactly one spelling per HTTP operation.
    /// The Gateway serves this list from /capabilities and the CLI's self-test asserts a
    /// 1:1 mapping between its command registry and this list (the P4-QSR-01 parity
    /// sweep's static half), so a route cannot be added without a CLI spelling or vice
    /// versa.
    /// </summary>
    public static readonly string[] Operations =
    [
        "GET /admin/v1/health",
        "GET /admin/v1/capabilities",
        "GET /admin/v1/status",
        "GET /admin/v1/plan",
        "GET /admin/v1/recovery-readiness",
        "GET /admin/v1/nodes",
        "GET /admin/v1/world-authority",
        "GET /admin/v1/partitions",
        "GET /admin/v1/snapshots",
        "GET /admin/v1/events",
        "GET /admin/v1/operations",
        "GET /admin/v1/operations/{id}",
        "GET /admin/v1/config",
        "PUT /admin/v1/config",
        "GET /admin/v1/handover-config",
        "PUT /admin/v1/handover-config",
        "POST /admin/v1/shutdown",
        "POST /admin/v1/gateway/restart",
        "POST /admin/v1/save-all",
        "POST /admin/v1/nodes/{slot}/close",
        "POST /admin/v1/nodes/{slot}/kill",
        "POST /admin/v1/world-authority/move",
        "GET /admin/v1/diag",
        "POST /admin/v1/triggers/{task}",
        "GET /admin/v1/clients",
        "POST /admin/v1/clients/{id}/kick",
        "GET /admin/v1/admission/bans",
        "POST /admin/v1/admission/bans",
        "DELETE /admin/v1/admission/bans/{userId}",
        "POST /admin/v1/chat",
        "GET /admin/v1/chat/history",
        "POST /admin/v1/world-exports",
        "GET /admin/v1/artifacts",
        "GET /admin/v1/artifacts/{id}",
        "DELETE /admin/v1/artifacts/{id}",
    ];
}

public sealed record AdminEnvelope<T>(int ProtocolVersion, DateTimeOffset CapturedAt, T Data);

public sealed record AdminErrorEnvelope(int ProtocolVersion, DateTimeOffset CapturedAt, AdminError Error);

public sealed record AdminError(string Code, string Message,
    IReadOnlyDictionary<string, string[]>? Validation = null);

public sealed record GatewayHealth(string Service, bool RegistryAvailable, string TransportProtocol);

/// <summary>What the presented credential may do. Query reads state; Manage also mutates it.</summary>
public enum AdminScope { Query, Manage, Executor }

/// <summary>Feature discovery: the operations this Gateway build serves and the caller's own scope.</summary>
public sealed record AdminCapabilities(
    string Service,
    AdminScope Scope,
    string[] Operations,
    IReadOnlyDictionary<string, string> Features);

/// <summary>
/// One entry of the ordered cluster event/audit tail. <c>Seq</c> is monotonic per Gateway
/// registry; readers resume with <c>?cursor=lastSeenSeq</c>. Retention is bounded, so a
/// gap between the requested cursor and the first returned event means truncation.
/// </summary>
public sealed record AdminEvent(
    long Seq,
    DateTimeOffset At,
    string Type,
    string? Actor,
    string? RequestId,
    string? Target,
    IReadOnlyDictionary<string, string>? Data);

public sealed record AdminEventPage(AdminEvent[] Events, long NextCursor, long EarliestRetained);

public enum AdminOperationState { Running, Succeeded, Failed }

/// <summary>
/// One durable admin mutation. Every mutating route requires an <c>Idempotency-Key</c>
/// header; retrying with the same key replays the recorded operation instead of repeating
/// its effect, and the record survives a Gateway restart. Reusing a key for a different
/// request is refused with <c>idempotency_key_reuse</c>.
/// </summary>
public sealed record AdminOperation(
    string OperationId,
    string Kind,
    AdminOperationState State,
    string Actor,
    string IdempotencyKey,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? Target,
    IReadOnlyDictionary<string, string>? Result,
    AdminError? Error);

public sealed record AdminOperationPage(AdminOperation[] Operations);

/// <summary>Graceful drain to Down, or emergency teardown. The Gateway API process stays up
/// in Down; final OS process teardown belongs to the packaged cluster CLI / host executor.</summary>
public sealed record ShutdownRequest(bool Emergency = false, double GraceSeconds = 60, double ForceAfterSeconds = 300);

public sealed record SaveAllRequest(bool Sweep = false);

public sealed record CloseNodeRequest(double? ForceAfterSeconds = null);

/// <summary>Force removal is provenance-bound: the exact incarnation must be named, never a bare slot.</summary>
public sealed record KillNodeRequest(string Node, long Epoch);

public sealed record WorldAuthorityMoveRequest(double? ForceAfterSeconds = null);

/// <summary>One desired node slot: the registry-owned policy the executor actualizes.</summary>
public sealed record AdminSlotSpec(string SlotKey, string Host, NodeRole Role, NodeGoal Goal);

/// <summary>
/// Effective cluster configuration: immutable deployment identity (hashes fence node
/// admission) plus the mutable, revisioned policy subset the registry owns today.
/// </summary>
public sealed record AdminConfig(
    string ClusterId,
    string WorldId,
    string ClusterConfigHash,
    string BinaryVersion,
    string ReplicationTypeTableHash,
    string WorldSettingsHash,
    double SyncDistance,
    double LeaseTtlSeconds,
    long PolicyRevision,
    AdminSlotSpec[] Slots);

/// <summary>Compare-and-set policy update: upserts the given slots; refused with
/// <c>revision_mismatch</c> when <c>ExpectedRevision</c> is stale.</summary>
public sealed record AdminConfigUpdate(long ExpectedRevision, AdminSlotSpec[] Slots);

/// <summary>
/// One connected player session as the operator sees it: identity, admission state, and
/// the authoritative route (node/partition) when the Registry knows it. The cluster is
/// one server - node placement is informational, not a management handle.
/// </summary>
public sealed record ClientSummary(
    ulong ClientId,
    string? PlayerName,
    long? IdentityId,
    string AdmissionState,
    bool? IsAdmin,
    string? Node,
    ulong? Partition,
    DateTimeOffset ConnectedAt,
    DateTimeOffset LastClientActivity);

/// <summary>Kick with cooldown, or a punitive-free forced reconnect when <c>Reconnect</c>.</summary>
public sealed record KickClientRequest(bool Reconnect = false);

public sealed record BanRequest(ulong UserId);

public sealed record AdmissionBans(ulong[] Banned);

/// <summary>Cluster-global chat send. <c>Sender</c> must be a real identity (an admin's
/// Steam id) - authorship is part of the admission model, not a free string.</summary>
public sealed record ChatSendRequest(string Message, ulong Sender);

public sealed record ChatMessage(long Seq, DateTimeOffset At, ulong Author, byte Channel, string Text);

/// <summary>
/// Accepted global chat, oldest first, captured at the World Authority (the single accept
/// point). The sequence restarts with each WA incarnation - <c>WorldAuthorityEpoch</c>
/// tells a resuming reader when its cursor became stale.
/// </summary>
public sealed record ChatHistory(ChatMessage[] Messages, long NextCursor, long WorldAuthorityEpoch);

/// <summary>Honest export labels: Quiescent only from a clean shutdown with nothing dirty;
/// an online cut is CutConsistent only when the cluster can prove it, else CrashConsistent.</summary>
public enum ExportConsistency { Quiescent, CutConsistent, CrashConsistent }

public enum ArtifactRetrievalKind { SharedPath, HttpDownload, StorageUri }

/// <summary>How to fetch an artifact. v1 implements SharedPath (a scope-validated absolute
/// path under the configured export root); the discriminator already carries the future
/// HttpDownload/StorageUri kinds so adding them is not a breaking change.</summary>
public sealed record ArtifactRetrieval(ArtifactRetrievalKind Kind, string? LocalPath = null, string? Uri = null);

public sealed record ArtifactDescriptor(
    string ArtifactId,
    string Format,
    string? SnapshotId,
    ExportConsistency Consistency,
    DateTimeOffset CreatedAt,
    DateTimeOffset ExpiresAt,
    long SizeBytes,
    string ManifestSha256,
    IReadOnlyDictionary<string, string> FileChecksums,
    ArtifactRetrieval Retrieval,
    string[] Warnings);

public sealed record WorldExportRequest(double? TtlHours = null);

public sealed record ArtifactPage(ArtifactDescriptor[] Artifacts);

public sealed record ClusterStatus(
    string ClusterId,
    string WorldId,
    ClusterPhase Phase,
    StartupKind Startup,
    DateTimeOffset? ShutdownStarted,
    DateTimeOffset? LastCleanShutdown,
    bool ExecutorSilent,
    bool GlobalSpawnHalted,
    string[] DegradedReasons,
    ClusterCounts Counts,
    WorldAuthorityStatus WorldAuthority,
    NodeStatus[] Nodes,
    ExecutorStatus[] Executors,
    bool AcceptingPlayers,
    AdminHealth Health,
    string[] ReasonCodes,
    DateTimeOffset ObservedAt, string? DeploymentRevision = null, bool ManagedDeploymentReady = false);

/// <summary>
/// Server-computed health severity with stable machine-readable <c>ReasonCodes</c>, so a
/// control plane never decodes internal states or error strings to decide severity.
/// </summary>
public enum AdminHealth { Healthy, Warning, Unhealthy }

public sealed record ClusterCounts(
    int ConnectedClients,
    int Nodes,
    int Partitions,
    int Saves,
    int Handovers,
    int IncompleteHandovers,
    int Snapshots,
    int VoxelBases,
    int VoxelJournals,
    int PendingDeletes,
    long WalRecords,
    long WalBytes);

public sealed record WorldAuthorityStatus(string? Node, long Epoch, long Generation, DateTimeOffset LeaseExpires);

public sealed record NodeStatus(
    string Node,
    string? SlotKey,
    long Epoch,
    NodeRole Role,
    NodeState State,
    string Endpoint,
    string? ControlEndpoint,
    DateTimeOffset Registered,
    DateTimeOffset LeaseExpires,
    long SimulationFrame,
    int QuarantinedPartitions,
    string? Host,
    bool LeaseFresh);

public sealed record PartitionSummary(
    ulong Partition,
    string? Node,
    long Epoch,
    long Generation,
    DateTimeOffset LeaseExpires,
    bool Recovering,
    bool Quarantined,
    bool Retired);

public enum AdminSnapshotKind { CleanShutdown, Online, Conversion }

public sealed record SnapshotSummary(
    string SnapshotId,
    AdminSnapshotKind Kind,
    long ClusterTime,
    int Partitions,
    bool HasGlobal,
    int VoxelMaps,
    string[] Dirty);

public sealed record ExecutorStatus(string Executor, DateTimeOffset LastHeartbeat);

public sealed record NodePlan(
    string SlotKey,
    string Host,
    NodeRole Role,
    NodeGoal Goal,
    NodeObservation Observed,
    string? ObservedNode,
    IncumbentAction IncumbentAction,
    string? IncumbentNode,
    long IncumbentEpoch,
    DateTimeOffset? ObservedAt,
    string? LastAttemptKey,
    string? LastFailure,
    DateTimeOffset? RetryAt,
    int FailureCount,
    bool FailureAlert,
    bool SpawnAllowed);

public sealed record RecoveryReadiness(
    RecoveryReadinessState State,
    RecoveryPoint? LatestPoint,
    long? MaxPartitionSaveAgeSeconds,
    long? GlobalSaveAgeSeconds,
    long? MaxVoxelJournalAgeSeconds,
    ArtifactCoverage PartitionSaves,
    ArtifactCoverage GlobalState,
    ArtifactCoverage Voxels,
    RegistryDurability Registry,
    MissingArtifact[] Missing,
    string[] Risks);

public sealed record RecoveryPoint(
    string SnapshotId,
    RecoveryConsistency Consistency,
    long ClusterTime);

public sealed record ArtifactCoverage(
    int Required,
    int Covered,
    int MinimumCopies,
    int MinimumDistinctHosts);

public sealed record RegistryDurability(
    bool SnapshotPresent,
    DateTimeOffset? LastCheckpointAt,
    long WalRecords,
    long WalBytes);

public sealed record MissingArtifact(string Kind, string Id);

public enum ClusterPhase { Bootstrapping, Serving, Degraded, Draining, Down }
public enum StartupKind { Warm, Recovery }
public enum NodeRole { Regular, WorldAuthority }
public enum NodeState { Active, Closed, Dead, Empty }
public enum NodeGoal { Wanted, Draining, Kill }
public enum NodeObservation { Missing, Spawning, Ready, Failed, Gone }
public enum IncumbentAction { None, Draining, Kill }
public enum RecoveryReadinessState { Ready, AtRisk, NotReconstructible }
public enum RecoveryConsistency { Quiescent, CutConsistent }
