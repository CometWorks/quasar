# Cluster Registry admin contract

Implementation-free DTOs for the versioned Cluster Registry admin API. Cluster Registry,
Quasar, `Quasar.Host`, and the packaged CLI consume this package instead of
referencing Registry implementation types.

The URL prefix and current wire version are exposed by `AdminProtocol`.

Package `0.4.0` includes recovery readiness, host-scoped executor contracts,
deployment revision/readiness and handover capabilities. It retains wire protocol v1.

Quasar currently vendors this source contract. `AdminContract.cs` was verified
byte-for-byte against `CometWorks/cluster` v1.1.7, commit
`98f1e7c5a714c768fef2a428b0f108f09e727260`. See [SOURCE.md](SOURCE.md) for the
source pin and checksums. The upstream source directory is
`ClusterRegistry.AdminContract`; the local project, package ID and namespaces keep
`ClusterGateway.AdminContract` for compatibility. The former cluster-gateway
repository is archived. Release package versions and DTO package versions are independent.
