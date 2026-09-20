# Convert between a standalone server and a cluster

Conversion creates a separate destination and leaves it stopped. Source data and
backups remain intact. Review the destination before manually switching players
over; conversion never deletes or restarts the source.

These workflows require Cluster Manage, Edit Servers and Edit Configs permissions,
plus access to the selected cluster. The initial cluster release targets Linux.

## Standalone server to cluster

1. Review plugin settings while the source Agent is connected, then stop the server.
   Quasar retains the last authenticated SDK configuration snapshot, including its
   capture time. It is historical data, not evidence that the Agent is still online.
2. Use **Convert to cluster** on the server row. Choose an empty registered cluster
   with a verified release package and frozen dependency snapshot. The page links to
   cluster registration and package/dependency setup when needed.
3. Review the frozen plugin commits. The common plugin IDs must match the source
   selection, apart from implicit compatibility/Agent plugins. Frozen commits govern
   the deployed versions; moving source version labels may resolve differently.
   An explicitly selected 40-character source commit must match the frozen commit.
4. Enter Host endpoints, private IPs, credential environment-variable names, game
   build, internal networks and public Steam port. Choose the Gateway/World Authority
   Host. At least two regular nodes are required: the shipped converter seeds slots
   1 and 2. Additional regular nodes may start without seed partitions. Each Host
   supports up to 32 regular nodes in this wizard, with 254 across the cluster.
5. Confirm **Back up and convert**. Quasar reserves the source against supervised
   starts, creates both server and world backups, and converts a verified copy with
   the release's MagnetarWorld tool. It sends the complete verified installation and
   converted world to each Host, prepares the shared specification, then activates
   the stopped deployment. Package role plugins remain exactly as shipped.

The reviewed profile supplies session settings, mods and administrators. Recorded
SDK plugin configuration becomes the canonical configuration applied to every node.
Password/group restrictions, bans, reserved slots and non-public online modes are
currently refused because the managed release generator cannot carry them through.
Non-SDK configuration providers are also refused. Resolve these limitations explicitly
before migration; Quasar does not silently discard access restrictions or config.
Plugins' arbitrary private files and global stores are retained in backups and need
plugin-specific migration if used. The converter may reject worlds outside its
supported layout, for example movable grids without a static anchor.

Regular node backend/control ports start at 28417/29417 on each Host. World Authority
uses 28700/29700; Gateway control uses the destination cluster's configured URL.
Credential fields contain environment-variable names, never secret values. Provision
the corresponding credentials on Quasar and Hosts before running the workflow.

## Cluster to standalone server

1. Shut down the source cluster cleanly and wait for the matching clean-Down proof
   and stopped Host processes. Select the release/dependencies of its active revision.
2. Open cluster details and choose **Convert to standalone server**. Select a new
   name, port and base profile for server settings/plugin selections.
3. Confirm the conversion. Quasar captures verified native snapshots from every Host
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
Shared plugin records, Registry state and per-node private files remain in the native
backup; they are not automatically merged into standalone plugin storage.

## Progress, interruption and API/CLI

The page shows progress and places a conversion UUID in its URL. Reopen that URL to
resume the immutable request. Failed attempts retain source/backup data and verified
intermediate outputs. A new operation key retries the same UUID/request; an existing
operation key replays its recorded result. Changed inputs require **Edit as a new
conversion**, which creates a new UUID. If Host activation partially succeeded,
resume the original request before starting the destination.

Conversion receipts and working copies live in `BackupDirectory/Conversions/<uuid>`.
Standalone backups use existing backup storage; cluster snapshots live under
`BackupDirectory/Clusters`. Conversion receipts, manual backups and Host conversion
inputs are retained for explicit cleanup after cutover/recovery needs have ended.
Large transfers, Host preparation/activation and the converter have two-hour limits;
ordinary Host control requests retain a 30-second limit.

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

The forward request includes `id`, `server`, `sourceRevision`, `binaryVersion`,
`gatewayHost`, `steamPort`, `joinTokenEnvironmentVariable`,
`adminTokensFileEnvironmentVariable`, `internalNetworks`, `hosts`,
`dependencySha256` and `packageRevision`. Each Host specifies `hostId`, `commandUrl`,
`tokenEnvironmentVariable`, `executorTokenEnvironmentVariable`, `address` and
`regularNodes`. Pin the reviewed destination dependency hash and package revision.
The reverse request includes `id`, `uniqueName`, `displayName`, `port`,
`configProfileId` and the reviewed `sourceLifecycle`.

Implementation tests exercise safety guards, transfer corruption, snapshot assembly,
replay and publication. Live game conversion parity and manual player cutover remain
part of the full local cluster acceptance run.
