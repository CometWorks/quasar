# Clusters

A Quasar cluster runs one Space Engineers 1 world across several Magnetar server processes (nodes) on one or more Linux machines, with one public server port for players. This page covers the cluster terms, the cluster pages, start/stop, plugins, mods, updates, backups, conversion to and from a standalone server, storage and the `Quasar cluster` command line. Creating a cluster is in [Guided cluster setup](ClusterSetup.md), the per-machine service in [Cluster hosts](ClusterHosts.md).

Managed clusters are in beta. The setup, conversion and cluster pages show "Managed clusters are still in beta. Review your backups before changing worlds, mods, or plugins." Each signed-in user can dismiss it with the outlined close button at the right edge; Quasar remembers the dismissal per account in `<install root>/cluster-beta-dismissals.json`.

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

## Client diagnostics and rotation publication

The optional Pulsar Cluster Diagnostics client opens from F11. Cluster nodes send its
allowlisted snapshot through secure game mod messages. In-game Admin and Owner receive
fleet detail; Moderator and Space Master receive player mode. Quasar roles do not grant
in-game privileges.

Settings, under Advanced cluster controls, has **Client diagnostics and rotation
publication**. Load the publication to inspect source, revision and expiry. Publishing
requires cluster-manage permission and the Registry capability. It replaces the full
fleet assignment list with `{node, epoch, dueAt}` rows and a lease of at most five minutes.
The Manager renews every 30 seconds. An expired or changed incarnation loses its countdown.
Publication reports rotation eligibility and does not rotate a node. See
[Client diagnostics](https://github.com/CometWorks/quasar/blob/main/Docs/ClientDiagnostics.md).

## Requirements and permissions

- Cluster package staging, dependency provisioning and guided setup need Quasar on Linux x64. On other platforms Quasar reports "Cluster release staging requires Linux." or "Guided cluster setup currently requires Linux x64."
- Cluster packages come from GitHub releases of `CometWorks/cluster` (asset `ClusterForLinux-<version>.tar.gz` plus `SHA256SUMS`). Only published, non-prerelease releases can be staged. Public releases are checked without a token. An optional token in **Updates → GitHub token** raises rate limits; private releases require repository read access.
- Roles: Viewer can open cluster pages and the console (cluster query). Editor and Admin can start, stop and change clusters (cluster manage). Guided setup, profile staging and conversion also need configuration editing (Editor or Admin). Enrolling host machines needs security administration (Admin only). See [Access control](AccessControl.md).
- Service principals with the `cluster.query` or `cluster.manage` scope can use the cluster API and CLI, limited to the cluster IDs in their `Clusters` list. See [Access control](AccessControl.md).

## Cluster pages

- **New server → Cluster** on the Dashboard and Servers pages opens `/clusters/new` (page title "Set up cluster"). See [Guided cluster setup](ClusterSetup.md).
- Clusters appear next to standalone servers on the Dashboard, in cards and list view. A cluster card shows the Server name, Cluster ID, Gateway URL, **Desired state**, Start/Stop and Save buttons, the console button and **Manage cluster**.
- **Hosts** (nav group "Infrastructure & tools", `/hosts`, page title "Host machines") lists Host executors and their reachability. See [Cluster hosts](ClusterHosts.md).

The cluster page is `/clusters/<Cluster ID>`. It opens on **Overview** and groups controls into six views. Start / Stop, **Save world** (while running), the console and the current operation result stay available across views.

| View | What it holds |
| --- | --- |
| Overview | Status, Capacity, Players, Crash recovery readiness and a live map of Gateway, game nodes, hot spares and World Authority. Service addresses and expandable world/configuration/runtime details, including the last failed Gateway output. |
| Nodes | Slots, registered nodes and performance. The **Drain** action, plugin statistics and recent plugin logs. |
| Events | Cluster Registry event history, with sequence, time, actor and target. |
| Mods & plugins | Content update checks and queues, with an active deployment. |
| Maintenance | Deployed/latest versions, release staging, pending updates, **Backups**, **Recovery**, **Convert to a standalone server**, **Advanced deployment tools**. |
| Settings | Server name, profile, hot spares, shutdown grace period, plugin configuration, advanced controls and cluster removal. Existing confirmations and permissions still apply. |

Live and historical cluster players appear in the top-level [Players page](Monitoring.md#players), grouped under one server per cluster. Node moves do not create duplicate player entries. Kick, Ban and Unban apply to the cluster through the Gateway.

A resume banner appears when there is no active deployment: **Resume guided setup** if a setup exists, otherwise **Set up a new cluster**. Views have bookmarkable fragments. `#cluster-nodes` opens Nodes; `#cluster-events` opens Events; existing `#cluster-content` links open Mods & plugins; `#cluster-configuration` opens Maintenance; `#cluster-settings` opens Settings.

The map shows connected players, routed players per node, hosted partitions, simulation speed, RAM in GB and process CPU. Hot spares are marked. Nodes show their host name when the cluster has at least two configured hosts. Configured empty slots remain visible. Solid links show player routing; dashed links show partition synchronization. Internal node links show Registry lease status. Counts describe player routing and partition ownership, not transport socket counts, which the current Registry contract does not expose.

Empty regular nodes and hot spares show **Idle** and **Waiting for work** when their live agent reports paused simulation telemetry.
Their CPU bars and raw sim-speed reading are hidden, with **CPU chart paused
while idle** in their place. RAM stays visible, and fresh background CPU/RAM
usage still contributes to resource totals. The **Nodes** table also marks them
idle. Readings return when work resumes. Low sim speed alone does not mean idle;
active slow nodes and World Authority keep their normal diagnostics. Missing or
stale telemetry does not establish an idle state.

CPU 100% means one logical CPU. Four bars each hold one core equivalent; they do not measure individual cores. The numeric total can exceed 400%. Totals sum available resource readings and state their coverage. Missing or stale readings are excluded and unavailable player/partition counts show a dash. Gateway resources need an updated Quasar.Host; older Hosts remain compatible but cannot supply them. Gateway CPU needs two samples of the same process, and Gateway readings older than 15 seconds are excluded. Node resources use the existing incarnation-matched agent telemetry and freshness checks.

Connected players and per-node routes come from separate Registry observations.
When assignments are missing, the map says routing data is incomplete. A dash is
unknown; `1+` means at least one confirmed routed player. Counts and link widths
update when assignments appear or change, and clear on disconnect. A player who
is already in-world but stays unassigned can indicate an older cluster package
that exports the connection's cached join route instead of its current placement.
That needs a cluster package fix; a Quasar UI update cannot manufacture the route.

**Service addresses** lists Steam and optional additional player ports, internal cluster connections and node management ports, Registry administration and Host executor management. It uses the active deployment's specification, not a staged update, plus current registered endpoints. Wildcard player binds use the enrolled host address when known and also show the original bind. These are configured addresses, not proof that a port is listening or reachable through a firewall. Older deployments without a recorded specification show the known endpoints only. See [Cluster health overview](https://github.com/CometWorks/Quasar/blob/main/Docs/ClusterOverview.md).

The cluster page refreshes status every five seconds. When a managed cluster is stopped, Quasar does not show Cluster Registry connection failures as warnings.

For history, open **Analytics** and select the cluster once under **Servers and
clusters**. Each metric has one graph with colored, named node series, including
World Authority. New slots join the selection automatically, and replacements
continue the same slot history. Shared player and world readings stay per node
instead of being added into a cluster total. See [Analytics](Monitoring.md#analytics).

**Entities** also selects the whole cluster by name. It combines live entities
from every responding active node, merges shared copies and routes viewer
buttons to the entity's node. Partial coverage is marked. Cluster deletion needs
updated agents and one verified owner; shared or moving entities cannot be
deleted from an arbitrary node. See [Entities](Monitoring.md#entities).

Overview, Maintenance and Settings stay available while the Gateway admin connection is being checked or is unavailable. Recovery, backups and local settings use the same tabs online and offline. Nodes, Events and Mods & plugins are greyed out until the authenticated capabilities endpoint responds, regardless of desired On/Off state. A selected live-only view falls back to Overview during an outage; reconnect restores its requested view and bookmark. Opened panels retain their state. The five-second poll checks availability again. Every action retains its permissions, confirmations and safety checks.

The **Crash recovery readiness** metric reports whether the Registry can reconstruct
saved cluster state after a crash. Its status comes from the recovery-readiness
endpoint. The labels are **Ready to recover**, **Recovery at risk**, **Complete
recovery unavailable** and **Recovery status unavailable**. A recovery point is a
complete set of saved world data that can be restored together. With no reported
point, the panel explains that recovery after a crash is not confirmed; this does
not mean no files have been saved. A failed check says recovery information is
unavailable. Progress since the last save can be lost even when recovery is ready.

### Offline partitions and compensation

Open **Nodes & players → Partitions** to inspect Registry placement, offline Final holders, profile readiness, confidence, unknown reasons and remote users. Refresh and Inspect only read state. Remote users pin ordinary offlining and clientless movement; a `remote-use` conflict means another node is using the partition remotely.

The authenticated Administrator role is required for **Profile**, **Dry run (prediction only)** and per-key cache clear. Viewer and Editor can inspect. Profile deliberately advances profiling on the selected online owner. Dry run reads the exact offline Final from an advertised holder for a finite interval of 0-168 game hours. It does not wake, claim or save the partition. A changed Final or unavailable holder needs refresh; a legacy Final may have no profile.

Results keep source confidence (**Exact**, **Approximate**, **Frozen**, **Creative**), skipped/content-mismatch reasons, modeled interval and cap. Confidence does not claim native game verification. Tables show stored energy and per-block electricity in MWh, fuel and net inventory definition quantities, production recipe units and zone-chip counts. Production and player safe zones share the source model's power budget and producer costs. Zone rows report bubble presence, activation, chip exhaustion and paid-credit timers. Power loss pauses paid credit; recovery requires vanilla activation; chip exhaustion removes the bubble and preserves the block switch/settings. Admin-created zones follow separate rules.

Expand **Shared production rate cache** for measured/formula rates per second, samples, spread, scripted status and key epochs. **Clear this key** submits the displayed epoch. `epoch_mismatch` means the key changed; review the refreshed cache before another deliberate clear. Running operations remain visible. A clear does not measure the group again.

Older cluster packages without the `offlinePartitions=v1` capability keep ordinary cluster controls but show offline administration as unsupported. Missing diagnostics show unavailable, never invented zero costs. Quasar uses the existing authenticated Gateway transport and never exposes node credentials. See [Offline partitions and compensation](https://github.com/CometWorks/quasar/blob/main/Docs/ClusterOfflineCompensation.md) for units, API routes and test limits.

### Cluster console

On wide screens, Registry event details and their JSON payload share a row to use
the console width. On narrow screens they stack; payloads wrap without clipping.

The terminal icon on the card or page opens the cluster console. It has two tabs, **Cluster Registry events** and **Node plugin logs** (up to 1000 most recent entries), and refreshes every five seconds. If the Registry cannot be reached it keeps the shown entries and says "Live feed unavailable. Retained entries may be stale." Full Gateway and node process logs stay on the host machines; see [Cluster hosts](ClusterHosts.md#logs).

## Starting and stopping

**Start** sets the cluster goal to On. **Stop** asks "Shut down cluster?" ("Players will disconnect while the cluster saves and drains."), then sets the goal to Off. A background reconciler in Quasar does the work every 2 seconds:

- Goal On: it asks the Host executor on the Gateway host machine to start the Cluster Gateway; the Gateway then starts the nodes. Status reads "Gateway reports Serving." when the cluster is up.
- Goal Off: it asks the Gateway for a graceful shutdown. The Gateway saves and drains for the **Shutdown grace period** (default 60 seconds, range 0 to 3600, set in **Cluster settings**). Quasar then records clean-shutdown proof in `cluster.json` and tells the Host executor to stop the exact Gateway process.

Start stays disabled ("Wait for stable stopped cluster services before starting") until Quasar has verified a stable stopped state: no pending deployment, restore, update or shutdown, the Host executor reachable, the Gateway process gone and matching clean-shutdown proof. A freshly activated deployment can start without proof because it has never run. The API returns `cluster_start_unavailable` in the same case.

Other controls:

- **Save world** runs the cluster-wide `save-all` command. It is disabled during another operation, when the Gateway admin connection is unavailable, or when the Gateway credential lacks management scope or save support. The tooltip explains why. The same checks apply to both button styles on every tab, including Maintenance, and the click handler checks again before submitting.
- **Restart Cluster Gateway** (Advanced cluster controls, while running) restarts only the Gateway: "Clients will reconnect; nodes keep simulating."
- **Drain** asks the Registry to move a node's work to another node. The web UI has no force-remove action. If a process needs to be killed manually, use operating-system tools on its host machine.
- Running clusters do not depend on Quasar. If Quasar stops, the Host executors keep the Gateway and nodes running.

The admin power dialog's **Save and stop all servers** includes managed clusters.
It sets their goals to Off and shows a loader with elapsed time and per-target
progress. It waits for the regular save/drain shutdown and the Host's fenced
Gateway stop confirmation. A failed or dirty cluster is reported, not
force-killed; Quasar remains running. Observed-only clusters are excluded.

### Machine reboot and logout

At machine shutdown or user logout, the Host executor shuts its clusters down with a save (see [Cluster hosts](ClusterHosts.md#shutdown-and-logout)). After the machine is back, the Gateway comes up in the Down phase. If the goal is still On, the shutdown was clean and the Host executor has restarted since, Quasar starts the cluster again ("The cluster was stopped cleanly outside Quasar, such as at a machine shutdown; starting it again."). Otherwise the status is `cluster_down` and the cluster waits for you.

### Recovery after an unclean shutdown

If the Gateway did not prove a clean shutdown, the cluster needs explicit recovery. With the goal Off, open **Maintenance → Recovery**, which remains available if the Gateway is unreachable:

- **Recover missing cluster services** resumes the recorded Gateway and any pending shutdown. It does not erase world state or mark a dirty shutdown as clean.
- **Recover stopped cluster after an unclean shutdown** requires every node to be stopped. It starts a new recovery generation from retained saves and plugin data. Unsaved game changes may be lost.

CLI equivalents: `cluster gateway-recover NAME` and `cluster recover NAME REQUEST.json` with `{"generation":"NEW-UUID"}`.

#### Update despite the recorded dirty shutdown

Recovery restarts the same build. If every stop of that build ends dirty, for example because a node crashes while it drains, the cluster could never take the update that fixes it. For that case, with a pending update prepared from the selected release, use **Maintenance → Pending update → Update despite the recorded dirty shutdown**:

1. Quasar asks the stopped Gateway what its last shutdown left unsaved (the Dirty list of its CleanShutdown snapshot: `node:<id>`, `partition:<id>`, `global` or voxel entries) and shows it. Accept it to continue.
2. Stopping proves every node stopped and stops the Gateway fenced, as Recovery does, then takes the pre-update backup. That backup is crash-consistent.
3. Quasar activates the update and starts it as a recovery start that may also change the revision and the node list (`CLUSTER_START_RECOVERY=update`). The listed parts come back from their newest saves; their unsaved changes are lost. Everything else had been saved.

It needs the stopped Gateway to still answer in phase Down, and a cluster release that declares `managedRecoveryUpdate: 1`. It refuses a rollback (restore a backup instead) and refuses when the Gateway lists something else than what was accepted. API callers send the list in `acceptDirtyShutdown` of the update request.

### Automatic cluster control

In **Runtime details**, **Automatic cluster control** means Quasar's background
check that the cluster is running or stopped as requested. It shows **Waiting for
first check**, **Monitoring only**, **Setup needed**, **Applying requested state**,
**Requested state reached** or **Needs attention**, followed by the current reason.
The API calls this component the reconciler.

**Host management service** means `Quasar.Host`, which starts, stops and monitors
processes on the host machine. Its count shows configured clusters, not nodes.
**Saved coordination state** shows the Cluster Registry snapshot and logged
updates, separate from world backups. Hover or focus these labels for explanations.

### Automatic cluster control status codes

| Code | Meaning and action |
| --- | --- |
| `shutdown_pending` | A shutdown is still running. "Waiting for clean Down before starting the next generation." Wait. |
| `shutdown_unverified` | The Gateway is gone but Quasar has no matching clean-shutdown proof, also after goal Off stopped the respawn of a Gateway that did not stay up (three launches in a row, no node running). Use Recovery. |
| `gateway_recovery_required` | Gateway process identity does not match the record. Use Recovery. |
| `shutdown_recovery_required` | The shutdown left something dirty or failed with no active nodes. "In Maintenance → Recovery, recover the stopped cluster after an unclean shutdown." |
| `cluster_down` | Goal On but the cluster is Down and Quasar cannot prove a clean restart. Stop and start it, or recover it. |
| `gateway_start_failed` | The Host executor could not start the Gateway. See **Last Cluster Registry process output** on the cluster page. |
| `gateway_api_starting` | "Gateway process is running; waiting for its admin API." |
| `gateway_stop_fencing_unavailable` | The Host executor is too old. Update it ([Cluster hosts](ClusterHosts.md#automatic-updates)). |
| `deployment_pending` | An activation or restore is incomplete. "Complete the managed update or restore before submitting cluster commands." |

## Cluster settings and configuration profile

**Settings → Cluster settings** (needs configuration editing) has:

- **Server name**: the name in Quasar lists.
- **Configuration profile** and **Stage profile changes**: prepares a new deployment on every host machine with the selected profile. Allowed changes are session settings, administrators, mods and plugin selections. Network and dedicated-server root settings cannot change this way ("Cluster network and dedicated-server root settings cannot change through a preserved-world profile update."). If managed Magnetar was updated since the cluster's runtime snapshot was frozen, staging freezes the installed Magnetar together with the plugins it exported, as long as its PluginSdk contract satisfies the cluster release's. The change applies only when you **Apply pending update** in Maintenance. Quasar refuses to apply if the staged profile was edited or deleted after staging; stage it again.
- **Hot spares** and **Stage hot spare change**: changes how many hot spares the cluster has. A spare is an extra node, so this prepares a new deployment on every host machine that adds or removes spare nodes; nothing else changes. New spares go where guided setup would put them, from the Gateway host machine on, at most 32 nodes per machine, and a removed spare is the last node of its machine. The change applies only when you **Apply pending update** in Maintenance, which stops the cluster cleanly, activates the new node list and starts it again. Adding spares needs a cluster release that supports them ("This cluster release does not support hot spares..."). A cluster whose nodes Quasar's setup did not lay out cannot change its spares here ("This cluster's nodes were not laid out by Quasar's setup...").
- **Shutdown grace period (seconds)**: used on the next managed shutdown.
- **Save cluster settings**.

Cluster profiles must list administrators as numeric SteamID64 values and have max players of at least 2. Otherwise staging, setup and conversion fail with "Administrator '…' is not a SteamID64; a cluster accepts only numeric Steam IDs." or "Max players is N; a cluster needs at least 2."

Guided setup copies the chosen profile to a new profile named "<Server name> (cluster)", so later edits to the original profile do not affect the cluster.

### Bugfixes and Performance by default

These defaults protect against known game crashes and reduce CPU overhead and
simulation pauses. Node crashes or prolonged stalls can interrupt players, trigger
partition recovery and lose unsaved progress. Guided setup explains the risks and
asks for confirmation before skipping missing defaults. Existing profile plugins
stay selected. Keep both plugins enabled for production clusters.

New clusters get the MagnetarHub server plugins Bugfixes and Performance with their default settings, unless the operator unticks **Enable the Bugfixes and Performance server plugins** in guided setup (see [Guided cluster setup](ClusterSetup.md#create-the-cluster)). Quasar records in the profile that it offered them (`offeredDefaultPlugins` in `profile.json`), whether they were taken or not, and never adds them to that profile again: removing either one from the cluster's profile sticks.

Quasar does not add them to existing clusters by itself. When the profile selected under **Cluster settings** lacks one of them and was never offered them, a notice offers **Add to profile**, which saves the profile with the missing plugins (then stage the profile changes and apply the update as usual), and **Don't offer again**. Other servers and clusters that use the same profile get the added plugins at their next restart or profile update.

## Plugins on clusters

Every node (regular nodes, the World Authority, spares and replacements) runs the same common plugin files with the same settings. Quasar builds them once, freezes them with their source commits and copies the frozen set to every host machine. Nodes do not download from the plugin hub or compile on their own.

- The cluster package ships the role plugins `cluster-node` (regular nodes) and `cluster-wa` (World Authority). Quasar does not let you choose them.
- Magnetar's exporter always adds `direct-transport`, pinned to its MagnetarHub entry. A `direct-transport` dev folder is left out of cluster preparation.
- Quasar adds the Quasar Agent (`quasar-agent`) pinned to Quasar's own source commit. This needs a published Quasar build; a build without commit information fails with "The Quasar build has no source commit provenance."
- A server-side companion of a Quasar UI plugin is included only when that UI plugin was installed from QuasarHub. Other local plugins without source metadata are left out, and setup reports "left out, no provenance metadata: <names>".
- Dev folder plugins are compiled from the checkout during preparation, always as a Release build, whatever their **Debug build** setting. A dev folder is identified by its manifest `Id`, or its folder name when the manifest has none.
- A dev folder with the id of a hub plugin is a dev override and replaces the hub build, also for the core plugins Magnetar loads by itself (`linux-compat`, `dotnet-compat`). Setup and staging progress show "dev overrides, not the hub pins: <id> <commit> from <folder>", and Quasar logs the same as a warning. See [Dev overrides](Plugins.md#dev-overrides-of-hub-plugins).
- Plugins from a registered [plugin hub](Plugins.md#plugin-hubs) are frozen like MagnetarHub plugins, at their manifest's `Commit`. Setup and staging stop with "No plugin hub or dev folder lists the selected plugins ..." when the profile selects an id no hub lists, and with "More than one plugin hub lists ..." when two hubs list it.

### Plugin settings

**Settings → Plugin configuration** shows the normal plugin settings editor for each plugin in the saved preparation. **Save and stage update** prepares a new deployment for all nodes; it does not change running nodes. Apply it from **Maintenance → Pending update → Apply pending update** (full downtime). The individual Agent editor on the Plugins page cannot change cluster-owned configuration. Generated plugin sections use padded bodies; advanced controls keep spacing between the restart action and nested panels. If no editor appears, the page says "No editable plugin configurations are available in the saved preparation. Complete guided setup or prepare an imported deployment first."

New clusters start with each plugin's SDK default values. A converted server keeps the plugin settings the Agent recorded before you stopped it.

### Shared plugin storage

With one host machine, Quasar sets the plugin shared directory (`PluginStorage.GetSharedDirectory`) to `<run root>/plugin-shared` on that Host, and Host backups include it. With several host machines Quasar leaves it unset. A multi-host cluster needs a filesystem mounted at the same path on every Host and an imported deployment specification with `sharedStorageRoot`; Quasar does not create that mount and does not back it up.

## Mods and content updates

The **Mods and plugins** panel on the cluster page tracks changes after deployment:

1. **Check content updates** reads the Steam Workshop `time_updated` timestamp of each mod in the cluster profile and compares the deployed plugin commits with the hub that lists each plugin. Scheduled checks follow the Quasar update check setting (every 15 minutes by default). Repeated manual checks share one run and are limited to one per minute.
2. **Queue mod and plugin updates** saves the observed changes as a pending request.
3. **Maintenance → Stage queued content updates** (needs configuration editing) restages the whole active profile. Quasar downloads each changed mod with its managed SteamCMD, checks the Workshop timestamp before and after the download, checks each queued plugin commit against Magnetar's export, and prepares every host machine. It clears the queue only when every Host prepared successfully.
4. **Apply pending update** stops the cluster, backs it up, activates the new deployment on every host machine and starts it again.

Workshop downloads try anonymous SteamCMD first. Private items need a Steam account in the Steam Workshop settings, otherwise staging fails with "SteamCMD could not download Workshop item <id> anonymously." Quasar always adds Magnetar's companion mod (Workshop ID 3750200326) to the pinned mods, also for a crossplay profile: Magnetar adds it to every node, and the node configuration the cluster writes never enables crossplay. Nodes always load mods from the staged copies and never download from the Workshop themselves.

A plugin that a registered dev folder overrides shows "Dev override: staging builds this plugin from the dev folder <folder>, not from its hub, so hub commits are not observed." in this panel, and its hub commits are never offered as an update. New commits in a dev folder reach the cluster only when you stage the profile again; removing the dev folder and staging again returns the cluster to the hub build.

Observations live in `<install root>/ClusterContentUpdates.json`. If that file is damaged, checks pause with "Saved content observations are unavailable. Restore ClusterContentUpdates.json and restart Quasar; checks are paused to preserve baselines."

### Mod configuration files (world Storage)

Some mods read their settings from the world's `Storage` folder, for example Modular Encounters Systems from `Storage/1521905890.sbm_ModularEncountersSystems/Config-General.xml`. Do not edit those files in Quasar's state folders: every node, the World Authority and the spare have their own copy of the world. Stage them instead:

1. Put the files in a folder laid out like the world's `Storage` folder, one subfolder per mod (`<folder>/1521905890.sbm_ModularEncountersSystems/Config-General.xml`).
2. Run `./Quasar cluster world-files-prepare NAME <folder> --idempotency-key KEY` (API `POST /api/v1/clusters/{name}/world-files-preparation`, needs cluster manage and configuration editing). Quasar adds or replaces those files in the cluster's world seed, sends each host machine only the changed files and prepares a new deployment. The cluster keeps running.
3. **Apply pending update** (or `cluster update`) stops the cluster cleanly, backs it up, activates the deployment and starts it again. On activation each host machine copies the changed files into the World Authority's and every node's world, spares included.

Later updates (profile, release, hot spares, plugin settings) keep the new world seed, so they keep the files and copy nothing again. Files a mod rewrites on the World Authority stay as the mod wrote them until you stage that file again. `./Quasar cluster world-files NAME` lists the `Storage/` files of the seed with their SHA-256. Only files under `Storage/<mod folder>/` are accepted, at most 1000 files and 16 MiB per staging; files are added or replaced, never deleted. Every host machine needs a Host executor with this feature; an older one is refused with "Host … cannot update world files: update its Host executor first."

The World Authority's copy of the mod Storage is the cluster's copy. Every time a node process starts, its host machine replaces the node's world `Storage` folder (in `premade-world` and `Saves/<world>`) with the World Authority's (on a node's first start too, so a new node or spare reads the World Authority's mod configuration from its first session on), and the node's own `Storage` folder (the mods' local and global storage) with the World Authority's. What a mod writes on a regular node therefore lasts only until that node restarts. The World Authority's world `Storage` is its `premade-world` copy with the files it saved while running on top, so its own writes survive its restart too. Only a host machine that runs the World Authority can do this; nodes on the other host machines keep the prepared seed and the files their mods write.

## Cluster release updates

Quasar checks the latest cluster release on the update interval (15 minutes by default) when at least one cluster has a package selected. A different release lights the update bell, appears on the cluster page and under **Updates → Cluster packages**. Checks only notify.

To update:

1. **Maintenance → Release update → Stage latest release** selects the release, freezes matching dependencies and prepares every host machine from the saved world seed and topology. Queued mod and plugin changes are staged together with it. The cluster keeps running. The same step without the web UI is `./Quasar cluster release-update-prepare NAME --idempotency-key KEY` (API `POST /api/v1/clusters/{name}/release-update-preparation`, needs cluster manage and configuration editing). Like the button, it reuses the current dependency snapshot's DS, Magnetar, Direct Transport and common plugins when they fit the new release, so a cluster from guided setup needs no configured dependency sources. `package-select` on its own clears the dependency snapshot.
2. **Maintenance → Pending update → Apply pending update** starts the full-downtime update: Stopping, Activating, Starting, Complete. Quasar checks every Host first, then stops the cluster, takes a backup under the update ID, activates the new deployment on every Host and starts it.

While an update runs, goal changes, commands, backups, restores, deletion and staging are locked. The only command allowed is an emergency shutdown while the update stops or starts the cluster; it closes the update.

If an update gets stuck, **Abandon update** (`./Quasar cluster update-abandon NAME --idempotency-key KEY`) closes it without rolling anything back. A refused staging (`profile_preparation_conflict`, or the same for hot spares, names and other stagings) names the update, what it waits for and this way out. Quasar gives up by itself after 60 minutes in Stopping, 30 minutes of failed attempts in Activating or 60 minutes in Starting, and writes the reason to the update's last error.

If nodes keep failing at startup, for example because a plugin throws in its preload hook, the update's last error says so as soon as the Gateway stops respawning them: which nodes, the exception they reported and where the node log is on the Host. When all of them fail, Quasar gives up 12 minutes after Starting began, once the Gateway's retry has failed too, and reverts from the pre-update backup. Then stage the fixed build and apply it as usual.

To stage a fixed build sooner, abandon the update. Applying the fixed build then stops in Stopping, because a Gateway whose World Authority never ran lists `global` as unsaved, and the update's last error says so at once. Abandon that update too and apply the build with **Update despite the recorded dirty shutdown**. Nothing is lost: the failed deployment never ran, and the world comes from its newest catalogued saves.

When Quasar gives up:

- Before any Host switched, the previous version carries on, and Quasar restores the On goal if the cluster was On before.
- After activation, Quasar reverts from the pre-update backup (phase Reverting) and starts the previous version again if it was On. A revert that cannot finish in 3 hours is given up, with the backup named in the error.

**Advanced deployment tools → Roll back binaries and configuration** reuses the update workflow with the previous installation and keeps the current world and plugin data.

Every staging step (release update, hot spares, plugin settings, profile) builds on the saved preparation only while it belongs to the running deployment or to the change Quasar staged on top of it. A restore that was prepared but failed, a rollback, or a package selection that discarded a staged change leaves an older saved preparation. Staging then builds on the running deployment's own preparation instead and says so, naming any Steam or Direct Transport address that would otherwise have changed back. A package selection also discards a staged profile: the cluster keeps its active profile until you stage the other one again. A profile staging that failed or was refused ("The selected profile changed during preparation") discards nothing: stage again and the earlier staged changes are still there. A candidate from a refused staging can't be applied until then.

## Backups and restore

Cluster backups are native snapshots taken by every Host executor.

- **Back up stopped cluster** makes a clean (`Quiescent`) backup. It needs clean-shutdown proof.
- **Back up running cluster** makes an online (`CrashConsistent`) backup without downtime. Quasar runs `save-all`, the Gateway copies its registry database, then each Host streams its runtime into the snapshot at most at the **Running cluster copy rate** (Backups page, default 32 MiB/s, range 1 to 4096). The cluster package must support online registry copies, otherwise "This cluster's Gateway cannot copy its registry while it runs. Update the cluster to get online backups."
- Scheduled server backups on the **Backups** page cover clusters too: a cleanly stopped cluster gets a `Quiescent` backup, a running one a `CrashConsistent` backup. Retention keeps the newest **Keep last N per server** automatic backups plus the newest `Quiescent` one. A failed scheduled capture is retried after an hour. Manual backups are kept while the cluster exists.
- The pre-update backup of each managed update and the backup a conversion takes are kept per purpose: Quasar keeps the newest 3 pre-update and the newest 3 pre-conversion backups of a cluster and deletes older ones of that kind. Older backups that don't record a purpose are kept.
- A backup leaves out each node's mods (activating the restored deployment writes them again from the profile) and the cluster plugin's handover diagnostic dumps (`data/cluster/handover-diff`). Restoring it needs a deployment whose profile pins the mods; otherwise the restore stops with "The snapshot left out node mods that the candidate deployment does not pin".

To restore, stop the cluster, open **Maintenance → Backups**, pick **Backup to restore**, choose **Prepare restore with fresh credentials**, review the generated request under **Restore prepared backup**, then choose **Restore backup**. Restore always rotates the cluster's runtime credentials. Each Host keeps the replaced runtime data beside the restored one. If the saved profile changed after the backup's deployment, the cluster's profile stays unset and the Cluster settings panel asks you to choose and stage one.

Cluster backups live in `<backup directory>/Clusters/<hash of the Cluster ID>/`. See [Backups](Backups.md) for the backup directory.

## Convert a standalone server to a cluster

Use **Convert to cluster** in the server's **More server actions** menu (page `/servers/<Server ID>/convert-to-cluster`). It needs cluster manage, server editing and configuration editing permissions, plus access to the destination cluster.

1. Choose **Create a new cluster** or an empty existing cluster in **Destination cluster**.
2. Click **Convert to cluster**. These are the only setup controls.

Quasar checks access restrictions and recorded plugin settings, reserves the source against other offline operations and supervised starts, saves and stops it if needed, and disables its automatic startup. It waits up to five minutes for a graceful stop without force-killing it. The source data remains intact and the converted cluster stays stopped for review and manual player cutover.

Quasar generates a new cluster ID when needed and keeps the source's server/world names. It chooses an enrolled host matching the destination address, otherwise the local Linux Host, otherwise the first enrolled host. Quasar starts the local Host when supported and waits for the selected Host to connect. Windows and container installs need an enrolled Linux host when local hosting is unavailable.

Quasar chooses free managed ports, downloads a compatible release, prepares the source's plugins, freezes runtime dependencies, detects the game build, and generates and installs credentials. Existing empty clusters receive these managed settings automatically. Conversion uses two regular nodes, one hot spare and World Authority on one host, so plugin shared storage works without a manual shared mount. Capacity can be changed later.

Quasar then takes server and world backups, converts a world copy with MagnetarWorld, stages the profile's Workshop mods (a profile with mods needs the Steam Web API key), transfers verified inputs, and prepares and activates the stopped cluster. Keep its public UDP port open through the firewall. The conversion URL and generated placement allow the same attempt to resume after an interruption.

Conversion is blocked when:

- The profile has a server password, group whitelist, bans or reserved slots: "This cluster package cannot transfer password, group whitelist, bans or reserved slots. Conversion is blocked to preserve access restrictions."
- The online mode is not public: "This cluster package does not transfer non-public online modes."
- The profile has plugins but no recorded settings: "No recorded plugin settings. Connect the source Agent once before stopping the server, then review again."
- A plugin configuration has no clear SDK type (custom providers or conflicting live copies).
- The frozen plugin set does not match the source: "Frozen dependency snapshot does not contain every selected source plugin. Missing: …", "Frozen dependency snapshot contains additional common plugins; align it with the reviewed source selection." or, for a plugin pinned to a 40-character commit, "Frozen plugin commit differs from an explicitly pinned source plugin." `linux-compat`, `dotnet-compat`, `error-reporting` and `quasar-agent` may be present without being selected.
- The destination already has a deployment, or the source cannot stop cleanly. Conversion never overwrites a deployed cluster.

Automatic ports start with a player port P of at least 28000, avoiding other managed port ranges. Registry administration uses P+16, regular node backends P+1100 onward, controls P+1200 onward and World Authority P+1164/P+1264. The converter may reject worlds it does not support. Advanced manual API/CLI conversion retains explicit topology and frozen input fields; see [Cluster conversion](https://github.com/CometWorks/Quasar/blob/main/Docs/ClusterConversion.md).

## Convert a cluster to a standalone server

On the cluster page, open **Maintenance → Convert to a standalone server → Open conversion wizard** (page `/clusters/<Cluster ID>/convert-to-server`).

1. Stop the cluster cleanly. A cluster that has never run since setup cannot be converted: "The cluster has not run since setup, so it has no saved state to convert. Start the cluster once first."
2. Enter **Server ID**, **Server name**, **Public server port** and a base configuration profile.
3. Confirm. Quasar backs up every Host, reassembles the world from those copies (no shared filesystem needed) and creates the standalone server with Goal Off and autostart disabled.

Session settings and mods come from the converted world. Quasar copies the base profile to "<Server name> (converted)" and removes `cluster-node`, `cluster-wa` and `direct-transport`. The cluster's plugin settings are written to `cluster-plugin-configurations.json` in the new server's folder for you to review and reapply; Quasar does not write them into plugin-owned files. Shared plugin records and per-node private files stay in the backup.

### Progress and resume

The cluster-to-standalone wizard asks you to confirm that you reviewed the source,
destination, frozen plugin versions and plugin settings. Standalone-to-cluster
conversion needs only a destination choice and **Convert to cluster**.

Clicking **Convert to cluster**, **Back up and convert** or **Resume conversion** immediately replaces
the button with a loader, including while Quasar checks permissions and validates
the request. Repeated clicks cannot start another attempt. Errors appear beside
the conversion controls, and the button returns after a failed attempt.
The conversion loader shows its current phase and elapsed time. Maintenance
actions use the same loader, with live transfer and preparation messages while
staging. After 30 seconds, a note explains that work is still running. No
percentage is guessed for steps without measured progress.

The conversion page shows the phase and a **Conversion ID**, also in the page URL (`?conversion=<uuid>`). Reopen that URL to resume an unfinished conversion. Completed conversions offer **Review destination** and no longer show **Resume conversion**, including when reopened. **Edit as a new conversion** starts over with a new ID. Work files are in `<backup directory>/Conversions/<uuid>/`. The world converter and large transfers have a 2-hour limit; ordinary Host executor requests time out after 30 seconds.

## Delete or forget a cluster

The delete icon on the card or page opens a dialog:

- **Delete definition** archives the cluster definition. Nodes must be stopped and all operations complete; Quasar checks each Host executor first. A Gateway still running or being respawned, such as after an unclean shutdown, is stopped once every Host proves its nodes stopped; nothing is saved. Delete refuses while a node runs or the Gateway's process state is unknown. API: `DELETE /api/v1/clusters/{name}` (204, or 409 when blocked).
- **Forget registration without checking remote processes** needs the Cluster ID typed exactly. Running processes keep running. API: `DELETE /api/v1/clusters/{name}/registration?confirmation={name}`.

After a delete, Quasar asks each host machine's Host executor to detach the cluster, so the Host stops polling and supervising it. Detaching is refused while a node or the Gateway still runs, the Gateway goal is On, an activation or restore is unfinished, or the deployment changed. The Host moves the cluster's attachment, Gateway specification and activation journal to `state/detached/<timestamp>-<Cluster ID>/`. Worlds, run roots and snapshots stay where they are until the retention below ends. When a Host is too old or refuses, the deletion still stands and Quasar logs a warning with the `Quasar.Host cluster detach` command to run on that machine (see [Host command line](ClusterHosts.md#host-command-line)). Forget does not detach: run that command once the processes are stopped.

Both keep worlds, Host data and backups for the **Keep deleted clusters (days)** set on the Backup page (default 7, 0 keeps them forever). Then Quasar removes them, manual backups included; see [Cluster storage](https://github.com/CometWorks/quasar/blob/main/Docs/ClusterStorage.md#deleted-clusters). A run root you chose outside the Host's `state/conversions` stays, and after a forget a Host keeps the cluster's data until you detach it. The definition moves to `Clusters/<Cluster ID>/History/<timestamp>-<guid>-deleted.json`. A removed Cluster ID, in any letter case, cannot be reused: "This cluster ID was removed. Choose a new ID to avoid reusing retained Host state or operation history."

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
| `ManagedRuntime/Tools/.objects` | Shared content store. Identical files across packages, snapshots and mods are stored once and hard-linked. Each node's `Mods` folder hard-links the Host's read-only copies too, instead of copying them per node. |
| `<backup directory>/Clusters/`, `<backup directory>/Conversions/` | Cluster backups and conversion work folders. |

Quasar removes unused package, dependency and mod content once no cluster, prepared update or retained backup refers to it. Cached cluster releases keep the newest two as well. Live clusters have no age-based retention; a deleted cluster's data has the limit above. Completed setup and conversion delete their temporary installation trees and transfer archives.

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
| Deployment | `create FILE`, `deployment-prepare NAME FILE`, `release-update-prepare NAME`, `world-files NAME`, `world-files-prepare NAME FOLDER`, `deployment`, `deployment-activate NAME FILE`, `update NAME FILE`, `update-status`, `update-abandon` |
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
- **"This stopped cluster has no clean shutdown proof."** Use **Recover stopped cluster after an unclean shutdown** before updating. If every stop ends dirty and only the prepared update can fix that, use [Update despite the recorded dirty shutdown](#update-despite-the-recorded-dirty-shutdown).
- **"This cluster release cannot start an update after a dirty shutdown."** The selected cluster release has no `managedRecoveryUpdate` capability. Recover the stopped cluster, or select a newer release.
- **"The recorded dirty shutdown is not the one that was accepted."** The Gateway's Dirty list changed since it was shown. Review it and try again.
- **"Resolve Workshop dependencies before staging: …"** A mod dependency could not be resolved; fix the profile's mod list.
- **"Magnetar's plugin exporter and the selected dependency snapshot use different PluginSdk builds."** Managed Magnetar was updated after the snapshot was frozen, and the cluster release or the new Magnetar declares no PluginSdk contract, or the new Magnetar's contract doesn't fit the release. In the last case the error names both contracts and says whether a newer Magnetar or another cluster release is needed. Otherwise stage a matching cluster release.
- **"Select the release and dependencies used by the active deployment before conversion."** Reselect the active package and dependencies before converting a cluster back.
- **`cluster-credentials.json` damaged.** Quasar moves an unreadable `cluster-credentials.json` to `cluster-credentials.json.corrupt-<timestamp>` and starts without it. Restore it from a backup together with the Data Protection keys, or re-create the clusters and Host enrollments.

## Profile command aliases

Admins can add per-profile shortcuts such as `!grids list` to `!ess grids list`
in **Configs > Command aliases**. Aliases retain the player's permissions. Save
and restart the standalone server, or stage and apply the cluster profile.
See [Command aliases](ConfigProfiles.md#command-aliases) for matching rules,
collisions, limits and the required Magnetar version.

## Sources
- `Quasar/Components/Pages/NewServerDialog.razor`
- `Quasar/Services/ServerManagementActions.cs`

- `Docs/ClusterOfflineCompensation.md`
- `Quasar/Components/Dashboard/ClusterPartitionsPanel.razor`
- `Quasar/Components/Dashboard/ClusterOfflineResults.razor`
- `Contracts/ClusterGateway.AdminContract/OfflineContract.cs`
- `Docs/ClusterOverview.md`
- `Quasar/Components/Dashboard/ClusterHealthOverview.razor`
- `Quasar/Components/Dashboard/ClusterResourceUsage.razor`
- `Quasar/Services/ClusterOverview.cs`
- `Quasar/Services/ClusterFleetService.cs`
- `Quasar/Services/ClusterPlayerService.cs`
- `Quasar/Services/KnownPlayerCatalog.cs`
- `Quasar/Components/Pages/Players.razor`
- `Quasar.Host.Contract/HostContract.cs`
- `Quasar.Host/GatewayActualizer.cs`
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
- `Quasar/Services/ClusterUpdatePreparationService.cs`
- `Quasar.Host/WorldFilesMigration.cs`
- `Quasar.Host/ModStorageSeed.cs`
- `Quasar.Host/DeploymentSnapshots.cs`
- `Quasar.Host/ProfileContentMigration.cs`
- `Quasar.Host/DeploymentActivation.cs`
- `Quasar/Services/ClusterDeploymentService.cs`
- `Quasar/Services/ClusterContentMonitor.cs`
- `Quasar/Services/ClusterConversionService.cs`
- `Quasar/Services/ClusterCredentialStore.cs`
- `Quasar/Services/ClusterDependencyService.cs`
- `Quasar/Services/ClusterGatewayClient.cs`
- `Docs/ClientDiagnostics.md`
- `Contracts/ClusterGateway.AdminContract/RotationContract.cs`
- `Contracts/ClusterGateway.AdminContract/DiagnosticsModels.cs`
- `Quasar/Services/ClusterPackageService.cs`
- `Quasar/Services/ClusterReconciler.cs`
- `Quasar/Services/ClusterReleaseMonitor.cs`
- `Quasar/Services/ClusterSetupService.cs`
- `Quasar/Services/QuasarDefaultPlugins.cs`
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
- `Quasar/Services/QuasarCommandAliases.cs`
- `Quasar.Agent/CommandAliasConfiguration.cs`
- `Quasar/Components/Shared/QuasarOperationLoader.razor`
- `Quasar/Components/Shared/QuasarOperationLoader.razor.css`
- `Quasar/Components/Pages/ClusterConsoleDialog.razor.css`
