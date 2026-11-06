# Cluster hosts

The Host executor (`Quasar.Host`) is the service that runs on every cluster host machine. It starts and supervises the Cluster Gateway and the cluster nodes, keeps them running while Quasar is down, and takes orders from Quasar over an outbound connection. This page covers its packages, files, configuration, credentials, connection to Quasar, command line, updates, shutdown behavior and connection troubleshooting. Enrollment steps are in [Guided cluster setup](ClusterSetup.md#enroll-host-machines).

## Packages and binary

- Each Quasar release package (`quasar-linux-x64.tar.gz`, `quasar-win-x64.zip`) ships the Host executor as a self-contained single-file executable at `Host/Quasar.Host` (`Host/Quasar.Host.exe` on Windows), next to the Quasar application and its updater. Quasar's local Host, the enrollment command, SSH installation, the manual package and the automatic updater all use the file of the running Quasar release, so the Host version follows the Quasar release, not the cluster package. Enrollment does not download another release archive.
- `QUASAR_HOST_BINARY` points Quasar at another `Quasar.Host` file, for development builds that lack `Host/`.
- Guided enrollment and automatic installation support Linux x64 only.
- `Quasar.Host --self-test` runs a built-in test with harmless child processes and temporary state. It does not start Space Engineers or contact Quasar.

## Security upgrade and network access

The Host's routes on Quasar (enrollment download, connection, tunnels and update downloads) are [machine endpoints](AccessControl.md#machine-endpoints). Besides the Host's bearer credential, Quasar checks the socket address against `Quasar:MachineEndpoints:AllowedSources`, the `Host` header against `AllowedHosts`, and refuses any `Origin` header. The defaults allow only loopback, which covers the local Host. Add every other host machine, or its site's private subnet, and the Quasar address it connects to, before you enroll it.

A Host's command listener stays on loopback. The Host connects out to Quasar, so a host machine needs no open inbound port.

Quasar releases from the DIS-0042 security fix on need the Host from the same release. When you update from an older Quasar, save and stop the clusters, back up each Host's `host.json` and `state/`, update Quasar first and then every Host, and re-pin the cluster's agent with **Stage profile changes** and **Apply pending update** before you start the cluster again. `Docs/SecurityUpgradeDIS0042.md` in the Quasar repository has the full procedure.

## Files on a host machine

This section is about enrolled machines. The Quasar machine's own Host is described in [The local Host](#the-local-host). The automatic installer puts everything under the account that ran it:

| Path | Content |
| --- | --- |
| `~/.local/share/Quasar/Hosts/<id>/Quasar.Host` | The executable. |
| `~/.local/share/Quasar/Hosts/<id>/host.json` | Host configuration. |
| `~/.local/share/Quasar/Hosts/<id>/state/` | State directory (from `stateDirectory` in `host.json`). |
| `state/credentials.json` | Enrollment and cluster credentials (owner read/write only). |
| `state/credentials/<cluster>/` | Per-cluster executor token roster. |
| `state/attachments/`, `state/gateways/`, `state/deployments/` | Cluster attachments, Gateway specifications and activation journals. |
| `state/launch-records/`, `state/gateway-launch-records/` | Records of started nodes and Gateways, used to re-adopt them after a Host restart. A deleted cluster's go when its [retention](Clusters.md#delete-or-forget-a-cluster) ends. |
| `state/conversions/` | Installations, worlds and mods sent by Quasar (`installations/`, `worlds/`, `mods/`), one folder per deployment with `configuration/` and `runtime/`, and the shared content store `.objects`. |
| `state/conversions/<id>/runtime/` | The cluster's run root: node data, saves and, for a single-machine cluster, `plugin-shared/`. |
| `state/snapshots/`, `state/restores/` | Backup snapshots and restore journals. |
| `state/detached/` | Attachments, Gateway specifications and activation journals of deleted clusters, one folder per detach. The Host no longer reads them and removes them when the deleted cluster's [retention](Clusters.md#delete-or-forget-a-cluster) ends. |
| `state/diagnostics/` | Diagnostic data of the Host and its child processes. |
| `bin/dotnet` | Link to the system `dotnet`, put first on the service `PATH`. |
| `bin/host-update.py` | The automatic updater. |
| `~/.config/systemd/user/quasar-host-<id>*.service`, `*.timer` | systemd user units. |
| `~/.steam/sdk64/steamclient.so` | Valve's Steam client library for the Gateway, sent by Quasar during setup. |

On the Quasar machine, enrollments are stored separately in `<install root>/Hosts/<id>/host.json` and the credentials in `<install root>/cluster-credentials.json`.

## The local Host

On Linux x64 every Quasar install runs its own Host executor, ID `local`, without enrollment. It uses the same binary, `host.json` format, credentials and connection as an enrolled Host, but Quasar runs it as a process of its own:

| Path under the install root | Content |
| --- | --- |
| `LocalHost/host.json` | Host configuration. Quasar rewrites it before each start, so a changed Quasar port reaches the Host. |
| `LocalHost/state/` | State directory, laid out as on any host machine (credentials, deployments, cluster saves). |
| `LocalHost/host.log` | The output of the Host, its Gateways and its nodes. The Host rotates it to `host.log.1` once it passes 64 MB, keeping `host.log.1` to `host.log.3`, and Quasar does the same at a Host start once it passes 10 MB. |
| `LocalHost/host.pid` | PID, process identity, binary hash and configuration hash of the running Host. |
| `LocalHost/stop-clusters` | Runs `Quasar.Host stop-clusters` for this Host. The clusters unit calls it. |

- The binary is the running Quasar release's `Host/Quasar.Host` (`QUASAR_HOST_BINARY` overrides it). It is not copied.
- Quasar starts the Host when the Hosts page or cluster setup is opened, when Quasar needs it for a cluster, and at Quasar start when a cluster is deployed on this PC. It runs in its own session (`setsid`), so `Ctrl+C` on a foreground Quasar doesn't reach it. If it exits, Quasar starts it again within 15 seconds.
- Commands to the local Host wait for its authenticated connection within a 30-second connection deadline, including commands from startup storage maintenance. A launch failure or timeout fails the operation without stopping Quasar; storage maintenance defers collection and applies a one-minute retry backoff. Caller cancellation and Quasar shutdown cancel the wait.
- A restarted or updated Quasar adopts the running Host by its PID and process identity when its binary and configuration hashes match. A changed binary or configuration, including Quasar's address or port, restarts the Host. The first update from a version without a configuration hash restarts it once. The Gateway and nodes keep running, as with any Host restart.
- When Quasar stops, it stops the Host unless a Gateway with goal On runs on it, or this PC takes part in a cluster whose Gateway runs elsewhere. Then the Host keeps running, like managed servers do, and the next Quasar start adopts it. With `Quasar:PreserveManagedServersOnShutdown` set to `false`, Quasar saves and stops those clusters with `stop-clusters` first, then stops the Host. An application update by the native updater always leaves them running.
- The Docker image has no local Host.
- A local Host that an older Quasar installed in `~/.local/share/Quasar/Hosts/<id>` with its own `quasar-host-<id>*` units stays an ordinary enrolled host machine next to the new one. Quasar doesn't touch it. [Install on Linux](InstallLinux.md#uninstall) shows how `uninstall.sh` reports it.

## host.json

The installer writes `host.json`; you normally do not edit it. Fields:

| Field | Meaning |
| --- | --- |
| `executorId` | `quasar-<id>`. Required. |
| `hostId` | The Host machine ID. Required. |
| `pollIntervalSeconds` | How often the Host reconciles and reports to each Gateway, 1 to 15 (installer default 2). Executor leases last 60 seconds. |
| `stateDirectory` | `state`, relative to the folder of `host.json`. Defaults to `host-state` when empty. |
| `attachments` | Static cluster attachments; empty for enrolled Hosts (Quasar adds attachments at runtime). |
| `command.url` | Control listener, `http://127.0.0.1:<port>` (default port 18400). Must be HTTP on loopback: "Host command URL must be an HTTP loopback origin". |
| `command.tokenEnvironmentVariable` | Name of the control credential. |
| `command.allowedSources`, `command.allowedHosts` | Optional. Source addresses and exact `Host` values the control listener accepts, checked like Quasar's [machine endpoints](AccessControl.md#machine-endpoints). Defaults: loopback only, and the authority of `command.url`. |
| `connection.quasarUrl` | The Quasar address the Host connects to. HTTPS, or HTTP only on loopback. It is a root URL, or the exact URL of a [hosted panel](HostedPanel.md) such as `https://dash.se-servers.net/<panel-name>/` with the trailing slash. |
| `connection.tokenEnvironmentVariable` | Name of the enrollment credential (the same credential as `command`). |
| `diagnosticsDirectory` | Optional; the `QUASAR_HOST_DIAGNOSTICS_DIRECTORY` environment variable overrides it. |

## Credentials

Quasar generates every secret. Names look like `QSR_MANAGED_<hex>`. At start the Host reads `state/credentials.json` and puts each entry into its own environment. Nothing else needs to be set.

- The enrollment credential authenticates the Host to Quasar and Quasar to the Host's control port.
- Guided setup installs per-cluster admin, join and executor credentials on every Host. Installing them again with different values fails with "Managed credentials are immutable; use a new cluster identity to replace them." A restore installs new ones.
- Each Gateway and node process gets only the secrets its specification references, not the enrollment credential.
- Quasar keeps its copies encrypted with its Data Protection keys. Back up `cluster-credentials.json` together with Quasar's `DataProtection-Keys` folder.

## Connection to Quasar

The Host connects out; Quasar never connects in to the Host machine.

1. The Host opens a WebSocket to `wss://<quasar>/api/v1/hosts/<id>/connect` with its enrollment credential as a bearer token (20-second keep-alive). With a hosted panel address the path keeps the panel prefix.
2. When Quasar needs to call the Host, it sends a tunnel ID over that socket. The Host opens a second WebSocket to `/api/v1/hosts/<id>/tunnels/<id>` and bridges it to its loopback control port. At most 16 tunnels per Host are open at once.
3. If the connection drops, the Host logs "Quasar connection interrupted (<error type>); retrying." and reconnects every 5 seconds. Supervision of the Gateway and nodes continues while disconnected. If `connection.quasarUrl` is invalid or the enrollment credential is missing, the Host logs "Quasar connection stopped (InvalidDataException): …" and stops trying until it restarts.

Quasar accepts one control connection per Host ID. A second one is closed with "Host already connected"; it does not replace the first.

Each node gets its own agent token in `QUASAR_AGENT_TOKEN`. The Host derives a key from its enrollment credential and from that a token bound to the cluster, slot, node, role and launch attempt. The Quasar Agent in the node presents it on `/ws/agent`, and Quasar recomputes it from the identity the node reports. Nodes never see the enrollment credential or the Host's key. A Host configuration without a `connection` section sets no token, so its nodes' agents can't connect.

The Hosts page and the setup wizard show **Connected** while that WebSocket is open. A reverse proxy in front of Quasar must pass WebSocket upgrades for `/api/v1/hosts/`.

Quasar talks to the Cluster Registry directly over the LAN/VPN, not through the tunnel: a guided cluster's Registry admin URL is `http://<Gateway host address>:<public port + 16>`. Quasar must be able to reach that port.

## systemd units

| Unit | Purpose |
| --- | --- |
| `quasar-host-<id>.service` | Runs `Quasar.Host run --config host.json`. `Restart=on-failure`, `RestartSec=5`, `KillMode=process`: restarting or updating the Host leaves the Gateway and nodes running, and the new Host process re-adopts them. |
| `quasar-host-<id>-update.timer` / `-update.service` | Automatic updates, see below. |
| `quasar-host-<id>-stop.service` | Saves and stops the clusters at logout or machine shutdown. Never restart or stop this unit by hand: stopping it shuts the clusters down. |

The local Host has no unit of its own. `install.sh` writes `<service name>-clusters.service` (default `quasar-clusters.service`) next to Quasar's unit instead. It does for the local Host what the stop unit does for an enrolled one: it is ordered after Quasar's service, so systemd stops it first at logout or shutdown, and its `ExecStop` runs `LocalHost/stop-clusters` with `TimeoutStopSec=110`. Restarting or stopping Quasar's service doesn't touch it, so clusters keep running. Quasar's unit sets `QUASAR_HOST_STOP_UNIT` to its path, which the Host uses for its warnings.

Useful commands, run as the Host account:

```bash
systemctl --user status quasar-host-<id>.service
journalctl --user -u quasar-host-<id>.service
systemctl --user restart quasar-host-<id>.service
```

## Automatic updates

The update timer runs 2 minutes after boot and then 15 minutes after each run. It runs `bin/host-update.py`, which:

1. Asks `GET /api/v1/hosts/<id>/update` (with the enrollment credential) for the SHA-256 and size of the Host binary of the running Quasar release.
2. Does nothing if the installed binary already matches.
3. Downloads `/update/binary`, checks size and SHA-256, keeps the old binary as `.Quasar.Host.previous`, swaps it in and restarts the Host unit. Cluster processes keep running.
4. If the Host does not stay active, it restores the previous binary and records the failed hash in `state/failed-host-update-sha256`. It then skips that version with "Host update failed previously; waiting for a different Quasar release." until Quasar offers a different one.

Updates need HTTPS, or HTTP on loopback ("Host updates require HTTPS or loopback HTTP."). Run an update check now with `systemctl --user start quasar-host-<id>-update.service`.

The local Host has no updater; Quasar restarts it from the new release, see [The local Host](#the-local-host). Older enrollments without the timer need one reinstall: choose the existing registration under **Hosts → Add host machine** and install again by command or SSH. Manual installations have no updater; replace `Quasar.Host` yourself.

## Shutdown and logout

At logout or machine shutdown, systemd tears down the user manager and kills whatever the Host left running. To save first, `quasar-host-<id>-stop.service` runs `Quasar.Host stop-clusters --config host.json` in its `ExecStop` with `TimeoutStopSec=110`. `stop-clusters` asks every Gateway this Host runs with goal On to shut down with a 30-second drain, and waits up to 105 seconds. It prints `cluster=<id> down`, `already down`, `shutdown failed: …` or `shutdown did not finish in time`.

After the machine comes back, Quasar restarts a cluster whose goal is On if the shutdown was clean. See [Clusters](Clusters.md#machine-reboot-and-logout).

When the Host runs as a systemd service, it checks three things and reports them as warnings on the enrollment form:

- "User lingering is off for '<user>', so this Host and its clusters stop when that user logs out. Enable it with: sudo loginctl enable-linger <user>"
- "The cluster stop unit is missing, so a reboot kills the clusters without a save. Run the Host enrollment again to install it."
- "systemd gives this user's services N s to stop at shutdown, but a clean cluster save needs 110 s, so the clusters are killed without a save. …"

The last one happens on Ubuntu, which ships `/usr/lib/systemd/system/user@.service.d/timeout.conf` with `TimeoutStopSec=5`. Override it with a file of the same name:

```bash
sudo mkdir -p /etc/systemd/system/user@.service.d
printf '[Service]\nTimeoutStopSec=150\n' | sudo tee /etc/systemd/system/user@.service.d/timeout.conf
sudo systemctl daemon-reload
```

The local Host gets the same checks against `<service name>-clusters.service`, without the lingering and timeout checks for a system service. Two more warnings are only for the local Host:

- "Quasar doesn't run as an installed service, so a reboot kills the clusters on this PC without a save. Install it with Quasar's install.sh." (a foreground `./Quasar serve`)
- "Quasar's service has no cluster stop unit, so a reboot kills the clusters on this PC without a save. Run Quasar's install.sh again to add it." (a service installed by an older `install.sh`)

With the manual package, have your service manager run `./Quasar.Host stop-clusters --config host.json` before stopping the Host and allow 110 seconds.

## How the Host runs the Gateway and nodes

- Quasar sends each Host a deployment (verified installation, world, configuration) and activates it while the cluster is stopped. The Host keeps the active specification on disk, so it works without Quasar.
- The Gateway Host starts the Cluster Gateway when the cluster goal is On and stops the exact recorded process when it is Off. It reconciles every `pollIntervalSeconds`.
- Before it starts the Gateway, the Host verifies the installation and, on a cluster's first start, creates every node's data folder and the world from the deployment's seeds. That can take a minute on a large cluster (35 s for 8 nodes). The Host does it in the background: meanwhile the cluster's lifecycle shows Converging with "Waiting for the Gateway process.", and the Gateway starts when it is done. Activation, restore and snapshots of that cluster are refused until then.
- Each Host polls its Gateway and starts or kills nodes as the Cluster Registry plans. Nodes receive `CLUSTER_ID`, `CLUSTER_NODE_ID`, `CLUSTER_NODE_ROLE`, `CLUSTER_SLOT_ID`, `CLUSTER_GATEWAY_REGISTRY`, `CLUSTER_DEPLOYMENT_REVISION` and `CLUSTER_LOCAL_WORLD_MODS=1`, so nodes load mods only from staged copies.
- A node counts as ready only after it writes its readiness receipt; otherwise it fails with `ready_timeout`.
- A node that exits with code 0 after a drain, or whose machine rebooted, is reported as gone, not failed. Any other exit is `process_exited:<code>`.
- The Host status lists the last failed launch of each node slot with its failure code, the exception the node reported, if any, and the path of the node's log on that machine. Quasar uses it to say why nodes don't start during a cluster update.
- Before it kills a node that the Cluster Registry declared dead, or one that hit `ready_timeout`, the Host spends up to 20 seconds collecting evidence: `hang-evidence.txt` in the node's diagnostics folder with each thread's state, CPU use and stack (from the runtime's `createdump`), plus a minidump only when dump consent is granted. A planned stop or an operator's kill collects nothing. On Linux with `kernel.yama.ptrace_scope=1`, the Ubuntu default, a fully frozen node may yield thread states only.
- A Gateway that keeps failing is started again after 5 seconds, doubling up to 5 minutes; the status then ends in `;respawn_in_seconds=N` and reports the number of failed launches in a row. A Gateway that ran for 2 minutes resets the delay. Once a Gateway with goal Off in Quasar has failed three launches in a row and no node runs, Quasar sets its goal Off on the Host too, which ends the respawns.
- A Gateway can stay alive but stop serving: after a registry persistence failure it answers every control request with 503 `registry_persistence_failed` until it restarts. The Host asks the Gateway's control endpoint (its `--control` argument) on every reconcile. Once that answer lasts 60 seconds, it records a `gateway_control_unhealthy` diagnostics report, stops the Gateway and starts it again with the same start generation, as after an exit. The status then shows `control_unhealthy:registry_persistence_failed_for_seconds=N`. Any other answer, or none, resets the count. `QUASAR_HOST_GATEWAY_UNHEALTHY_RESTART_SECONDS` in the Host's environment changes the 60 seconds; `0` turns the restart off. Fix the storage fault (a full or read-only disk, for example): the restart alone does not.
- The Host prints Gateway output with the prefix `[gateway <cluster>]` and keeps its last 20 lines (300 characters each). When the Gateway exits, Quasar shows them under **Last Cluster Registry process output** with `process_exited:exit_code=N`. After a Host restart the output of a still-running Gateway is lost until it restarts.
- If an interrupted activation or restore cannot be recovered at Host start, the Host pauses only that cluster and logs `cluster=<id> paused: <reason>`; retrying the operation from Quasar resumes it.

Node and Gateway failure codes:

| Code | Meaning |
| --- | --- |
| `process_exited`, `process_exited:<code>` | The process ended unexpectedly. |
| `ready_timeout` | The node did not report readiness before its deadline. |
| `unmanaged_conflict:…` | An unknown process holds a reserved port or the slot. The Host leaves it alone; stop it yourself. |
| `spawn_preflight_failed:…` | Bundle verification or launch preparation failed before start. |
| `recorded_process_identity_mismatch`, `running_spec_mismatch`, `running_deployment_mismatch` | The running process does not match the record or the active deployment. |
| `gateway_stop_fence_mismatch`, `kill_authority_mismatch` | A stop or kill request named a different process than the one running. |

The Host also reports Gateway working-set RAM and total process CPU for the [cluster health overview](Clusters.md#cluster-pages). CPU 100% equals one logical CPU and needs two samples of the same process. Process restarts reset the comparison. The optional metrics field keeps older Hosts compatible; their Gateway resource readings appear as unavailable. The overview excludes Gateway readings older than 15 seconds.

## Host command line

| Command | Purpose |
| --- | --- |
| `Quasar.Host run --config host.json [--once]` | Run the service. `--once` does one pass and exits. |
| `Quasar.Host stop-clusters --config host.json` | Shut down this Host's running clusters with a save (used by the stop unit). |
| `Quasar.Host status --url URL --token-env ENV` | Print the Host status JSON from its control port. |
| `Quasar.Host attachment apply`, `gateway apply`, `deployment activate` `--url URL --token-env ENV --file FILE` | Apply an attachment, Gateway specification or activation directly. Quasar refuses these for managed clusters through its own API; calling the Host directly bypasses Quasar's safety checks. |
| `Quasar.Host cluster detach --url URL --token-env ENV --cluster ID [--sha256 BUNDLE_SHA256]` | Stop supervising a deleted cluster: moves its attachment, Gateway specification and activation journal to `state/detached/`, keeps its data until the deleted cluster's [retention](Clusters.md#delete-or-forget-a-cluster) ends. Refused while any of its processes runs or its Gateway goal is On. `--sha256` is the active bundle manifest hash, from `bundleManifestSha256` in the `status` output; leave it out for an attachment without a bundle. Quasar runs it itself when you delete a cluster. |
| `Quasar.Host deployment prepare --file FILE --sha256 SHA256 --directory DIR` | Verify and copy deployment inputs offline. |
| `Quasar.Host deployment configure …`, `deployment export`, `deployment import` | Offline preparation and transfer of installations. |
| `Quasar.Host --self-test` | Built-in test. |

`--token-env` takes the name of an environment variable holding the control credential. For an enrolled Host, that name is `command.tokenEnvironmentVariable` in `host.json` and its value is in `state/credentials.json`:

```bash
cd ~/.local/share/Quasar/Hosts/<id>
VAR=$(python3 -c 'import json; print(json.load(open("host.json"))["command"]["tokenEnvironmentVariable"])')
export "$VAR=$(python3 -c 'import json, sys; print(json.load(open("state/credentials.json"))[sys.argv[1]])' "$VAR")"
./Quasar.Host status --url http://127.0.0.1:18400 --token-env "$VAR"
```

Exit codes of `status`, `cluster detach` and the `apply` commands: `0` success, `2` usage or unset credential variable, `3` the Host rejected the request (the error code is printed), `4` connection or protocol error.

## Logs

The Host writes to stdout/stderr, which systemd stores in the user journal (`journalctl --user -u quasar-host-<id>.service`). The local Host writes to `<install root>/LocalHost/host.log` instead. Typical lines:

- `cluster=<id> executor=<executor> heartbeat=accepted`: the Host reached that cluster's Gateway.
- `cluster=<id> error=Gateway request timed out` or `cluster=<id> error=<message>`: the Gateway did not answer; the Host retries.
- `cluster=<id> gateway error=<type>: <message>`: reconciling the Gateway failed for this cluster only.
- `[gateway <cluster>] …`: Gateway output.
- `Quasar connection interrupted (…); retrying.` / `Quasar connection stopped (…)`: the tunnel to Quasar.

The cluster console in Quasar shows Registry events and node plugin logs, not these process logs. See [Clusters](Clusters.md#cluster-console).

## Troubleshooting connection problems

| Symptom | Check |
| --- | --- |
| Host shows **Offline** / "Waiting for this Host executor to connect." | `systemctl --user status quasar-host-<id>.service` on the machine and its journal. Check the Host can reach `connection.quasarUrl` over HTTPS, and that any reverse proxy passes WebSockets. |
| Journal repeats "Quasar connection interrupted (…); retrying." | Network, TLS or proxy problem between Host and Quasar, or Quasar is down. A rejected credential or a refused source address also shows this way (Quasar answers 401 or 403). |
| Journal shows "Quasar connection stopped (InvalidDataException)" | `connection.quasarUrl` is HTTP on a non-loopback address, or has a path other than an exact hosted panel prefix. The Host stops trying. Re-enroll with an HTTPS address. |
| Host connection, enrollment download or update download gets 403 | Quasar's [machine endpoint](AccessControl.md#machine-endpoints) policy does not list the Host's source address or the `Host` value it sends, or a proxy added an `Origin` header. |
| "Enrollment credential is unavailable." | `state/credentials.json` lacks the enrollment credential named in `host.json`. A reinstall over the same folder refuses to replace it ("Existing Host enrollment credential differs"). |
| Two machines with the same Host ID | Quasar accepts one connection per Host ID and closes the second with "Host already connected". |
| Hosts page **unreachable**, tooltip "Cannot reach Host executor '<id>'. Start the Host executor (Quasar.Host) on that host machine and wait for it to reconnect before retrying." | The Host is not connected. |
| "Host executor command timed out." | The Host did not answer within 30 seconds. Check its load and journal. |
| "Host executor command contract version is incompatible." / "Host executor returned invalid contract JSON." | Host and Quasar versions differ. Let the Host update or reinstall it. |
| "Host tunnel limit reached." | 16 requests to that Host are already open. Retry later. |
| Hosts page "No Host executor command endpoints are configured." | No host machine is enrolled. |
| Quasar log "Host registration … is unreadable and was ignored; enroll that Host executor again." | `<install root>/Hosts/<id>/host.json` on the Quasar machine is damaged. |
| Reinstall fails with "Existing Host belongs to a different enrollment; its data has been preserved." or "Existing Host enrollment credential differs; its data has been preserved." | The machine already has a Host with that ID from another enrollment or with another credential. The installer does not overwrite it. |
| "The Host directory must not be a symbolic link." | `~/.local/share/Quasar/Hosts/<id>` is a symlink; use a real directory. |
| Cluster pages say "Cannot reach the Cluster Registry at …" while the cluster runs | Quasar cannot reach the Registry admin port (public port + 16) on the Gateway host. Check the LAN/VPN route and firewall. |

Stop clusters through their Quasar controls before you remove a Host installation.

## Sources
- `Docs/SecurityUpgradeDIS0042.md`
- `Docs/ClusterGuidedSetupPlan.md`
- `Docs/Configuration.md`
- `Docs/BuildingAndDevelopment.md`
- `Docs/Phase4IntegrationPlan.md`
- `Docs/ClusterStorage.md`
- `Quasar.Host/Program.cs`
- `Quasar.Host/HostConnection.cs`
- `Quasar.Host/HostCredentials.cs`
- `Quasar.Host/HostCommandCli.cs`
- `Quasar.Host/HostCommandServer.cs`
- `Quasar.Host/HostShutdown.cs`
- `Quasar.Host/PausedClusters.cs`
- `Quasar.Host/GatewayActualizer.cs`
- `Quasar.Host/NodeActualizer.cs`
- `Quasar.Host/ProcessOutputTail.cs`
- `Quasar.Host/HungNodeEvidence.cs`
- `Quasar.Host/DeploymentPreparation.cs`
- `Quasar.Host/ManagedPreparation.cs`
- `Quasar.Host/DeploymentSnapshots.cs`
- `Quasar.Host/DeploymentActivation.cs`
- `Quasar.Host/Quasar.Host.csproj`
- `Quasar.Host.Contract/HostContract.cs`
- `Quasar/Assets/host-update.py`
- `Quasar/Components/Pages/Hosts.razor`
- `Quasar/Components/Shared/HostEnrollment.razor`
- `Quasar/Services/ClusterHostInstaller.cs`
- `Quasar/Services/ClusterHostTunnels.cs`
- `Quasar/Services/ClusterHostEnrollmentApi.cs`
- `Quasar/Services/ClusterHostCatalog.cs`
- `Quasar/Services/ClusterHostClient.cs`
- `Quasar/Services/ClusterLocalHost.cs`
- `install.sh`
- `Quasar/Services/ClusterCredentialStore.cs`
- `Quasar/Services/ClusterGatewayClient.cs`
- `Quasar/Services/ClusterSetupService.cs`
- `Quasar/Services/ClusterSteamClientLibrary.cs`
- `Quasar.Bootstrap/Quasar.Bootstrap.csproj`
- `scripts/package-linux-release.sh`
- `scripts/package-windows-release.ps1`
- `Shared/ClusterDeployment/ClusterContentStore.cs`
- `Shared/Networking/QuasarControlAddress.cs`
- `Shared/Networking/MachineEndpointPolicy.cs`
- `Quasar/Services/DedicatedServerSupervisor.cs`
- `Quasar/Services/ClusterReconciler.cs`
- `Docs/ReleaseNotes1.5.md`
