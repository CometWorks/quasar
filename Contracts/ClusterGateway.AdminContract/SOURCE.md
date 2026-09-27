# Gateway contract source pin

Admin contract 0.4.0 is mirrored byte-for-byte from published cluster v1.1.7,
commit `98f1e7c5a714c768fef2a428b0f108f09e727260`.
It includes the host-scoped executor contract, deployment revision/readiness and
handover capabilities. The upstream project renamed the source directory; the
contract contents and namespace did not change.

Source: https://github.com/CometWorks/cluster/tree/v1.1.7/ClusterRegistry.AdminContract

Build as a project reference; no private feed or neighboring checkout is required.
Refresh both DTO sources and wire fixtures together. Release and DTO package versions
are independent.

AdminContract.cs SHA-256: `3a7771678de09f3956418df2de605eb4430d8c7fca84352168cd8c7f0a0e15b3`

ExecutorContract.cs SHA-256: `935b12c565216281a27a1ab0dc0a97aac7d7cd570b30ff9a4b3684ebe313bea4`
