# Cluster mod pinning and content updates

Implementation proposal, 2026-09-27. **Not implemented.** No running deployment is changed by this plan.

## Recommended behavior

Freeze the complete effective mod and plugin set into each deployment. Every regular node,
World Authority (WA), spare and replacement loads those exact bytes. Scheduled checks report
upstream changes; only an explicit administrator action applies a prepared update.

This is mandatory for every dedicated-server process running as a cluster node, including WA.
Cluster mode must require valid deployment pins automatically; absence of a manifest is a
startup error, never permission to fall back to standalone Workshop or plugin updates.

### Pooled cache and deployment pins

Use a pooled, immutable artifact cache. A deployment pins entries in that pool; nodes consume
locally verified copies. Several nodes or deployments can reuse one entry without downloading
or compiling it again.

| Artifact | Cache lookup identity | Verified deployment pin |
| --- | --- | --- |
| Workshop mod | Provider + Workshop ID + Workshop changelog version/update timestamp | That version/timestamp and SHA-256 of the sealed content inventory |
| Plugin source | Repository + exact source commit declared by the plugin manifest (`<Commit>`) | Manifest-declared commit and captured source digest |
| Compiled plugin bundle | Manifest-declared source commit + SDK/runtime/build inputs + dependency identities and effective manifest inputs | Manifest-declared commit and SHA-256 of the compiled bundle inventory |

Mods and plugins have distinct version keys: Workshop changelog version/timestamp for mods,
and the manifest-declared commit hash for plugins. The Git blob SHA of the manifest file can
help cache metadata requests, but is not the plugin version. Artifact hashes verify the bytes
behind these pins; build-input identities prevent reuse of an incompatible compiled bundle.

Use a provider-supplied changelog revision/version where exposed, with the Workshop update
timestamp as the fallback. Do not parse a version number from arbitrary change-note prose.

A timestamp is useful for cache lookup, but does not prove byte identity or guarantee that
Workshop can retrieve that historical version. Record the captured hashes, reject conflicting
content for an existing immutable entry, and never replace its files in place.

```text
Upstream → preparation/download → pooled immutable cache
                                      ↓
                           candidate deployment pins
                                      ↓ explicit update period
                            active deployment pins
                                      ↓
                       Host-local verified artifacts
                                      ↓
                         regular nodes / WA / spares
```

The preparation layer owns fetching, verification and publication. Reuse the existing Host
artifact transfer for separate machines; a pooled cache does not require a shared filesystem
or a new download service. Each Host keeps the artifacts needed by its authorized deployments,
so healthy nodes and locally prepared replacements do not depend on a live central cache.

Concurrent requests for the same artifact share one fetch/build and publish only when complete.
Retain entries referenced by active deployments, prepared candidates and unfinished rollovers;
reclaim only unreferenced entries. A missing local entry can be restored by Host preparation
from the exact pinned cached artifact. If those bytes are unavailable, startup fails; downloading
the latest upstream version is not a substitute.

Initial provisioning establishes the first pins. Thereafter, an explicit update period changes
the authorized pins. During a compatible rolling update, each incarnation is bound to its
authorized old or new revision; running processes never switch files underneath loaded code.
Scheduled checks only observe upstream changes and notify administrators.

Use two update modes:

| Change | Default deployment mode |
| --- | --- |
| Mod bytes, mod order or dependency set changes | Announced maintenance, clean fleet shutdown, activation and client reconnect |
| Server-only plugin change with proven coexistence compatibility | Rolling replacement of regular nodes and WA |
| Plugin compatibility unknown, shared data/schema incompatible, or client companion changes | Announced maintenance; companion changes also require reconnect |
| Description/changelog changes without effective content changes | Information only; no deployment |

Keep existing cluster-package updates on their current full-downtime path. Content updates
must work independently of a newer cluster package. Do not infer rolling compatibility from
a version number, changelog, unchanged SDK version, or an administrator bypass switch.

### Client limitation and proposed admission policy

Freezing server files does **not** pin stock clients. SE downloads Workshop mods while joining;
clients can receive newer files while the server retains an older deployment. Reconnecting
after an update is necessary, but cannot guarantee parity if the author publishes again.

Proposed conservative default: once a provider revision change is observed for an active mod,
notify administrators and temporarily hold new joins/rejoins until they deploy a current
candidate. Existing sessions remain on the active deployment until its announced update.
A changelog edit may produce a conservative hold; preparation can clear it if actual bytes,
dependency graph and order are unchanged. Recheck upstream before reopening admissions.

This narrows the risk; polling leaves a detection window. A strict guarantee requires a
client-side content fingerprint check **before world/mod loading**, enforced by Gateway
admission. A companion mod loaded with the world is too late to guarantee safe initial loading.
Do not promise old Workshop-version delivery to unmodified clients. Supporting indefinitely
frozen mods while admitting fresh clients requires separate client work.

**Product decision before implementation:** accept the proposed temporary join hold, or choose
an explicitly best-effort policy that warns administrators and permits joins with mismatch risk.
The rest of the pinning/preparation design is independent of that choice.

## Verified starting points

Audit uses Quasar `46e43f53795c0ece409a74079b25494411ad4a91`, cluster
`98f1e7c5a714c768fef2a428b0f108f09e727260`, Magnetar
`f478b5f774925179d80795f70b8924d993e597f9`, and decompiled SE **1.210.014 b0**.
The decompiled version matches the installed dedicated-server binaries.

| Layer | Existing behavior and gap |
| --- | --- |
| Quasar dependency staging | `ClusterDependencyService` freezes runtime/plugin files; there is no Workshop payload root. `QuasarModSelection` contains ID/name/dependency status, without content provenance. |
| Magnetar preparation | `Legacy/Preparation/ManagedPreparation.cs` explicitly rejects Workshop `ModPlugin` exports. Its current managed plugin exporter is a useful owner to extend, not evidence of mod pinning. |
| Quasar activation | `ClusterUpdateService` waits for clean shutdown, activates a revision, then waits for fleet readiness. Reuse for content maintenance updates. |
| Cluster managed admission | `ClusterRegistry/ManagedDeployment.cs` authorizes one deployment revision. A new generation requires clean Down or recovery with unchanged inventory; executor reports and runtime proofs must match the active revision. |
| Node admission | `NodeAdmission.Validate` checks config, binary version, replication type table, world settings and identity. Mixed revision authorization cannot bypass those checks. |
| WA move | `POST /admin/v1/world-authority/move` calls `CloseNode`; NodePlan supplies the successor. The operation completes after requesting the drain, before successor readiness. No target deployment revision is selected. |
| WA persistence | `WorldAuthorityPersistence` registers a final global save and marks Empty; exit includes a bounded wait for live regular peers. A live WA move needs explicit handoff semantics distinct from whole-fleet shutdown. |
| Notifications | Existing bell/push supports multiple subscriptions and cluster access checks, but `UpdateNotices.Current` selects one notice and push stores one `LastNoticeKey`. Content notices need to coexist with other outstanding updates. |

### SE Workshop download paths

Inspected through `se-dev-server-code`, under `Data/Decompiled`:

```text
MySessionLoader.LoadDedicatedSession
  → MyWorkshop.DownloadWorldModsBlocking
    → DownloadWorldModsBlockingInternal
      → dependency resolution + DownloadModsBlocking
        → DownloadModsBlockingUGC → UpdateMod
          → MySteamWorkshopItem.Download → SteamUGC.DownloadItem

Magnetar PluginLoader
  → SteamMods.Update → SteamMods.UpdateInternal
    → reflected MyWorkshop.DownloadModsBlocking
```

`UpdateMod` refreshes remote state and downloads when the item is not current. Skipping only
the outer call misses Magnetar's plugin path. Returning success alone also fails: SE binds
each checkpoint `ModItem` to a `MyWorkshopItem`; `ModItem.GetPath()` uses that item's `Folder`.
`ModItem` is a struct, so bindings must be written back into the list.

Magnetar already owns an Early Harmony patch in `Legacy/Patch/Patch_MyWorkshop.cs`. It injects
the implicit companion mod and repairs legacy archives afterward. `Legacy/Extensions/ModPlugin.cs`
already uses a `MyWorkshopItem` subclass to bind a folder. Extend those responsible paths.

Dedicated servers reject local mods with `PublishedFileId == 0`; retain published IDs and
service names. `MySessionLoader.LoadMultiplayerSession` separately calls `DownloadModsAsync`,
confirming that a server patch does not control client downloads.

## Implementation sequence

### 1. Extend deployment provenance and stage immutable content

**Owners:** Quasar dependency/preparation services, shared deployment archive validation,
Magnetar preparation, and cluster preparation/launch contracts.

- Add a mod lock to the existing hashed dependency manifest and bind it to the deployment
  revision. Keep one source of truth; avoid an independent runtime mod database.
- Implement the pooled cache above in the preparation/artifact layer. Reuse verified entries
  by identity, coalesce concurrent fetches, and keep mutable download staging separate from
  published entries. Deployment manifests reference hashes; cache metadata cannot change pins.
- Record service + Workshop ID, explicit/dependency/companion origin, dependency edges,
  effective load order, changelog version/update timestamp, provider content handle where available,
  retrieval time, normalized relative paths, sizes and SHA-256 file inventory. Hash the
  canonical inventory and effective ordering. Timestamps are observations, not content hashes.
- Include every effective mod: configured world mods, transitive dependencies, plugin-associated
  Workshop mods and the implicit Magnetar companion. Record companion opt-out/crossplay effects
  when resolving the effective set; startup must not silently change it.
- Pin plugins by the exact source commit declared in their manifest. Retain the source repository,
  captured manifest, SDK identity, dependency/assets and compiled artifact hashes as provenance.
  Build once for the selected runtime and distribute identical outputs to every Host.
- Prepare in an isolated Workshop download/cache directory. Use the existing Steam UGC
  download behavior inside a preparation process; do not implement a second downloader in
  Quasar or pretend a metadata query downloads the payload. Establish the required Steam
  initialization in that process without starting a serving world.
- Resolve and download the complete set, recheck provider revisions/dependencies after download,
  and reject/retry a candidate if uploads raced preparation. Never label newly downloaded bytes
  with a previously observed revision without verification. Record unavailable provider identity
  honestly; local hashes still identify captured bytes.
- Normalize legacy archives before sealing; record original and normalized hashes. Validate
  extraction paths, symlinks, file collisions and sizes through the existing archive boundary.
  Copy or use copy-on-write snapshots; no writable hardlinks into Steam's mutable cache.
- Publish atomically and transfer through existing authenticated Host preparation. Validate every
  Host's complete inventory before activation; the design must work across separate machines.
- Keep world saves, plugin storage, logs and other writable data outside sealed content. Retain
  active and in-progress artifacts using existing deployment retention rules. Do not depend on
  Workshop being able to redownload historical versions.

**Exit proof:** one candidate contains the full effective content set, and two Hosts verify
identical inventories without independently resolving or rebuilding anything.

### 2. Enforce pinned loading in Magnetar and cluster readiness

**Owners:** Magnetar Workshop patch/loader; cluster runtime admission and launch contracts;
Quasar Host `NodeActualizer` and shared preparation validation.

- Add one managed mod resolver used by both the world-download patch and `SteamMods.UpdateInternal`.
  In managed mode it validates the lock, verifies files, binds the requested IDs to immutable
  folders and returns the appropriate success/cancellation/failure result without remote lookup.
- Activate enforcement early enough to cover plugin loading, initial world load, restarts and
  replacements. Require a deployment-supplied manifest/capability; no per-plugin cluster toggle.
  Detect cluster-node mode independently of manifest presence, so missing pins cannot disable
  enforcement. Cover plugin resolution/auto-update as well as both mod download paths.
- Resolve dependencies during preparation, not startup. Reject missing, extra, altered or
  unexpectedly reordered mods and missing/corrupt artifacts. No network fallback to latest.
- Ensure `ModPlugin.ModLocation` and world `ModItem` bindings use the same frozen artifacts.
  Skip archive repair on sealed runtime folders. Preserve Workshop consent and script trust
  rules; a supplied manifest must not make arbitrary files trusted.
- Add content identity to locally verified readiness and incarnation-specific runtime proofs,
  covering regular nodes and WA. Refuse managed startup when the runtime cannot enforce pins.
  Quasar being offline must not affect startup from a valid locally active deployment.
- Preserve standalone behavior through the existing managed-mode boundary. Coordinate updated
  Magnetar/cluster/Host contracts and release pins; no legacy transition adapter is proposed.

**Exit proof:** after a mod changes upstream, restarting any managed role uses the old recorded
hash with Workshop unavailable. A missing or modified file prevents serving admission.

### 3. Scheduled observations, changelogs and notifications

**Owner:** Quasar. Add a focused content monitor following `ClusterReleaseMonitor`; reuse
`QuasarWorkshopModResolver` and the existing update interval (currently 15 minutes by default).

- Batch metadata requests for the union of active mod IDs and plugin manifests across clusters.
  Use conditional GitHub requests, bounded concurrency, cancellation, backoff and rate limits.
  Provide “Check now” using the same path.
- Compare against each **active** content lock. Keep selected/prepared candidates separate.
  Persist observations so restarts do not erase pending updates or repeatedly notify users.
- Detect Workshop candidates by comparing the changelog version/update timestamp to the active
  mod pin, including dependency changes.
  Download/hash during explicit preparation to establish whether payloads really differ.
  Change-note prose explains changes; it is not a machine-readable version contract.
- Fetch Workshop change notes best effort through an available supported source; always provide
  the Workshop changelog link. Missing/private/removed items and failed checks show actionable
  unknown/unavailable states, never “up to date.” Do not make HTML scraping a deployment gate.
- Compare each plugin manifest's declared `<Commit>` against the active plugin pin to detect
  source updates. Use the manifest blob SHA only to avoid redundant metadata fetches. Also
  validate dependency, asset and compatibility changes when preparing a candidate; changes to
  effective deployment inputs must remain visible even if the declared source commit is unchanged.
  Title/description-only changes do not require a rollout. A changed declared commit is an
  update even when the displayed version string is unchanged.
  Offer source commit comparisons/release notes as plugin change context.
- Extend notice selection to enumerate outstanding authorized notices. Persist bounded delivery
  state per subscription and stable cluster/content revision key; acknowledge successful sends
  only. An outstanding Quasar update must not starve mod/plugin notices. Preserve multiple-client
  delivery, revoked-access checks and automatic cluster-details callback URLs.
- Show separate package and content status. Hide the package update section when current,
  while still showing pending mod/plugin updates. Include last checked time and partial failures.

**Exit proof:** one upstream change produces a durable notice for every authorized subscribed
client, without changing running files, downloading candidates or starting a deployment.

### 4. Explicit content update using the existing maintenance workflow

**Owner:** Quasar preparation/update services and deployment panel, with cluster admission/chat.

- Add “Review content updates” and “Prepare update” to cluster details. Default preparation
  resolves one coherent candidate; reuse topology, package, world seed and settings. Show changed
  mods/plugins, provenance, notes and required mode. No GUID/JSON input in the normal flow.
- Require an explicit “Apply update” action for the exact prepared revision. Power users may
  select a subset only if its complete dependency closure can be prepared and validated.
- Extend the existing durable update workflow with announcement/countdown and admission control.
  Broadcast once per connected player through the existing cluster-wide messaging route,
  including expected disconnect/rejoin behavior. Record announcement progress across restarts.
  The current `/admin/v1/chat` requires a real sender identity. Add an authorized system-announcement
  operation through the same WA chat machinery if unattended announcements cannot use that
  contract; do not invent a Steam ID or require an administrator to be online in-game.
- Immediately before the window, recheck the candidate's Workshop observations. If superseded,
  leave the approved bytes unchanged and require preparation/review of a new candidate.
- Stop cleanly, including WA and final saves; activate the prepared revision; require all runtime
  content/config proofs and Serving readiness; then reopen admission under the chosen policy.
  Send completion/failure notifications and a post-update message to returning players.
- Keep operations idempotent and bound to expected active/candidate revisions. Serialize against
  package updates, conversion, restore and another content update. An unstable/draining cluster
  must not expose a parallel Start action.
- On timeout or failed proof, show the exact blocked step and retain safe admission state.
  Forced termination is not successful maintenance. Do not automatically restore old binaries
  over data already written by new plugins; reuse explicit recovery/backup controls.

**Exit proof:** a mod/plugin-only update works on the latest cluster package, resumes after
Quasar restart, announces the window, and finishes only with the entire fleet verified.

### 5. Compatible plugin rollover, including World Authority

**Owners:** cluster Registry/AdminContract and runtime, Quasar Host executor, Quasar orchestration.
This requires upstream cluster work; a Quasar loop calling drain is insufficient.

- Extend managed deployment authorization with a durable rollout operation: source/target
  revision, expected current revision, approved compatibility, per-slot desired revision and
  incarnation-bound progress. Permit only those two authorized revisions during that operation.
- Keep config/binary/type-table/world-settings checks. Define which plugin revisions may coexist
  without changing replication types, inter-node messages, shared data schemas or configuration
  meaning. Unknown compatibility selects maintenance. SDK compatibility alone is insufficient.
- Stage all target Host artifacts before starting. Extend executor poll/spawn/report contracts
  and Host storage so old and new bundles can coexist and each replacement selects its authorized
  revision. Unexpected old-revision respawns must not reverse completed progress.
- Check spare capacity and ports up front. Start a candidate regular node as a non-owning spare,
  verify readiness, transfer partitions/player routing using existing handover machinery, then
  drain the old incarnation. Proceed one bounded step at a time; do not drain the whole fleet.
- Extend WA move with target revision and observable lifecycle completion. Reuse final global
  saves, generation fencing and NodePlan replacement; do not treat the existing HTTP operation's
  completion as proof that the successor is ready.
- Before WA transfer, quiesce global mutations, finish acknowledged persistence/voxel work and
  capture the final authoritative state. Fence the outgoing generation, restore the successor,
  prove its content/config/readiness and rebind authority-dependent sessions before resuming.
  At most one WA may write; a prepared successor must not register as active early.
- Separate WA move from its whole-fleet peer-drain wait. Preserve voxel journal service/state
  across handoff while regular nodes stay alive. Check player admission generation rebinding
  and global command retries; preserve exactly-once effects where existing contracts require them.
- Use regular nodes first, WA last only for revisions verified compatible in both mixed-role
  directions. Otherwise choose maintenance; no guessed ordering makes incompatible code safe.
- Pause on failed readiness, drain or save acknowledgment. Persist enough progress for Quasar,
  Gateway and Host restarts to reconcile the same operation. Timeout/force-kill is failure or
  explicit recovery, not a transparent successful handoff.
- Complete only when all expected regular slots and WA attest the target revision and no old
  incarnation remains authorized. Retire old artifacts only after those references are gone.

**Exit proof:** cross-Host plugin rollover preserves partition ownership and WA single-writer
authority through injected crashes. Only call it seamless after live client tests demonstrate
preserved sessions; a bounded authority pause may still be necessary.

## Focused acceptance matrix

| Scenario | Required result |
| --- | --- |
| Upstream mod changes; regular node or WA restarts | Same pinned hashes, no Workshop refresh |
| Missing/corrupt file, dependency, or unsupported enforcement capability | Preparation/startup refused with artifact identity |
| Companion/plugin mod enters through the second loader path | Same verified resolver and lock |
| Workshop upload or dependency change races preparation | Candidate rejected/retried; active deployment untouched |
| Archive repair and extraction | Only staging mutates; sealed hashes remain valid; traversal/symlink escape rejected |
| Separate Hosts and unavailable Workshop at restart | Complete identical content, readiness independent of Workshop |
| Multiple nodes/candidates request the same cache entry concurrently | One fetch/build, atomic publication, identical verified bytes |
| Missing cluster manifest or missing historical cache entry | Startup refused; no standalone/latest-version fallback |
| Cache cleanup during preparation or rollover | Every referenced old/new artifact retained; active pins unchanged |
| Metadata-only update, removed item, missing notes, API throttling | Correct informational/unknown/update state; no silent activation |
| Multiple clusters/clients; Quasar notice already present; service restart | Authorized deduplicated content notices still delivered |
| Same cluster package version, newer mod/plugin | Content action remains visible and prepares a valid candidate |
| New/rejoining stock client after upstream publication | Chosen admission policy enforced; no claim that stock client bytes are pinned |
| Crash mid-drain, Host disconnect, insufficient spare capacity | Durable blocked/resumable operation; no accidental fleet shutdown |
| WA move with regular nodes alive; late writes from old WA | Verified final state, old generation fenced, sessions rebound, no lost voxel/global state |
| Plugin changes schemas or client companion | Maintenance selected; mixed-version serving rejected |

Update Quasar architecture/configuration/cluster-plugin docs, cluster Admin API/deployment docs,
and Magnetar managed-preparation/usage docs alongside their implementation phases. This plan
requires source/contract tests plus targeted live multi-Host acceptance; source inspection alone
does not establish seamless behavior.

## Source references

- Quasar: `Quasar/Services/ClusterDependencyService.cs`, `ClusterUpdatePreparationService.cs`,
  `ClusterUpdateService.cs`, `QuasarWorkshopModResolver.cs`, `ClusterReleaseMonitor.cs`;
  `Quasar/Services/Updates/UpdateNotice.cs`, `PushNotificationService.cs`;
  `Quasar.Host/NodeActualizer.cs`; `Shared/ClusterDeployment/`.
- [Magnetar Workshop patch](https://github.com/CometWorks/magnetar/blob/f478b5f774925179d80795f70b8924d993e597f9/Legacy/Patch/Patch_MyWorkshop.cs),
  [secondary download path](https://github.com/CometWorks/magnetar/blob/f478b5f774925179d80795f70b8924d993e597f9/Legacy/Loader/SteamMods.cs),
  [managed preparation](https://github.com/CometWorks/magnetar/blob/f478b5f774925179d80795f70b8924d993e597f9/Legacy/Preparation/ManagedPreparation.cs).
- [Cluster managed revision enforcement](https://github.com/CometWorks/cluster/blob/98f1e7c5a714c768fef2a428b0f108f09e727260/ClusterRegistry/ManagedDeployment.cs),
  [WA move handler](https://github.com/CometWorks/cluster/blob/98f1e7c5a714c768fef2a428b0f108f09e727260/ClusterRegistry/AdminApiMutations.cs),
  [WA persistence](https://github.com/CometWorks/cluster/blob/98f1e7c5a714c768fef2a428b0f108f09e727260/Shared/Cluster/WorldAuthorityPersistence.cs).
- SE skill `Data/Decompiled`: `Sandbox.Game/Sandbox/Game/World/MySessionLoader.cs`;
  `Sandbox.Game/Sandbox/Engine/Networking/MyWorkshop.cs`;
  `VRage.Game/VRage/Game/MyObjectBuilder_Checkpoint.cs` (`ModItem`);
  `VRage.Steam/VRage/Steam/MySteamWorkshopItem.cs`.
