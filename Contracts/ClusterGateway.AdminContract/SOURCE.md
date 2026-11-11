# Cluster Registry contract source pin

The base admin contract 0.11.0 is mirrored from the public CometWorks/cluster
repository at commit `d33cd2fdc9a7c588e2d066a511fb1542f50aca7f`. HTTP wire protocol remains v1.
This feature revision combines offline compensation, client diagnostics, rotation
and player moderation with the current executor and deployment contracts.

Contract 0.12.0 stages a player-access extension on top of this pin:
`AdminContract.cs` adds `PUT /admin/v1/players/access` and
`PlayerAccessContract.cs` defines its expiring policy/status payloads. These two
files match the companion Cluster implementation prepared from the same base;
the extension has not yet acquired an immutable published upstream commit.
Keep it capability-gated until that package is published. Do not represent the
extended files as byte-for-byte copies from the base commit.

Source: https://github.com/CometWorks/cluster/tree/d33cd2fdc9a7c588e2d066a511fb1542f50aca7f

Build as a project reference. No private feed or neighboring checkout is required.
Refresh the DTO sources and wire fixtures together. Release and DTO package versions
are independent; this pin does not change the Quasar application version.

- Extended `AdminContract.cs` from `ClusterRegistry.AdminContract/AdminContract.cs`: SHA-256 `6d3748e9cb77d35c28ef38dd0dacc94d3fc60fb9e51daf7150f911cee7e3fe2b`.
- New `PlayerAccessContract.cs` from `ClusterRegistry.AdminContract/PlayerAccessContract.cs`: SHA-256 `6f90c1efe8d4cee07b65c1cdf3620dab34b8022f304087afa9625b905f3739a5`.
- `ExecutorContract.cs` from `ClusterRegistry.AdminContract/ExecutorContract.cs`: SHA-256 `935b12c565216281a27a1ab0dc0a97aac7d7cd570b30ff9a4b3684ebe313bea4`.
- `OfflineContract.cs` from `ClusterRegistry.AdminContract/OfflineContract.cs`: SHA-256 `55ae39e90872b29285b56de25496701fc6ff7e4dd41e75f042475b834e31b21c`.
- `RotationContract.cs` from `ClusterRegistry.AdminContract/RotationContract.cs`: SHA-256 `846ff9b5bd50aa2347dc57e37130eaeaea16b9bb979fc63154a5e266731dca06`.
- `DiagnosticsModels.cs` from `Shared/Cluster/Diagnostics/Protocol/DiagnosticsModels.cs`: SHA-256 `bb7464f4f8b776b5504fb02b73b36af9bbeb2456739d1bf67ea99938a5a6d967`.
- `PlayerModerationContract.cs` from `ClusterRegistry.AdminContract/PlayerModerationContract.cs`: SHA-256 `73d996e804d165324f89d8141b82544951ccda9065efa0d6fa7ae907b7bacff1`.
