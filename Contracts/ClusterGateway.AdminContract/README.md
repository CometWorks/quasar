# Cluster Registry admin contract

Implementation-free DTOs for the versioned Cluster Registry admin API. Quasar,
Quasar.Host and the packaged CLI consume this contract without referencing Registry
implementation types. AdminProtocol exposes the URL prefix and wire version.

Contract 0.10.0 includes capability-gated offline partition, profile, prediction and
cache administration, client diagnostics and rotation, recovery readiness,
executor contracts, deployment readiness and handover capabilities. It retains
wire protocol v1. All five DTO files are vendored byte-for-byte from the same
immutable public Cluster draft; see [SOURCE.md](SOURCE.md) for provenance and hashes.
This is a source pin, not a published package or cluster release.

Package `0.4.0` includes recovery readiness, host-scoped executor contracts,
deployment revision/readiness and handover capabilities. It retains wire protocol v1.

Quasar vendors this source contract. The base `AdminContract.cs` originated
byte-for-byte from `CometWorks/cluster` v1.1.7, commit
`98f1e7c5a714c768fef2a428b0f108f09e727260`. See [SOURCE.md](SOURCE.md) for the
source pin and checksums. The upstream source directory is
`ClusterRegistry.AdminContract`; the local project, package ID and namespaces keep
`ClusterGateway.AdminContract` for compatibility. The former cluster-gateway
repository is archived. Release package versions and DTO package versions are independent.

Client diagnostics adds a small source overlay: `RotationContract.cs` and
`DiagnosticsModels.cs` from the cluster client-diagnostics PR, plus its three operation
names. The existing 0.4.0 core DTO shapes remain unchanged. No neighboring checkout or
private NuGet feed is needed. See SOURCE.md for pins and hashes.

The player-moderation overlay adds `PlayerModerationContract.cs` and three operation
names from cluster admin contract `0.11.0`. Roles use a per-player expected revision;
observed state includes disconnected players' Gateway admission cooldowns. The base
contract version and existing DTO shapes stay pinned as above.
