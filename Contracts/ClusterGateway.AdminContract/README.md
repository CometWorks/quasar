# Cluster Gateway admin contract

Implementation-free DTOs for the versioned Cluster Gateway admin API. Gateway,
Quasar, `Quasar.Host`, and the packaged CLI consume this package instead of
referencing Registry implementation types.

The URL prefix and current wire version are exposed by `AdminProtocol`.

Package `0.2.0` adds the recovery-readiness DTOs. This is an additive extension
of wire protocol v1: existing health, status, and plan consumers remain valid.
