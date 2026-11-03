# Cluster Registry contract source pin

The base admin contract 0.4.0 originated byte-for-byte from published cluster v1.1.7,
commit `98f1e7c5a714c768fef2a428b0f108f09e727260`.
It includes the host-scoped executor contract, deployment revision/readiness and
handover capabilities. The upstream project renamed the source directory; the
contract contents and namespace did not change.

Source: https://github.com/CometWorks/cluster/tree/v1.1.7/ClusterRegistry.AdminContract

Build as a project reference; no private feed or neighboring checkout is required.
Refresh both DTO sources and wire fixtures together. Release and DTO package versions
are independent.

Original base AdminContract.cs SHA-256: `3a7771678de09f3956418df2de605eb4430d8c7fca84352168cd8c7f0a0e15b3`

ExecutorContract.cs SHA-256: `935b12c565216281a27a1ab0dc0a97aac7d7cd570b30ff9a4b3684ebe313bea4`

## Client diagnostics overlay

The additive client-diagnostics contract comes from `CometWorks/cluster` commit
`ade3f7e907a8e2a518246669b66df742203fd530` (admin contract 0.10.0). Existing core DTO shapes remain on the base
pin above; only the rotation/client-diagnostics operation names are added to the
base operation list. The two new sources are copied byte-for-byte:

- `RotationContract.cs` from `ClusterRegistry.AdminContract/RotationContract.cs`: SHA-256 `846ff9b5bd50aa2347dc57e37130eaeaea16b9bb979fc63154a5e266731dca06`.
- `DiagnosticsModels.cs` from `Shared/Cluster/Diagnostics/Protocol/DiagnosticsModels.cs`: SHA-256 `bb7464f4f8b776b5504fb02b73b36af9bbeb2456739d1bf67ea99938a5a6d967`.

Source: https://github.com/CometWorks/cluster/tree/ade3f7e907a8e2a518246669b66df742203fd530

AdminContract.cs with the additive operation names: SHA-256
`39c1c64ef291569ad6f51d92f5cf0feff85b0d16ba92c16a2f20a776836b95d2`.

## Player moderation overlay

The additive player-moderation overlay comes from `CometWorks/cluster` commit
`eda4a1cd56967cf753b86a9d5eec2013f1db726d` (admin contract 0.11.0), implemented for
[ticket 1855](https://tasks.cometworks.se/tasks/1855). The companion Cluster change
provides the Gateway and World Authority support; older Gateways keep these
actions disabled through operation capabilities.

Source: https://github.com/CometWorks/cluster/tree/eda4a1cd56967cf753b86a9d5eec2013f1db726d

`PlayerModerationContract.cs` is copied byte-for-byte from
`ClusterRegistry.AdminContract/PlayerModerationContract.cs` (package 0.11.0).
It adds a revision-checked role request and the observed promotion/cooldown state.
The three player operation names are added to the existing local operation list.
Existing base and diagnostics DTO shapes remain unchanged. HTTP wire v1 is retained.

- `PlayerModerationContract.cs` SHA-256: `73d996e804d165324f89d8141b82544951ccda9065efa0d6fa7ae907b7bacff1`.
- `AdminContract.cs` with the player operation names SHA-256: `f212df588d47a2650b6229aad1b6c2117ae34bdcb0a4c21183597de6987840aa`.
