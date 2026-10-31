# Cluster Registry contract source pin

Admin contract 0.10.0 is mirrored byte-for-byte from the public CometWorks/cluster
repository at commit `288ba5f9b2be1d8da060f85e3554c383032b793c`. This is the offline
administration draft, not a published cluster release. Wire protocol remains v1.
It includes offline catalog, profile, prediction and cache contracts together with
client diagnostics, rotation and the existing executor contracts.

Source: https://github.com/CometWorks/cluster/tree/288ba5f9b2be1d8da060f85e3554c383032b793c

Build as a project reference; no private feed or neighboring checkout is required.
Refresh all DTO sources and wire fixtures together. Release and DTO package versions
are independent; this source pin does not publish a package or change Quasar’s version.

- `AdminContract.cs` from `ClusterRegistry.AdminContract/AdminContract.cs`: SHA-256 `083c6f517eb0131fb08d7726155d0c2a8b7f124d9ad138e30ef320cb86c982c1`.
- `ExecutorContract.cs` from `ClusterRegistry.AdminContract/ExecutorContract.cs`: SHA-256 `935b12c565216281a27a1ab0dc0a97aac7d7cd570b30ff9a4b3684ebe313bea4`.
- `OfflineContract.cs` from `ClusterRegistry.AdminContract/OfflineContract.cs`: SHA-256 `55ae39e90872b29285b56de25496701fc6ff7e4dd41e75f042475b834e31b21c`.
- `RotationContract.cs` from `ClusterRegistry.AdminContract/RotationContract.cs`: SHA-256 `846ff9b5bd50aa2347dc57e37130eaeaea16b9bb979fc63154a5e266731dca06`.
- `DiagnosticsModels.cs` from `Shared/Cluster/Diagnostics/Protocol/DiagnosticsModels.cs`: SHA-256 `bb7464f4f8b776b5504fb02b73b36af9bbeb2456739d1bf67ea99938a5a6d967`.
