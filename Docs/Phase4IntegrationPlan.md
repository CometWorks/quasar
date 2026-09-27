# Phase 4 Quasar integration — release continuation

Updated 2026-09-26. This is the current seven-stage plan and implementation record.
It supersedes the older six-step branch plan. Implementation checks and live acceptance
are recorded separately; a built package does not establish live acceptance.

Cluster 1.1.7 and Magnetar 2.4.2.3 are published. Quasar now consumes the renamed
`registry/ClusterRegistry` package layout and preserves the unchanged admin contract.
Generated one-Host deployments receive a local shared plugin directory; multi-Host
storage still needs a separately provisioned shared mount. The historical baselines
and test observations below remain records of their original runs.

The 2026-09-20 local run reached managed Serving, exercised save, Gateway restart,
clean Off/warm On and a central Quasar outage. UI controls and live refresh were
corrected from browser feedback. Acceptance remains open pending verification with
cluster 1.1.1 (authenticated save replication), plus the remaining conversion,
update/restore and client/multi-host scenarios. See the
[verification record](ClusterIntegrationVerification.md#local-managed-run--2026-09-20)
for evidence and limits.

## Guided setup follow-up

The seven stages below supplied deployment primitives; they did not supply turnkey
machine enrollment or fresh cluster creation. The branch now includes local,
one-time-command and SSH Host enrollment, protected outbound control tunnels,
generated credentials, resumable world/profile preparation and verified stopped
activation. [Guided setup](ClusterGuidedSetupPlan.md) records the workflow and its
separate acceptance gate. It depends on [Magnetar #58](https://github.com/CometWorks/magnetar/pull/58)
and a cluster release with the matching SDK binary pin. Existing live evidence does
not establish acceptance of this new workflow.

## Authority and ownership

Baseline: `clustering-plan` at `6621d1b7324a6b7da4285f87b354ef974fcb0a9f`,
cluster release v1.0.3 at `1cd3a4265749fd932b9ba25ac155166160709a08` (verified
SHA256SUMS), and Quasar branch `phase4/quasar-integration` starting at `65d4511`.
The archived cluster-gateway and cluster-runtime repositories are historical only.

Upstream implementation is reviewed in [cluster PR #12](https://github.com/CometWorks/cluster/pull/12)
and [Magnetar PR #57](https://github.com/CometWorks/magnetar/pull/57). These changes
are now published in cluster 1.1.0 and Magnetar 2.4.2.1; they are not
capabilities of cluster v1.0.3 or Magnetar v2.4.2.0. The vendored admin
contract has its own [source pin](../Contracts/ClusterGateway.AdminContract/SOURCE.md).

Registry owns placement, admission, drain, rotation, WA assignment and shared plugin
data. Host executes its plan with fenced process identities. Quasar owns desired
state, operator workflows and operation tracking. Agent supplies optional observations.
Gateway starts first and stops last. Running clusters do not depend on Quasar/Agent.
No automatic `fresh` or wipe is permitted. An accepted request is not convergence.

Consume the entire verified release, including Local node/WA plugins, Gateway,
world tool, CLI and docs. Do not rebuild those plugins at deployment or fetch them
from MagnetarHub. External DS, Magnetar, Direct Transport, compatibility/common
plugins and native assets are frozen separately and bound into the same revision.
All regular/WA/spare/replacement nodes receive identical common plugins/config;
only centrally prescribed infrastructure differs by role. No legacy variable aliases.
`CLUSTER_NODE_EPOCH` identifies a catalog slot, not a Registry incarnation.

## 1. Release compatibility and contracts

Implemented: canonical CLUSTER variables; release provenance and vendored DTOs;
query/manage authorization; durable admin operations; runtime-published physical
incarnation; public host-scoped executor leases and sequenced reports. Contract 0.4.0
also exposes deployment revision/readiness and handover configuration capabilities.

Gate: compare declared capabilities and exact artifacts, not version ordering.
Unsupported old packages cannot enter managed deployment.

## 2. Verified provisioning

Implemented: authenticated release staging, checksum/path/inventory validation,
immutable package selection, revision CAS, isolated DS/Content/Magnetar snapshots,
compiled Direct Transport bundles, common/compatibility plugins and resolved native
assets. Offline verification detects changed or missing inputs. Existing managed
runtime acquisition supplies DS/Magnetar; cluster staging never updates an active
shared installation. Common plugin compilation/cache export is an explicit input step.

Host preparation verifies the complete input inventory before promotion. Portable
installation archives support remote Hosts and regenerate local launch paths. The
package's nonlaunching generator produces execution bundles and canonical config.
Capability `pluginServices: 1` requires the exact build PluginSdk SHA-256. Immutable
candidate inputs remain separate from mutable runtime trees and the active revision.

## 3. Durable packaged lifecycle

Implemented: create/import UI/API/CLI; all-Host preparation and activation; explicit
missing-Gateway recovery; per-cluster lifecycle serialization; continuous NodePlan
execution; process adoption and lost-response replay; exact-incarnation kills;
Registry acknowledgements; authoritative clean-Down proof before Gateway teardown.

Shutdown proof binds lifecycle generation/endpoints/spec. Same-generation replay
preserves draining, backoff, pending kills and shutdown. Candidate staging does not
invalidate proof. On waits for an outstanding shutdown to settle. Partial activation
blocks start until the original request completes. Host stop carries the observed
PID and launch time, never just a slot name.

Runtime heartbeats prove canonical plugin/config readiness. Executor reports cannot
grant it. Registry restart clears transient readiness without undoing lifecycle;
surviving nodes re-establish proof. Stopped-slot configuration refresh preserves
plugin-owned files, empty directories and modes. Warm starts never overwrite worlds.

Live gate: create → Serving → clean Down → warm start, process adoption without
churn, replacement/spare admission and response-loss recovery.

## 4. Configuration, telemetry and administration

Implemented: one durable preparation specification for all Hosts; ordinary SDK
schema/editor reused for common config values; a config edit prepares a new candidate
and full-downtime activation applies it. SDK canonical storage is installed before
plugin construction and live read-back detects drift. Per-node config writes are
rejected in Quasar and Agent. Failed/unknown/mismatched provenance remains visible.

Registry observations and optional Agent receipts are correlated to process,
slot/node/incarnation and revision. Diagnostics, scoped maintenance, handover config,
world exports and artifact operations have management UI/API/CLI paths. Exact
capability/CLI parity includes the upstream handover routes.

Live gate: unchanged ordinary plugin fixture, uniform effective config, drift,
stale Agent receipt rejection and continued operation without Agent/Quasar.

## 5. Conversion, backups and restore

Implemented: guided standalone-to-cluster and cluster-to-standalone pages/buttons,
with matching API/CLI routes, immutable conversion requests, progress and resume.
Both create stopped destinations, retain backups and preserve source data. Forward
conversion reviews historical SDK settings and frozen plugin commits, transfers the
complete verified installation/world to every Host, then prepares/activates the fleet.
The release seeds regular slots 1 and 2; the wizard requires two regular nodes and
assigns World Authority separately. Reverse conversion assembles verified copies from
all stopped Host snapshots without shared storage and atomically publishes a separate
standalone server. Canonical plugin settings are exported for review/reapplication;
private/shared plugin stores remain in backup. Unsupported access restrictions and
non-SDK configuration providers block forward conversion. See
[Cluster Conversion](ClusterConversion.md) for exact scope and operator steps.

Shipped MagnetarWorld conversion preserves source; stopped multi-Host
native snapshots retain world, Registry, plugin records/private files and provenance.
Quiescent requires matching clean-Down proof; CrashConsistent requires explicit opt-in.
Snapshot IDs bind a capture lifecycle, hashes, directory/file modes and storage formats.
Host snapshot copies are released only after verified local archive commit.

Gateway vanilla exports have bounded retention and are retrieved through the Gateway
Host, with exact manifest/file hashes, inventory and byte counts checked before
release. Single-Host data roots are generated automatically. Multi-Host exports need
explicit Gateway-readable shared `exportDataRoots`; stopped native snapshots do not.

Automatic native backups use existing server schedule/retention only while already
cleanly stopped; scheduling does not introduce downtime. Manual backups/exports are
retained. Online export scheduling is not part of automatic native backup policy.

Restore requires a stopped fleet and newly prepared credentials. It retains the
previous runtime, journals each Host restore, fences old operations and blocks start
until activation completes. Registry restore clears old execution/ownership state,
bump generations and changes plugin StoreId. Storage maps (`world`, `registry`,
`plugins`) must match; changed formats require an explicit migration.

Live gate: conversion parity, verified export retrieval, native restore/recovery,
credential and stale-writer fencing, truthful consistency labels.

## 6. PluginSdk shared state — mandatory first post-beta release

Implemented upstream: `PluginCluster.ForPlugin` with loader-bound namespace;
authoritative durable reads/CAS with store UUID, revisions, schemas, tombstones and
bounded replay; node/incarnation context; WA/partition/physical targets; ownership
fences; bounded request/reply and game-thread handler admission. Standalone has one
local durable owner. Cluster provider loss fails unavailable, never local fallback.

Registry WAL and authenticated runtime links own the data path. Quasar/Agent are
observers only. The SDK exposes results for conflicts, fencing, capacity, unavailable
and timeouts; no automatic message retry or exactly-once external effects. Queue
admission checks lease freshness; authoritative effects still require fenced writes.
See [Cluster Plugins](ClusterPlugins.md#planned-sdk-extension-shared-state-and-node-aware-data)
and upstream SDK documentation/example for exact semantics and bounds.

Packaging binds the actual SDK hash; Gateway's verbatim protocol mirror has a hash
check. Diagnostics use existing PluginStats. Lifecycle requests fail closed when the
provider is absent, persist regular-node save-first stop/restart outcomes, and reject
unsupported WA/no-save requests rather than silently terminating a local node.

Live gate: provider registration from exact package, concurrent conflicts, durable
recovery, stale ownership, bounded dispatch, schema/restore, standalone behavior and
Quasar/Agent outage. Current ordinary plugins need no shared-state adaptation.
This stage's delivery and acceptance remain mandatory before first post-beta release.

## 7. Full-downtime updates and acceptance

Implemented: durable Stopping → Activating → Starting → Complete workflow. Online
preflight validates every Host before requesting downtime. Clean proof and stopped
Host previews precede activation. Errors retain the checkpoint for replay. Completion
requires matching Gateway deployment revision, managed runtime readiness and Host
attachment hashes. UI follows catalog progress; CLI `update --wait` checks the exact
submitted workflow. Pending workflows block competing lifecycle/config actions.

Previous installation is retained. Explicit rollback reuses the same workflow and
storage-format gate, preserving current world and plugin data. Format mismatch is an
error, never a silent data downgrade. No mixed cluster-package rollout is supported.

Final live acceptance: P4-QSR-01…06 (parity, convergence, Quasar non-dependency,
updates, monitoring and conversion), stage 6 packaged SDK cases, and unchanged
upstream phase 1–3 regressions. Full local verification follows implementation;
Quasar web service must not be launched without an explicit smoketest request.

## Verification record and release gate

Exact source pins, test counts and local artifact hashes are in the
[verification report](ClusterIntegrationVerification.md).

Focused checks have passed for Gateway persistence/executor/admin/plugin services,
Host inert-process preparation/activation/snapshot/restore, Python managed generation,
and Quasar cluster services/API/CLI. SDK checks cover CAS/replay/tombstone/restore,
standalone durability/concurrency, missing-provider lifecycle and queued-handler fencing.
The example plugin compiles. A fresh complete cluster review package builds and its
shipped self-tests pass. These are implementation checks, not live cluster acceptance.

Upstream cluster/Magnetar merge/releases are complete. Remaining release gate:
exact packaged runtime acceptance and full local P4 scenarios. Quasar changes remain on this branch; upstream
code and generic-plan changes are submitted for review separately.

### PR review follow-up — 2026-09-20

Stages 3–7 now include explicit stopped-fleet recovery for dirty Down/persisted Serving,
without weakening clean activation/update gates. Quasar verifies all nodes stopped,
stops the exact Gateway incarnation and rechecks every Host before recording a recovery
generation. Saves/plugin records survive; stale authority is fenced; replay preserves
later shutdown progress. Normal process restarts retain the original generation.

Executor lease-only heartbeats retain persistence batching. Managed legacy plan/heartbeat
mutations are refused. CLI Gateway calls carry configured bearer authentication, including
loopback. Release build, CLI startup and Host preparation enforce actual SDK version/types;
Magnetar 2.4.2.1 and its managed configuration/plugin services remain a release prerequisite.
Persistence failure deliberately remains fail-closed with 503 and paused relay; supervisor
control-health checks and storage repair/restart are required. Live acceptance remains open.

The subsequent runtime review fixes WA readiness after checkpoint restore, separates
new-client admission from existing valid relay, and returns explicit authenticated
plugin-routing errors. SDK storage is lazy outside managed mode; loaded private/static
and multiple configuration types are supported through canonical schema 2 and Agent
snapshot discovery. Numeric normalization avoids false drift. Genuine failures hold the
slot Draining for correction, retaining uniform configuration instead of a restart loop.

Plugin CAS uses bounded fsynced journal mutations and per-plugin capacity/rate limits.
The Registry capability is now `cluster-registry-v2`; readers retain v1 support, but
managed format-map changes and downgrade require explicit migration. No managed
v1-to-v2 migrator is provided. Preserve stopped backups; do not bypass metadata gates.
Unmanaged adoption uses stopped world export/conversion into a separate managed
Registry, retaining old plugin data for explicit migration. These changes still require
packaged live acceptance, especially WA startup, drift handling and storage latency.
