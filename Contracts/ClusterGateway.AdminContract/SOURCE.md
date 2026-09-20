# Gateway contract source pin

Admin contract 0.4.0 is mirrored from the upstream integration commit
`5d2541c` (release baseline v1.0.3: `1cd3a4265749fd932b9ba25ac155166160709a08`).
It adds the host-scoped executor contract, deployment revision/readiness and handover capabilities; published v1.0.3 does not implement it.

Source: https://github.com/CometWorks/cluster/tree/5d2541c/ClusterGateway.AdminContract

Build as a project reference; no private feed or neighboring checkout is required.
Refresh both DTO sources and wire fixtures together. Release and DTO package versions
are independent.

AdminContract.cs SHA-256: `3a7771678de09f3956418df2de605eb4430d8c7fca84352168cd8c7f0a0e15b3`

ExecutorContract.cs SHA-256: `935b12c565216281a27a1ab0dc0a97aac7d7cd570b30ff9a4b3684ebe313bea4`
