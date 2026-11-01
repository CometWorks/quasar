# Convert between a standalone server and a cluster

Conversion creates a separate destination and leaves it stopped. Source data and
backups remain intact. Review the destination before manually switching players
over; conversion never deletes or restarts the source.

These workflows require Cluster Manage, Edit Servers and Edit Configs permissions,
plus access to the selected cluster. The initial cluster release targets Linux.

## Standalone server to cluster

1. Use **Convert to cluster** in the standalone server's **More server actions** menu.
2. In **Destination cluster**, keep **Create a new cluster** or select an empty existing
   cluster. Clusters with a deployment are excluded so conversion cannot overwrite a world.
3. Click **Convert to cluster**. No release, dependency, topology, credential or game-build
   fields and no review checkbox are required.

Quasar checks that access restrictions and recorded plugin settings can transfer, reserves
its source against concurrent offline operations and supervised starts, then saves and stops
it if running. Automatic stopping waits up to five minutes without force-killing the process.
The source remains Off with automatic startup disabled, including after an interrupted attempt.

Quasar creates a destination ID when needed and retains the source's server/world names.
It chooses an enrolled host matching the destination's address, otherwise the local Linux
Host, otherwise the first enrolled host. It starts the local Host when supported and waits
for the selected Host to connect. A system without a local Linux x64 Host needs an enrolled
Linux host; errors explain that prerequisite without adding conversion fields.

Quasar chooses an available managed port range starting at 28000, checks it against local
standalone servers and other cluster placements, and deploys two regular nodes, one hot spare
and World Authority on that host. It downloads a compatible verified cluster release,
prepares the source's plugin selection, freezes runtime and plugin inputs, determines the
node binary version, and generates and installs cluster credentials. Existing empty clusters
receive these managed endpoints, credentials and inputs automatically. Capacity can be changed
later from the cluster's controls.

The existing offline conversion then creates both server and world backups, converts a copy
with MagnetarWorld, stages the profile's Workshop mods, transfers verified inputs, prepares
and activates the stopped destination. The source world and backups remain available.

The reviewed profile supplies session settings, mods and administrators. Recorded
SDK plugin configuration becomes the canonical configuration applied to every node.
Password/group restrictions, bans, reserved slots and non-public online modes are
currently refused because the managed release generator cannot carry them through.
Non-SDK configuration providers are also refused. Resolve these limitations explicitly
before migration; Quasar does not silently discard access restrictions or config.
Plugins' arbitrary private files and global stores are retained in backups and need
plugin-specific migration if used. The converter may reject worlds outside its
supported layout, for example movable grids without a static anchor.

Automatic conversion keeps all nodes on one host so their plugin shared directory lives
under the Host executor's durable runtime root and is included in snapshots. It does not
create a shared filesystem between machines.

For the selected player port P, Registry administration uses P+16; node backends start at
P+1100, controls at P+1200, and World Authority uses P+1164/P+1264. Quasar generates
credentials in its protected credential store and installs them on the Host; the conversion
page does not ask for environment variables or secrets. Firewall rules and existing Steam
Workshop credentials remain deployment prerequisites.

## Cluster to standalone server

1. Shut down the source cluster cleanly and wait for the matching clean-Down proof
   and stopped cluster processes on every host machine. Select the release/dependencies of its active revision.
   A cluster activated after a clean Down can be converted before it starts again. A cluster that has never
   run since setup has no saved state to convert; start it once first.
2. Open cluster details and choose **Convert to standalone server**. Select a new
   Server name, Public server port and base configuration profile for server settings/plugin selections.
3. Confirm the conversion. Quasar captures verified native snapshots from every Host executor
   and extracts copies into private conversion storage. MagnetarWorld reassembles
   the world from the split seed and saved partitions/global state across those
   copies. Unused spare-node directories are skipped. No shared filesystem is needed.
4. Review the new standalone server. Session settings and mods come from the converted
   world. Quasar clones the selected profile, removes cluster role/transport plugins,
   then publishes the server only after its files are prepared. Goal is Off and
   AutoStart is disabled.

Canonical plugin settings are exported to `cluster-plugin-configurations.json` in
the new server directory. They are **not automatically written into arbitrary
plugin-owned configuration files**. Review/reapply those settings before starting.
Shared plugin records, Cluster Registry state and per-node private files remain in the native
backup; they are not automatically merged into standalone plugin storage.

## Progress, interruption and API/CLI

The page shows progress and places a conversion UUID in its URL. Reopen that URL to
resume the immutable request and its generated placement. Failed attempts retain source/backup data and verified
intermediate outputs. A new operation key retries the same UUID/request; an existing
operation key replays its recorded result. Changed inputs require **Edit as a new
conversion**, which creates a new UUID. If Host executor activation partially succeeded,
resume the original request before starting the destination.

Conversion receipts and working copies live in `BackupDirectory/Conversions/<uuid>`.
Standalone backups use existing backup storage; cluster snapshots live under
`BackupDirectory/Clusters`. Conversion receipts, manual backups and Host executor conversion
inputs are retained for explicit cleanup after cutover/recovery needs have ended.
Large transfers, Host executor preparation/activation and the converter have two-hour limits;
ordinary Host executor control requests retain a 30-second limit.

API routes under `/api/v1/clusters/{name}`:

| Method | Route | Purpose |
| --- | --- | --- |
| GET | `/convert/review/{server}` | Source revision, plugin selection and recorded settings age |
| GET | `/convert/plugins` | Verified destination plugin IDs and source commits |
| POST | `/convert/from-server` | `ServerToClusterRequest`; requires `Idempotency-Key` |
| POST | `/convert/to-server` | `ClusterToServerRequest`; requires `Idempotency-Key` |
| GET | `/conversions/{uuid}` | Persisted progress/completion |

CLI equivalents are `cluster conversion-review NAME SERVER`,
`cluster conversion-plugins NAME`, `cluster convert-to-cluster NAME REQUEST.json`,
`cluster convert-to-server NAME REQUEST.json` and `cluster conversion-status NAME UUID`.
For mutations supply `--idempotency-key KEY`; large conversions can use
`--timeout 3600`. An HTTP timeout does not prove conversion failed: inspect progress
and resume using the original UUID/request.

The forward endpoint also accepts automatic conversion requests. Set `automatic: true`,
provide `id` and `server`, and set `createCluster: true` to create the cluster named in the
route; otherwise that route must name an empty existing cluster. Other forward fields are
supplied by Quasar in automatic mode. The conversion page generates a new cluster ID and
checks cluster access before sending this request. Saved manual requests remain resumable.

For advanced manual API/CLI conversion, the forward request includes `id`, `server`, `sourceRevision`, `binaryVersion`,
`gatewayHost`, `steamPort`, `joinTokenEnvironmentVariable`,
`adminTokensFileEnvironmentVariable`, `internalNetworks`, `hosts`,
`dependencySha256` and `packageRevision`. Each Host executor placement specifies `hostId`, `commandUrl`,
`tokenEnvironmentVariable`, `executorTokenEnvironmentVariable`, `address` and
`regularNodes`. Pin the reviewed destination dependency hash and package revision.
The reverse request includes `id`, `uniqueName`, `displayName`, `port`,
`configProfileId` and the reviewed `sourceLifecycle`.

Implementation tests exercise safety guards, transfer corruption, snapshot assembly,
replay and publication. Live game conversion parity and manual player cutover remain
part of the full local cluster acceptance run.

SDK-loaded private/static configuration and multiple configuration types are included
in reviewed snapshots when the compatible SDK discovery API is available. Conflicting
private-only copies block automatic transfer. Each type has its own canonical entry
and cluster editor; older single-type snapshots remain readable.

An unmanaged Cluster Registry cannot be adopted in place. Back up and stop it, use the
release world tools to export/reassemble verified copies into a standalone world, then
convert/import that world into a fresh managed deployment. Preserve the original Cluster Registry
and plugin stores for explicit migration or rollback. Quasar's reverse wizard requires
a managed source and does not provide an unmanaged Cluster Registry migration.

## Operator labels and suggested names

Standalone-to-cluster conversion has only **Destination cluster** (including **Create a new
cluster**) and **Convert to cluster**. Quasar generates the new cluster ID and copies the
source's names; there are no topology rows or advanced registration links on this page.

A standalone destination uses **Server ID** and **Server name**. Its initial name
comes from the source cluster; clearing it fills an editable generated suggestion
on blur or submission. The resulting request saves that name. A populated Server ID
is preserved, and resumed conversions use their recorded request unchanged. See
[Cluster terminology](ClusterTerminology.md) for the complete label map.
