# Clusters

A Quasar cluster runs one Space Engineers 1 world across several Magnetar server processes (nodes) on one or more Linux machines, with one public server port for players. This page covers the cluster terms, the cluster pages, start/stop, plugins, mods, updates, backups, conversion to and from a standalone server, storage and the `Quasar cluster` command line. Creating a cluster is in [Guided cluster setup](ClusterSetup.md), the per-machine service in [Cluster hosts](ClusterHosts.md).

Managed clusters are in beta. The setup, conversion and cluster pages show "Managed clusters are still in beta. Review your backups before changing worlds, mods, or plugins." Each signed-in user can dismiss it once; Quasar remembers the dismissal per account in `<install root>/cluster-beta-dismissals.json`.

## Terms

| Term in the UI | Meaning |
| --- | --- |
| Host machine | A physical or virtual Linux computer that runs cluster processes. |
| Host executor | The `Quasar.Host` background service on a host machine. It starts and supervises the Cluster Gateway and the nodes. See [Cluster hosts](ClusterHosts.md). |
| Cluster Gateway | Routes player traffic. Players connect to its public server port. |
| Cluster Registry | Runs in the same process as the Cluster Gateway. It owns coordination, placement, drain and administration. Quasar talks to its HTTP admin endpoint (the "Cluster Registry admin URL"). |
| World Authority (WA) | A special node that owns world-wide state. It runs on the Cluster Gateway host machine. |
| Regular node | A Magnetar process that simulates part of the world (a partition). |
| Hot spare | A regular node that runs warm and holds nothing until a node crashes; then it takes over that node's partitions, and the crashed node restarts as the new spare. Shown as **Hot spare** in **Nodes and players**. |
| Cluster Gateway and World Authority host machine | The placement choice for the machine that runs the Gateway, Registry and World Authority. |
| Public server port | The UDP port players connect to. Separate from the internal node ports and the Registry admin port. |
| Host machine ID / Cluster ID / Server ID | Permanent IDs. Server name and Host machine name are the editable display names. |
| Configured clusters (Hosts page) | Count of configured cluster attachments on a Host executor, not live readiness. |
| Connected agents (Hosts page) | In-process Quasar.Agent telemetry connections, separate from Host executors. |

Older names still appear in JSON fields and the API: `gatewayUrl` and `gatewayAdminTokenEnvironmentVariable` mean the Cluster Registry admin endpoint and its credential, `HostId`/`hostCommandUrl` mean the Host executor, `steamPort`/`playerPort` mean the public server port.

New dialogs prefill names and IDs such as `cluster-a1b2c3d4e5f6`, `host-machine-a1b2c3d4e5f6` or `server-a1b2c3d4e5f6` (prefix plus 12 hex characters). These are real values that you can edit. Clearing a name fills in a new suggestion when the field loses focus.

## Requirements and permissions

- Cluster package staging, dependency provisioning and guided setup need Quasar on Linux x64. On other platforms Quasar reports "Cluster release staging requires Linux." or "Guided cluster setup currently requires Linux x64."
- Cluster packages come from GitHub releases of `CometWorks/cluster` (asset `ClusterForLinux-<version>.tar.gz` plus `SHA256SUMS`). Only published, non-prerelease releases can be staged. The release check needs a GitHub token in **Updates → GitHub token** with access to that repository; without one the cluster page shows "A GitHub token with access to CometWorks/cluster is required."
- Roles: Viewer can open cluster pages and the console (cluster query). Editor and Admin can start, stop and change clusters (cluster manage). Guided setup, profile staging and conversion also need configuration editing (Editor or Admin). Enrolling host machines needs security administration (Admin only). See [Access control](AccessControl.md).
- Service principals with the `cluster.query` or `cluster.manage` scope can use the cluster API and CLI, limited to the cluster IDs in their `Clusters` list. See [Access control](AccessControl.md).

## Cluster pages

- **New cluster** on the Dashboard and Servers pages opens `/clusters/new` (page title "Set up cluster"). See [Guided cluster setup](ClusterSetup.md).
- Clusters appear next to standalone servers on the Dashboard, in cards and list view. A cluster card shows the Server name, Cluster ID, Gateway URL, **Desired state**, Start/Stop and Save buttons, the console button and **Manage cluster**.
- **Hosts** (nav group "Infrastructure & tools", `/hosts`, page title "Host machines") lists Host executors and their reachability. See [Cluster hosts](ClusterHosts.md).

The cluster page is `/clusters/<Cluster ID>`. From top to bottom:

| Section | What it holds |
| --- | --- |
| Versions | **Deployed version** (from the active deployment, "Unknown" for older deployments without a package record) and **Latest available version**, with the release check time or error. |
| Resume banner | Shown when the cluster has no active deployment: **Resume guided setup** if a setup exists, otherwise **Set up a new cluster**. |
| Administration | Start / Stop, **Save world** (while running), **Cluster settings**, **Plugin configuration**, **Advanced cluster controls**. |
| Cluster overview | Status, Capacity, Players, Recovery, "World, configuration & runtime", and **Last Cluster Registry process output** when the Gateway failed to start. |
| Nodes and players | Slots, registered nodes, performance and players. Shown while the goal is On or the Gateway still runs. Node actions **Drain** and **Force remove node…**; player actions **Kick** and **Ban**. Tabs for events, plugin statistics and recent plugin logs. |
| Mods and plugins | Content update checks (only with an active deployment). |
| Maintenance | Release update, pending update, **Backups**, **Recovery**, **Convert to a standalone server**, **Advanced deployment tools**. |

The cluster page refreshes status every five seconds. When a managed cluster is stopped, Quasar does not show Cluster Registry connection failures as warnings.

### Cluster console

The terminal icon on the card or page opens the cluster console. It has two tabs, **Cluster Registry events** and **Node plugin logs** (up to 1000 most recent entries), and refreshes every five seconds. If the Registry cannot be reached it keeps the shown entries and says "Live feed unavailable. Retained entries may be stale." Full Gateway and node process logs stay on the host machines; see [Cluster hosts](ClusterHosts.md#logs).

## Starting and stopping

**Start** sets the cluster goal to On. **Stop** asks "Shut down cluster?" ("Players will disconnect while the cluster saves and drains."), then sets the goal to Off. A background reconciler in Quasar does the work every 2 seconds:

- Goal On: it asks the Host executor on the Gateway host machine to start the Cluster Gateway; the Gateway then starts the nodes. Status reads "Gateway reports Serving." when the cluster is up.
- Goal Off: it asks the Gateway for a graceful shutdown. The Gateway saves and drains for the **Shutdown grace period** (default 60 seconds, range 0 to 3600, set in **Cluster settings**). Quasar then records clean-shutdown proof in `cluster.json` and tells the Host executor to stop the exact Gateway process.

Start stays disabled ("Wait for stable stopped cluster services before starting") until Quasar has verified a stable stopped state: no pending deployment, restore, update or shutdown, the Host executor reachable, the Gateway process gone and matching clean-shutdown proof. A freshly activated deployment can start without proof because it has never run. The API returns `cluster_start_unavailable` in the same case.

Other controls:

- **Save world** runs the cluster-wide `save-all` command.
- **Restart Cluster Gateway** (Advanced cluster controls, while running) restarts only the Gateway: "Clients will reconnect; nodes keep simulating."
- **Drain** asks the Registry to move a node's work to another node. **Force remove node…** removes a node by its exact epoch ("Unsaved simulation state may be lost.").
- Running clusters do not depend on Quasar. If Quasar stops, the Host executors keep the Gateway and nodes running.

### Machine reboot and logout

At machine shutdown or user logout, the Host executor shuts its clusters down with a save (see [Cluster hosts](ClusterHosts.md#shutdown-and-logout)). After the machine is back, the Gateway comes up in the Down phase. If the goal is still On, the shutdown was clean and the Host executor has restarted since, Quasar starts the cluster again ("The cluster was stopped cleanly outside Quasar, such as at a machine shutdown; starting it again."). Otherwise the status is `cluster_down` and the cluster waits for you.

### Recovery after an unclean shutdown

If the Gateway did not prove a clean shutdown, the cluster needs explicit recovery. With the goal Off, open **Maintenance → Recovery**:

- **Recover missing cluster services** resumes the recorded Gateway and any pending shutdown. It does not erase world state or mark a dirty shutdown as clean.
- **Recover stopped cluster after an unclean shutdown** requires every node to be stopped. It starts a new recovery generation from retained saves and plugin data. Unsaved game changes may be lost.

CLI equivalents: `cluster gateway-recover NAME` and `cluster recover NAME REQUEST.json` with `{"generation":"NEW-UUID"}`.

### Reconciler status codes

| Code | Meaning and action |
| --- | --- |
| `shutdown_pending` | A shutdown is still running. "Waiting for clean Down before starting the next generation." Wait. |
| `shutdown_unverified` | The Gateway is gone but Quasar has no matching clean-shutdown proof. Use Recovery. |
| `gateway_recovery_required` | Gateway process identity does not match the record. Use Recovery. |
| `shutdown_recovery_required` | The shutdown left something dirty or failed with no active nodes. "In Maintenance → Recovery, recover the stopped cluster after an unclean shutdown." |
| `cluster_down` | Goal On but the cluster is Down and Quasar cannot prove a clean restart. Stop and start it, or recover it. |
| `gateway_start_failed` | The Host executor could not start the Gateway. See **Last Cluster Registry process output** on the cluster page. |
| `gateway_api_starting` | "Gateway process is running; waiting for its admin API." |
| `gateway_stop_fencing_unavailable` | The Host executor is too old. Update it ([Cluster hosts](ClusterHosts.md#automatic-updates)). |
| `deployment_pending` | An activation or restore is incomplete. "Complete the managed update or restore before submitting cluster commands." |

## Cluster settings and configuration profile

**Administration → Cluster settings** (needs configuration editing) has:

- **Server name**: the name in Quasar lists.
- **Configuration profile** and **Stage profile changes**: prepares a new deployment on every host machine with the selected profile. Allowed changes are session settings, administrators, mods and plugin selections. Network and dedicated-server root settings cannot change this way ("Cluster network and dedicated-server root settings cannot change through a preserved-world profile update."). If managed Magnetar was updated since the cluster's runtime snapshot was frozen, staging freezes the installed Magnetar together with the plugins it exported, as long as its PluginSdk contract satisfies the cluster release's. The change applies only when you **Apply pending update** in Maintenance. Quasar refuses to apply if the staged profile was edited or deleted after staging; stage it again.
- **Shutdown grace period (seconds)**: used on the next managed shutdown.
- **Save cluster settings**.

Cluster profiles must list administrators as numeric SteamID64 values and have max players of at least 2. Otherwise staging, setup and conversion fail with "Administrator '…' is not a SteamID64; a cluster accepts only numeric Steam IDs." or "Max players is N; a cluster needs at least 2."

Guided setup copies the chosen profile to a new profile named "<Server name> (cluster)", so later edits to the original profile do not affect the cluster.

## Plugins on clusters

Every node (regular nodes, the World Authority, spares and replacements) runs the same common plugin files with the same settings. Quasar builds them once, freezes them with their source commits and copies the frozen set to every host machine. Nodes do not download from the plugin hub or compile on their own.

- The cluster package ships the role plugins `cluster-node` (regular nodes) and `cluster-wa` (World Authority). Quasar does not let you choose them.
- Magnetar's exporter always adds `direct-transport`, pinned to its MagnetarHub entry. A `direct-transport` dev folder is left out of cluster preparation.
- Quasar adds the Quasar Agent (`quasar-agent`) pinned to Quasar's own source commit. This needs a published Quasar build; a build without commit information fails with "The Quasar build has no source commit provenance."
- A server-side companion of a Quasar UI plugin is included only when that UI plugin was installed from QuasarHub. Other local plugins without source metadata are left out, and setup reports "left out, no provenance metadata: <names>".
- Dev folder plugins are compiled from the checkout during preparation, always as a Release build, whatever their **Debug build** setting. A dev folder is identified by its manifest `Id`, or its folder name when the manifest has none.
- Plugins from a registered [plugin hub](Plugins.md#plugin-hubs) are frozen like MagnetarHub plugins, at their manifest's `Commit`. Setup and staging stop with "No plugin hub or dev folder lists the selected plugins ..." when the profile selects an id no hub lists, and with "More than one plugin hub lists ..." when two hubs list it.

### Plugin settings

**Administration → Plugin configuration** shows the normal plugin settings editor for each plugin in the saved preparation. **Save and stage update** prepares a new deployment for all nodes; it does not change running nodes. Apply it from **Maintenance → Pending update → Apply pending update** (full downtime). The individual Agent editor on the Plugins page cannot change cluster-owned configuration. If no editor appears, the page says "No editable plugin configurations are available in the saved preparation. Complete guided setup or prepare an imported deployment first."

New clusters start with each plugin's SDK default values. A converted server keeps the plugin settings the Agent recorded before you stopped it.

### Shared plugin storage

With one host machine, Quasar sets the plugin shared directory (`PluginStorage.GetSharedDirectory`) to `<run root>/plugin-shared` on that Host, and Host backups include it. With several host machines Quasar leaves it unset. A multi-host cluster needs a filesystem mounted at the same path on every Host and an imported deployment specification with `sharedStorageRoot`; Quasar does not create that mount and does not back it up.

## Mods and content updates

The **Mods and plugins** panel on the cluster page tracks changes after deployment:

1. **Check content updates** reads the Steam Workshop `time_updated` timestamp of each mod in the cluster profile and compares the deployed plugin commits with the hub that lists each plugin. Scheduled checks follow the Quasar update check setting (every 15 minutes by default). Repeated manual checks share one run and are limited to one per minute.
2. **Queue mod and plugin updates** saves the observed changes as a pending request.
3. **Maintenance → Stage queued content updates** (needs configuration editing) restages the whole active profile. Quasar downloads each changed mod with its managed SteamCMD, checks the Workshop timestamp before and after the download, checks each queued plugin commit against Magnetar's export, and prepares every host machine. It clears the queue only when every Host prepared successfully.
4. **Apply pending update** stops the cluster, backs it up, activates the new deployment on every host machine and starts it again.

Workshop downloads try anonymous SteamCMD first. Private items need a Steam account in the Steam Workshop settings, otherwise staging fails with "SteamCMD could not download Workshop item <id> anonymously." Unless crossplay is enabled, Quasar adds Magnetar's companion mod (Workshop ID 3750200326) to the pinned mods. Nodes always load mods from the staged copies and never download from the Workshop themselves.

A dev folder plugin shows "Plugin manifest is unavailable or has no exact commit" in this panel, because the monitor only looks at the plugin hub. New commits in a dev folder reach the cluster only when you stage the profile again.

Observations live in `<install root>/ClusterContentUpdates.json`. If that file is damaged, checks pause with "Saved content observations are unavailable. Restore ClusterContentUpdates.json and restart Quasar; checks are paused to preserve baselines."

## Cluster release updates

Quasar checks the latest cluster release on the update interval (15 minutes by default) when at least one cluster has a package selected. A different release lights the update bell, appears on the cluster page and under **Updates → Cluster packages**. Checks only notify.

To update:

1. **Maintenance → Release update → Stage latest release** selects the release, freezes matching dependencies and prepares every host machine from the saved world seed and topology. Queued mod and plugin changes are staged together with it. The cluster keeps running.
2. **Maintenance → Pending update → Apply pending update** starts the full-downtime update: Stopping, Activating, Starting, Complete. Quasar checks every Host first, then stops the cluster, takes a backup under the update ID, activates the new deployment on every Host and starts it.

While an update runs, goal changes, commands, backups, restores and deletion are locked. The only command allowed is an emergency shutdown while the update stops or starts the cluster; it closes the update.

If an update gets stuck, **Abandon update** closes it without rolling anything back. Quasar gives up by itself after 60 minutes in Stopping, 30 minutes of failed attempts in Activating or 60 minutes in Starting, and writes the reason to the update's last error:

- Before any Host switched, the previous version carries on, and Quasar restores the On goal if the cluster was On before.
- After activation, Quasar reverts from the pre-update backup (phase Reverting) and starts the previous version again if it was On. A revert that cannot finish in 3 hours is given up, with the backup named in the error.

**Advanced deployment tools → Roll back binaries and configuration** reuses the update workflow with the previous installation and keeps the current world and plugin data.

## Backups and restore

Cluster backups are native snapshots taken by every Host executor.

- **Back up stopped cluster** makes a clean (`Quiescent`) backup. It needs clean-shutdown proof.
- **Back up running cluster** makes an online (`CrashConsistent`) backup without downtime. Quasar runs `save-all`, the Gateway copies its registry database, then each Host streams its runtime into the snapshot at most at the **Running cluster copy rate** (Backups page, default 32 MiB/s, range 1 to 4096). The cluster package must support online registry copies, otherwise "This cluster's Gateway cannot copy its registry while it runs. Update the cluster to get online backups."
- Scheduled server backups on the **Backups** page cover clusters too: a cleanly stopped cluster gets a `Quiescent` backup, a running one a `CrashConsistent` backup. Retention keeps the newest **Keep last N per server** automatic backups plus the newest `Quiescent` one. A failed scheduled capture is retried after an hour. Manual backups are kept.

To restore, stop the cluster, open **Maintenance → Backups**, pick **Backup to restore**, choose **Prepare restore with fresh credentials**, review the generated request under **Restore prepared backup**, then choose **Restore backup**. Restore always rotates the cluster's runtime credentials. Each Host keeps the replaced runtime data beside the restored one. If the saved profile changed after the backup's deployment, the cluster's profile stays unset and the Cluster settings panel asks you to choose and stage one.

Cluster backups live in `<backup directory>/Clusters/<hash of the Cluster ID>/`. See [Backups](Backups.md) for the backup directory.

## Convert a standalone server to a cluster

Use **Convert to cluster** in the server's **More server actions** menu (page `/servers/<Server ID>/convert-to-cluster`). It needs cluster manage, server editing and configuration editing permissions. Conversion never deletes or restarts the source; the new cluster stays stopped.

Before you start:

- Connect the server's Agent once so Quasar records its plugin settings, then stop the server.
- Register an empty destination cluster with **Register existing cluster** and select its release package and dependency snapshot (**Advanced deployment tools → Select latest release only** and **Freeze selected dependencies**, or the `package-stage`, `package-select` and `dependencies-stage` CLI commands).
- Freezing dependencies this way copies the managed DS, Content and Magnetar plus two prepared folders set in `appsettings.json` as `Quasar:ClusterDependencies:DirectTransportDirectory` and `Quasar:ClusterDependencies:CommonPluginsDirectory`. Build them with `scripts/package-cluster-direct-transport.sh SOURCE_CHECKOUT COMMIT DS64 MAGNETAR_INSTALL OUTPUT_DIRECTORY` and `python3 scripts/package-cluster-plugins.py --plugin MANIFEST CACHE … --output DIR` from the Quasar source. An unset folder fails with "Dependency source DirectTransport is not configured." (or `CommonPlugins`).

The wizard steps:

1. **Review the stopped source**: profile, plugins, when plugin settings were captured, and blocking warnings.
2. **Choose the destination and verified release**: an empty, stopped registered cluster. **Review frozen plugin versions** lists the frozen plugin commits.
3. **Place the cluster**: per placement the Host machine ID, Host executor command URL, credential environment variable names, private IP address, regular nodes (1 to 32). Then the number of hot spares (1 by default). Then the Cluster Gateway and World Authority host machine, game build, public server port, allowed internal networks and two more credential variable names. Credential fields take environment variable names, never secret values.
4. Confirm and choose **Back up and convert**.

Quasar then reserves the source, takes a server backup and a world backup, converts a copy of the world with the release's MagnetarWorld tool, stages the profile's Workshop mods as guided setup does (a profile with mods needs the Steam Web API key), sends the installation, the converted world and the mods to every Host, prepares and activates the stopped cluster.

Conversion is blocked when:

- The profile has a server password, group whitelist, bans or reserved slots: "This cluster package cannot transfer password, group whitelist, bans or reserved slots. Conversion is blocked to preserve access restrictions."
- The online mode is not public: "This cluster package does not transfer non-public online modes."
- The profile has plugins but no recorded settings: "No recorded plugin settings. Connect the source Agent once before stopping the server, then review again."
- A plugin configuration has no clear SDK type (custom providers or conflicting live copies).
- The frozen plugin set does not match the source: "Frozen dependency snapshot does not contain every selected source plugin. Missing: …", "Frozen dependency snapshot contains additional common plugins; align it with the reviewed source selection." or, for a plugin pinned to a 40-character commit, "Frozen plugin commit differs from an explicitly pinned source plugin." `linux-compat`, `dotnet-compat`, `error-reporting` and `quasar-agent` may be present without being selected.
- The source is running: "Stop the source server and wait for its process to exit before converting."

The converter needs at least two regular nodes in total. Regular nodes use backend ports from 28417 and control ports from 29417 on each host machine; the World Authority uses 28700/29700. The converter may reject worlds it does not support.

## Convert a cluster to a standalone server

On the cluster page, open **Maintenance → Convert to a standalone server → Open conversion wizard** (page `/clusters/<Cluster ID>/convert-to-server`).

1. Stop the cluster cleanly. A cluster that has never run since setup cannot be converted: "The cluster has not run since setup, so it has no saved state to convert. Start the cluster once first."
2. Enter **Server ID**, **Server name**, **Public server port** and a base configuration profile.
3. Confirm. Quasar backs up every Host, reassembles the world from those copies (no shared filesystem needed) and creates the standalone server with Goal Off and autostart disabled.

Session settings and mods come from the converted world. Quasar copies the base profile to "<Server name> (converted)" and removes `cluster-node`, `cluster-wa` and `direct-transport`. The cluster's plugin settings are written to `cluster-plugin-configurations.json` in the new server's folder for you to review and reapply; Quasar does not write them into plugin-owned files. Shared plugin records and per-node private files stay in the backup.

### Progress and resume

The conversion page shows the phase and a **Conversion ID**, also in the page URL (`?conversion=<uuid>`). Reopen that URL to resume. **Edit as a new conversion** starts over with a new ID. Work files are in `<backup directory>/Conversions/<uuid>/`. The world converter and large transfers have a 2-hour limit; ordinary Host executor requests time out after 30 seconds.

## Delete or forget a cluster

The delete icon on the card or page opens a dialog:

- **Delete definition** archives the cluster definition. Managed processes must be stopped and all operations complete; Quasar checks each Host executor first. API: `DELETE /api/v1/clusters/{name}` (204, or 409 when blocked).
- **Forget registration without checking remote processes** needs the Cluster ID typed exactly. Running processes keep running. API: `DELETE /api/v1/clusters/{name}/registration?confirmation={name}`.

Both keep worlds, Host data and backups. The definition moves to `Clusters/<Cluster ID>/History/<timestamp>-<guid>-deleted.json`. A removed Cluster ID, in any letter case, cannot be reused: "This cluster ID was removed. Choose a new ID to avoid reusing retained Host state or operation history."

## Register an existing cluster

**Register existing cluster (advanced)** on `/clusters/new` connects an already installed Cluster Registry for monitoring and administration. It does not install anything. Fields: Cluster ID, Server name, **Cluster Registry admin URL** (the Registry's HTTP admin endpoint, for example `http://10.0.0.10:28016`) and **Admin token environment variable name**. Set that variable on the Quasar service to the Registry's admin token, then restart Quasar.

## Files and storage

| Path (under the Quasar install root unless noted) | Content |
| --- | --- |
| `Clusters/<Cluster ID>/cluster.json` | Cluster definition, goal, package and dependency selection, shutdown proof. `Quasar:ClusterCatalogPath` overrides the `Clusters` directory. Do not hand-edit the proof fields. |
| `Clusters/.removed/` | Reserved IDs of removed clusters. |
| `ClusterSetup/<Cluster ID>/` | Guided setup request, status, copied profile and world, plugin export. |
| `Hosts/<Host machine ID>/host.json` | Host machine enrollments. |
| `cluster-credentials.json` | Generated Host and cluster credentials, encrypted with Quasar's Data Protection keys. Back up both together. |
| `ClusterContentUpdates.json` | Mod and plugin observations and queues. |
| `ManagedRuntime/Tools/Cluster/<version>/` | Staged cluster packages. |
| `ManagedRuntime/Tools/ClusterDependencies/<hash>/` | Frozen DS, Content, Magnetar, Direct Transport and common plugin snapshots. |
| `ManagedRuntime/Tools/ClusterModVersions/` | Verified Workshop mod versions. |
| `ManagedRuntime/Tools/.objects` | Shared content store. Identical files across packages, snapshots and mods are stored once and hard-linked. |
| `<backup directory>/Clusters/`, `<backup directory>/Conversions/` | Cluster backups and conversion work folders. |

Quasar removes unused package, dependency and mod content once no cluster, prepared update or retained backup refers to it. There is no age-based retention. Completed setup and conversion delete their temporary installation trees and transfer archives.

## Command line

The packaged `Quasar` launcher has a `cluster` command that calls the Quasar API and prints one JSON envelope on stdout:

```bash
export QUASAR_API_TOKEN='<service-principal token>'
./Quasar cluster list --url https://quasar.example.com
./Quasar cluster goal production off --url https://quasar.example.com --idempotency-key stop-1 --wait
```

Options: `--url` (or `QUASAR_URL`, or local discovery), `--token-env NAME` (default `QUASAR_API_TOKEN`), `--idempotency-key KEY` (required for every change), `--wait`, `--timeout SECONDS` (1 to 3600, default 30), `--wait-timeout SECONDS` (default 900), `--cursor`, `--limit` (1 to 500). Commands that take a JSON file accept `-` for standard input.

| Area | Commands |
| --- | --- |
| Read | `list`, `health`, `status`, `lifecycle`, `plan`, `recovery-readiness`, `config`, `capabilities`, `fleet`, `nodes`, `world-authority`, `snapshots`, `partitions`, `clients`, `bans`, `events`, `chat-history`, `gateway-operations`, `operation NAME ID`, `diagnostics`, `handover-config`, `artifacts`, `artifact NAME ID` |
| Lifecycle | `goal NAME on|off`, `gateway-restart`, `gateway-recover`, `recover NAME REQUEST.json`, `command NAME REQUEST.json` |
| Packages | `package-release`, `package-stage NAME VERSION SHA256`, `package-selection`, `package-select NAME VERSION SHA256 EXPECTED_REVISION`, `dependency-candidate`, `dependencies`, `dependencies-stage NAME FILE`, `deployment-inputs` |
| Deployment | `create FILE`, `deployment-prepare NAME FILE`, `deployment`, `deployment-activate NAME FILE`, `update NAME FILE`, `update-status`, `update-abandon` |
| Backups | `backups`, `backup NAME FILE`, `restore NAME FILE`, `artifact-backup NAME ID` |
| Conversion | `conversion-review NAME SERVER`, `conversion-plugins NAME`, `convert-to-cluster NAME FILE`, `convert-to-server NAME FILE`, `conversion-status NAME UUID` |

`command` accepts `{"action": "..."}` with `save-all`, `shutdown`, `gateway-restart`, `config-set`, `node-close`, `node-kill`, `wa-move`, `kick`, `ban`, `unban`, `chat` or `trigger`. An online backup request is `{"snapshotId":"NEW-UUID","allowCrashConsistent":true,"online":true}`. A rollback is `cluster update NAME` with `{"id":"<new UUID>","rollback":true}`.

Exit codes: `0` success, `2` usage, `3` local configuration, `4` connection or timeout, `5` authentication or authorization, `6` API rejection, `7` the operation completed as failed, `8` incompatible protocol. An HTTP timeout does not mean the operation failed; check it with `cluster operation` or `conversion-status`.

Guided setup has no CLI command; use the UI or `POST /api/v1/clusters/setup`.

## API

Cluster routes are under `/api/v1/clusters`. Changes need an `Idempotency-Key` header and return a durable operation; repeating the same key and body returns the same operation, a different body returns `idempotency_key_conflict`. Operations are stored under `<install root>/Operations/Clusters`. An operation still running when Quasar stopped is reported as Failed with `interrupted_by_restart`; resubmit with a new key.

## Common problems

- **Start stays grey.** Quasar has not verified a stable stopped state. Check the cluster overview status code (see the table above), make sure every Host executor is connected on the Hosts page, and finish any pending update or restore.
- **"Cannot reach the Cluster Registry at …"** The Gateway is not running or its admin port is not reachable from Quasar. While the cluster is stopped this is expected.
- **"Cluster Registry credential environment variable '…' is not set."** For a registered cluster, set that variable on the Quasar service and restart Quasar.
- **"Queued mod/plugin updates must be staged before applying."** Stage the queued content or remove the queue.
- **"This stopped cluster has no clean shutdown proof."** Use **Recover stopped cluster after an unclean shutdown** before updating.
- **"Resolve Workshop dependencies before staging: …"** A mod dependency could not be resolved; fix the profile's mod list.
- **"Magnetar's plugin exporter and the selected dependency snapshot use different PluginSdk builds."** Managed Magnetar was updated after the snapshot was frozen, and the cluster release or the new Magnetar declares no PluginSdk contract, or the new Magnetar's contract doesn't fit the release. In the last case the error names both contracts and says whether a newer Magnetar or another cluster release is needed. Otherwise stage a matching cluster release.
- **"Select the release and dependencies used by the active deployment before conversion."** Reselect the active package and dependencies before converting a cluster back.
- **`cluster-credentials.json` damaged.** Quasar moves an unreadable `cluster-credentials.json` to `cluster-credentials.json.corrupt-<timestamp>` and starts without it. Restore it from a backup together with the Data Protection keys, or re-create the clusters and Host enrollments.

## Sources
- `Docs/ClusterTerminology.md`
- `Docs/ClusterConversion.md`
- `Docs/ClusterPlugins.md`
- `Docs/ClusterStorage.md`
- `Docs/Configuration.md`
- `Docs/Phase4IntegrationPlan.md`
- `Quasar/Components/Pages/ClusterCreate.razor`
- `Quasar/Components/Pages/ClusterDetail.razor`
- `Quasar/Components/Pages/ClusterConsoleDialog.razor`
- `Quasar/Components/Pages/ClusterConversion.razor`
- `Quasar/Components/Pages/ClusterRemoveDialog.razor`
- `Quasar/Components/Pages/Servers.razor`
- `Quasar/Components/Pages/Home.razor`
- `Quasar/Components/Pages/Backup.razor`
- `Quasar/Components/Dashboard/ClusterAdminPanel.razor`
- `Quasar/Components/Dashboard/ClusterCard.razor`
- `Quasar/Components/Dashboard/ClusterContentPanel.razor`
- `Quasar/Components/Dashboard/ClusterDeleteButton.razor`
- `Quasar/Components/Dashboard/ClusterDeploymentPanel.razor`
- `Quasar/Components/Dashboard/ClusterDetailPanel.razor`
- `Quasar/Components/Dashboard/ClusterFleetPanel.razor`
- `Quasar/Components/Dashboard/ClusterPluginConfigurationPanel.razor`
- `Quasar/Components/Layout/NavMenu.razor`
- `Quasar/Models/ClusterDefinition.cs`
- `Quasar/Models/QuasarBackupSettings.cs`
- `Quasar/Program.cs`
- `Quasar/Services/ClusterApi.cs`
- `Quasar/Services/ClusterBackupService.cs`
- `Quasar/Services/ClusterBetaNoticeCatalog.cs`
- `Quasar/Services/ClusterCatalog.cs`
- `Quasar/Services/ClusterCommandService.cs`
- `Quasar/Services/ClusterContentMonitor.cs`
- `Quasar/Services/ClusterConversionService.cs`
- `Quasar/Services/ClusterCredentialStore.cs`
- `Quasar/Services/ClusterDependencyService.cs`
- `Quasar/Services/ClusterGatewayClient.cs`
- `Quasar/Services/ClusterPackageService.cs`
- `Quasar/Services/ClusterReconciler.cs`
- `Quasar/Services/ClusterReleaseMonitor.cs`
- `Quasar/Services/ClusterSetupService.cs`
- `Quasar/Services/ClusterStorageMaintenance.cs`
- `Quasar/Services/ClusterUpdatePreparationService.cs`
- `Quasar/Services/ClusterUpdateService.cs`
- `Quasar/Services/ClusterWorkshopModStager.cs`
- `Quasar/Services/ClusterWorldConverter.cs`
- `Quasar/Services/QuasarDevFolderCatalog.cs`
- `Quasar/Services/Updates/QuasarUpdateOptions.cs`
- `Quasar.Bootstrap/ClusterCli.cs`
- `scripts/package-cluster-direct-transport.sh`
- `scripts/package-cluster-plugins.py`
- `Shared/ClusterDeployment/ClusterContentStore.cs`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
