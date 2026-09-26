# Cluster plugins: one server, existing SDK

Design requirements and source audit, 2026-09-19. This document distinguishes the
required behavior from the branch's current implementation. It does not claim the
release already supplies plugin synchronization or transparent global plugin state.

## Operator contract

Select plugins and edit their settings once for the cluster. Regular nodes, WA,
hot spares and replacements receive the exact same common plugin artifacts,
dependencies and effective configuration. The release's role-specific node/WA
infrastructure plugins are centrally managed exceptions, never per-node operator
choices. Instance paths, ports and process identity are launch inputs. Each process
retains its own writable state/cache/log directories.

One durable deployment revision binds package/runtime pins, the common plugin set,
role-specific infrastructure and plugin configuration. Configuration includes schema
identity, normalized effective values/defaults and revision/hash. Identical displayed
version strings do not prove identical code. Resolve/build once and deploy the same
hashed outputs, including server companions owned by Quasar UI plugins. Disable
independent hub updates and mutable-source builds on nodes.

### Infrastructure plugins from release artifacts

Consume the complete verified cluster release as built: node and WA plugins, their
bundled libraries and metadata, Gateway executable, world-conversion tool, CLI and
documentation. Load the packaged plugins through Magnetar's existing `Local` mechanism,
as cluster v1.0.3 already does in `Cli/magnetar_config.py`. Do not publish or resolve the
cluster node/WA plugins through MagnetarHub, rebuild them during deployment, or mix
artifacts from different cluster releases.

The launcher automatically selects the packaged `ClusterNode` plugin (`cluster-node`)
for regular nodes and `WorldAuthority` (`cluster-wa`) for WA. These remain required
infrastructure, not operator choices. Managed profiles contain the appropriate `Local`
entry; user configuration cannot suppress, replace, duplicate or add the opposite
role's plugin. Invalid role identity or missing required infrastructure prevents
cluster startup. Standalone servers receive neither cluster plugin.

One deployment revision binds the release archive hash and extracted artifact hashes
to compatible external dependencies and the common plugin/config set. The reviewed
v1.0.3 package still requires separately provisioned DS, Magnetar and Direct Transport;
using all release artifacts does not imply those host dependencies are bundled.
Compatibility and ordinary hub plugins keep their existing loading mechanisms, with
resolved metadata, compiled outputs and declared assets pinned for the deployment.

Every replacement must load the same verified bytes without independent hub refreshes,
missing-asset downloads or compilation from mutable sources. A long cache lifetime or
`-noupdate` alone does not prove completeness. The host readiness gate and Agent inventory
verify role infrastructure alongside the identical common plugin/config set.

**Status:** packaged local node/WA loading exists in cluster v1.0.3. Complete dependency
pinning, managed startup enforcement and Agent verification remain integration work.
The earlier implicit-hub proposal is superseded by consuming the release's built
artifacts. Existing Quasar package/dependency staging is preparation, not evidence that
managed lifecycle and offline replacements already work.

Schema-2 dependency snapshots now include common/compatibility bundles and resolved
native assets. The upstream CLI accepts `DIRECT_TRANSPORT_BINARIES` and
`CLUSTER_COMMON_PLUGINS`, disables source/hub resolution in frozen mode, keeps compatibility
plugins implicit and rejects unexpected local entries. Host preparation copies the verified
snapshot and package into an isolated directory with pinned launch paths. A capability
marker requires a future release with that CLI support; v1.0.3 is not modified in place.
This does not yet activate synchronized config or enforce serving admission.

Quasar is the desired-config authority. Agent snapshots are observations. Do not
choose the newest node snapshot as authoritative, copy arbitrary plugin state files,
or silently promote a node's local changes to every other node.

## Configuration and activation

- Reuse PluginSdk configuration declarations and schemas. The host owns distribution,
  persistence and revision tracking; ordinary plugins need no cluster flags or config
  synchronization code. An opaque config outside the supported SDK/provider boundary
  needs an explicit host adapter or a documented unsupported setting; never claim it
  was synchronized without verification.
- Stage edits separately from the active revision. Initial policy is coordinated
  full-cluster downtime for plugin binaries and config. Live fan-out alone cannot
  preserve identical settings across serving nodes. Any later live activation needs
  its own proven consistency protocol.
- Stage and validate artifacts/config on the target hosts, shut down the old fleet,
  persist/activate one revision, then validate effective config and successful plugin
  initialization before admitting nodes. Plugins may normalize or reject values;
  read-back must match the intended canonical result. Replacements/spares follow the
  same gate, including when Quasar is unavailable, using locally persisted active state.
- Track a durable operation, expected revision and per-target pending/applied/rejected/
  unknown result tied to cluster/slot/node/incarnation and process identity. A send or
  an old incarnation's acknowledgement is not successful application. Expected fleet
  membership comes from the Registry, not only the connected Agents.
- Interrupted activation resumes the selected operation, or explicitly restores the
  previous complete revision. Keep admission closed while a mixed or unverified fleet
  exists. A staged edit must not change which revision replacement nodes use.
- Block standalone per-Agent plugin writes for cluster Agents in the application
  layer as well as the UI. Route UI/API/CLI edits through shared cluster management,
  authorization and validation. Detect plugin-originated or external local changes as
  drift; do not broadcast them automatically. Preserve secret storage/access rules.

The local host/runtime must enforce boot/readiness checks without a live Quasar/Agent
connection. Missing Agent telemetry is unknown, not a mismatch or a reason to stop
healthy nodes. A proven runtime mismatch is reported and remediated through Registry-
mediated lifecycle, never an independent Agent restart loop. Current code still needs
these deployment/readiness contracts.

## Agent monitoring

Retain incarnation-specific plugin inventory, config observations, stats, profiler
samples and logs. Extend observations with actual loaded artifact identity and the
applied revision/schema/config hashes. Joining those observations to the Registry's
current node identity produces a single cluster plugin view with per-node detail:
for example, revision 12 verified on five nodes, one node unknown. Show missing/extra
plugins, initialization failures and config drift explicitly.

Registry health/ownership stays authoritative. Plugin deployment compliance is a
separate desired-versus-observed result alongside it. Old-process observations must
not verify replacements. Agent/Quasar outages do not invalidate the last locally
activated revision or make existing nodes depend on the control plane.

Keep metrics per node/incarnation by default. Aggregate only when ownership is known
and values are meaningfully combinable. `StatAggregation.AcrossInstances` describes
instances in a provider group; it does not prove that values on different nodes are
disjoint. Repeated global counts must not be summed. No new cluster-specific statistic
annotation is required merely to display an ordinary plugin's existing metrics.

## Existing SDK/API audit

Server plugins use **Magnetar PluginSdk**. **Quasar.Plugin.Abstractions** is a different
API for UI-host extensions; it does not run in every dedicated-server process.
World mods/PB scripts and the mod communication channel are another boundary, covered
by the generic plan's `Modding.md`; that document is not a server-plugin SDK contract.

Reviewed Magnetar source at
[`adbf1616b8963e707358d66928c9f355277b9bd7`](https://github.com/CometWorks/magnetar/tree/adbf1616b8963e707358d66928c9f355277b9bd7/PluginSdk),
Quasar's current working branch and cluster v1.0.3. This Magnetar source checkout is
not proof that a selected deployment contains the same SDK/runtime or registers its
providers; dependency pinning and end-to-end release tests remain required.

| Surface | Existing behavior | Required cluster treatment / gap |
| --- | --- | --- |
| `PluginConfig`, attributes, `ConfigStorage` | Schema/default/value declarations, XML/JSON serialization and change notification; no distributed config store | Keep plugin declarations. Persist/distribute desired config in Quasar/host, verify effective values on every node; no plugin-written replication loop. |
| Agent config provider bridge | Discovers SDK config or an explicit provider; applies values on the game thread and returns a later snapshot | Add durable cluster revision and correlated success/rejection. Current `PluginConfigUpdateRequest` has only plugin ID and values; sending is not confirmed application. |
| `Logger` | Existing environment-selected sinks | Keep plugin calls; host/Agent adds cluster/node/incarnation context and one cluster log view. |
| `PluginStats` and attributes | Self-describing statistics, instance/time aggregation metadata | Keep declarations; Agent labels provenance, Quasar renders per-node data and only justified cross-node aggregates. |
| `ServerCommands`, `CommandContext`, responder | Host registers and dispatches commands using local session caller data; responder supplies replies | Verify one recipient per invocation, caller authorization and routing across player handover. Ordinary commands keep existing declarations. Arbitrary handler logic is not automatically a global operation. |
| `ServerControl` quit/restart variants | Routes through a registered `ClusterLifecycle` provider; otherwise executes the bound standalone action. Provider failures fail closed | Reuse the facade. Verify provider registration before plugins can issue lifecycle requests, and the intended logical scope/acknowledgement. SDK unit support is not proof of v1.0.3 end-to-end wiring. |
| `ServerControl.SaveWorld` / `ReloadConfig` | Calls host-bound delegates directly; does not use the above lifecycle provider | Audit and define safe cluster behavior behind the facade. Never treat a bool/request acceptance as a completed durable cluster operation. |
| `MissionScreens` | Host-bound sender for a player/Steam ID/all; client receiver still required | Keep calls; verify destination and broadcast coverage across nodes, avoiding duplicates and stale player routing. Current facade alone proves no cross-node delivery. |
| `PathResolver` | Path/case compatibility, not shared storage | Keep local paths; it does not replicate plugin files/state. |
| `PluginSdk.Clustering` node link | Single transport-provider rendezvous with byte transport and lifecycle framing | Infrastructure contract, not a general plugin RPC/store/scheduler. Normal plugins should not register a competing provider or use it for config/telemetry synchronization. |

## Transparent by default, honest about scope

The operator confirms that the current plugin selection does not require global-state
compatibility or adaptation. That work is not a prerequisite for integrating this
selection. Shared deployment/config and the existing SDK routing requirements still
apply. Future plugins needing global state or node-aware data handling are an explicit
SDK extension track, described below, required for the first post-beta release.

The compatibility target is unchanged ordinary PluginSdk code. Distribution, config,
logging and monitoring belong to the host stack. Prefer implementing logical player,
command and lifecycle routing behind existing facades over adding cluster checks to
every plugin. Node/partition identity and explicit remote messaging remain opt-in for
plugins deliberately needing them; add SDK surface only for a concrete missing operation.

There is a limit beyond node-aware communication: arbitrary statics, private caches,
file/database writes and timers execute independently in each process. Raw entity
enumeration sees local game objects, not automatically the entire world. Entity-bound
behavior can remain unchanged where ownership and persistence preserve its assumptions;
state kept only in memory needs review across handover/replacement. Identical deployment
cannot provide transparent distributed memory or exactly-once external effects.

Therefore classify behavior, not every plugin with a mandatory cluster marker:

- Existing local/entity operations and supported SDK facilities: make transparent in
  the host/runtime, verify with unchanged-plugin fixtures.
- Logical global operations exposed by a host-owned facade: supply correct routing
  behind that facade and retain documented return/acceptance semantics.
- Intentional topology/remote communication: opt-in cluster API when needed.
- Arbitrary global-state assumptions or direct engine patches: audit the concrete
  behavior, prefer a host compatibility fix, otherwise document minimal adaptation.

Do not claim all old plugins are compatible, elect the WA to run every unknown global
callback, or broadcast command handlers to all nodes. Those choices change behavior
without enough information. Keep this audit as a prerequisite for lifecycle activation.

## SDK shared state and node-aware data

The stage 6 extension is published in Magnetar 2.4.2.3 and cluster 1.1.7.
Live packaged acceptance remains a separate gate.

Plugins opt in through `PluginCluster.ForPlugin(manifestId)`; ordinary configuration
and local APIs stay unchanged. The launcher binds the namespace to the owning assembly.
Registry serializes durable shared records and flushes before success. Reads return
store UUID/revision; CAS accepts schema, payload, operation ID and optional owner fence.
Conflicts are explicit; deletion preserves a tombstone revision. Explicit restore
changes store UUID and clears replay history. Binary rollback never rolls data back.

Owners may be physical node/incarnation, current WA or partition owner. Request/reply
uses authenticated runtime transport and bounded queues. Handlers begin on the game
thread after Registry validation and a lease/freshness recheck; asynchronous continuations
are not game-thread guaranteed. Routing is not an exclusive lock: authoritative effects
must use fenced CAS, and external effects need their own idempotency contract.

Limits: 64 KiB values/messages, 4096 records including tombstones, 8 MiB current/replay
payload, 128 recent write outcomes, 128 pending requests and 30-second message timeouts.
Retries of uncertain CAS use the same ID and arguments. Messaging never automatically
retries or reroutes; timeout does not prove that an admitted handler did not execute.
Standalone provides durable local state/single logical owner. A missing cluster provider
fails unavailable, never falls back to divergent local stores.

The exact API, failure semantics and runnable example are documented in Magnetar's
[SharedState.md](https://github.com/CometWorks/magnetar/blob/v2.4.2.3/skills/se-dev-plugin-sdk/SharedState.md).
Gateway's protocol copy is hash-pinned; release capabilities bind the actual SDK binary
hash. These facilities require coordinated Magnetar 2.4.2.3 / cluster 1.1.7 artifacts.

`PluginStorage.GetSharedDirectory` uses `CLUSTER_SHARED_ROOT`. Quasar generated
single-Host deployments set it to `<Host runtime root>/plugin-shared`, which is
shared by that Host's nodes and included in its stopped snapshots. For several
Hosts, Quasar leaves it unset: the same pathname on separate disks is not shared
storage. An imported deployment specification may provide `sharedStorageRoot`
only after the operator mounts one shared filesystem at that path on every Host.
The SDK does not lock concurrent writes, and Quasar's per-Host snapshots do not
capture an external shared mount. See Magnetar's
[SharedDirectory.md](https://github.com/CometWorks/magnetar/blob/v2.4.2.3/skills/se-dev-plugin-sdk/SharedDirectory.md).

## Configuration and monitoring implementation

The cluster deployment panel reuses the ordinary PluginSdk schema/editor against the
persisted common preparation specification. An edit prepares a new immutable candidate
on every Host. Activation uses the stopped-fleet/full-downtime workflow. No per-node
configuration write bypass is allowed. Canonical storage precedes plugin construction;
continuous live read-back gates runtime readiness independently of Agent.

Registry owns fleet readiness and topology. Agent reports process/slot/incarnation,
revision, proof failure and plugin statistics. `cluster-plugin-services` adds availability,
pending operations, conflicts, failures and ownership changes. Do not sum replica observations into logical
shared-record counts. Missing telemetry stays unknown, not a fabricated mismatch.

Stage 6 unit/self-tests cover replay after owner replacement and Registry restart,
conflicts, tombstones/restore fencing, standalone persistence and queued-handler
revalidation. Package-level and Quasar-outage scenarios still belong to live acceptance.
See [the current integration plan](Phase4IntegrationPlan.md) for all seven stages and
[configuration](Configuration.md#managed-cluster-workflows) for operator workflows.

## Standalone/cluster conversion

The [conversion wizard](ClusterConversion.md) reviews the last authenticated standalone
SDK settings snapshot and exact frozen destination plugin commits before preparing a
shared canonical configuration. An explicit source commit mismatch fails closed.
Reverse conversion exports canonical settings beside the stopped standalone server
for review/reapplication. Arbitrary private files and shared records remain in the
backup; conversion does not infer a plugin-specific storage migration.

## Review corrections — 2026-09-20

SDK configuration loaded through `ConfigStorage` is discoverable even when plugins
keep it in private/static storage. Agent captures additional configuration types using
an optional SDK API, preserving compatibility with older standalone SDKs. Historical
snapshots retain every type; conversion emits canonical schema 2 for multiple types,
and the cluster editor prepares each type independently. Conflicting private-only
instances block automatic conversion rather than selecting arbitrary values. Standalone
editing still needs a public configuration target; capture alone does not grant a safe
mutation target. Hand-rolled storage outside the SDK still needs explicit integration.

Canonical comparison treats equivalent numbers equally. Actual runtime drift remains
fail-closed to preserve the one-server configuration invariant. Registry holds the
failed slot Draining until correction/operator action, preventing endless replacement
of identically misconfigured processes. Existing sessions keep relaying while their
attachment and authority generation remain valid; readiness loss closes new admission.
Persistence faults still pause all relay.

Upstream shared-state writes use bounded durable journal entries, per-plugin capacity
and write-rate limits. Tombstones retain revision history; old globally full stores
need explicit migration to regain new-key capacity. Storage is opened lazily only when
a standalone plugin opts into shared state, and storage failures report unavailability.
The SDK documentation release-tag link becomes available after the coordinated release;
review changes are in Magnetar #57, not a requirement to install from a mutable branch.
