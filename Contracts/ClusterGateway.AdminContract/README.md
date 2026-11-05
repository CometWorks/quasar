# Cluster Registry admin contract

Implementation-free DTOs for the versioned Cluster Registry admin API. Quasar,
Quasar.Host, Bootstrap and the packaged CLI consume this project without
referencing Registry implementation types. AdminProtocol exposes the URL prefix,
wire version and capability operation names.

Contract 0.11.0 combines offline partition/profile/prediction/cache administration,
client diagnostics/rotation, revision-checked player moderation and existing
recovery, executor, deployment and handover contracts. HTTP wire v1 is retained.
All six DTO sources are copied byte-for-byte from one immutable public Cluster
feature revision; see [SOURCE.md](SOURCE.md) for paths and checksums. Runtime
features remain capability-gated for older compatible Gateways.

The upstream directory is ClusterRegistry.AdminContract. The local project,
package ID and namespace retain ClusterGateway.AdminContract for compatibility.
No neighboring checkout or private package feed is required. Source pinning does
not publish a Cluster package or change Quasar's application version.
