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

The upstream source directory is ClusterRegistry.AdminContract. The local project,
package ID and namespaces keep ClusterGateway.AdminContract for compatibility.
No neighboring checkout or private feed is needed. Release and DTO package versions
are independent.
