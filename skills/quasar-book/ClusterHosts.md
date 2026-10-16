# Cluster hosts

The Host executor (`Quasar.Host`) is the service that runs on every cluster host machine. It starts and supervises the Cluster Gateway and the cluster nodes, keeps them running while Quasar is down, and takes orders from Quasar over an outbound connection. This page covers its packages, files, configuration, credentials, connection to Quasar, command line, updates, shutdown behavior and connection troubleshooting. Enrollment steps are in [Guided cluster setup](ClusterSetup.md#enroll-host-machines).

## Packages and binary

- Each Linux Quasar release ships the Host executor as a self-contained single-file executable at `Host/Quasar.Host` next to the web worker. Quasar's installer, the enrollment command, SSH installation, the manual package and the automatic updater all use this same file, so the Host version always follows the Quasar web release, not the cluster package.
- Releases also publish `quasar-host-linux-x64.tar.gz` and `quasar-host-win-x64.zip` with the Host publish tree (`Quasar.Host` or `Quasar.Host.exe` at the root), listed in the release `SHA256SUMS`. Quasar does not use these archives during enrollment.
- Guided enrollment and automatic installation support Linux x64 only.
- `Quasar.Host --self-test` runs a built-in test with harmless child processes and temporary state. It does not start Space Engineers or contact Quasar.

## Files on a host machine

The automatic installer puts everything under the account that ran it:

| Path | Content |
| --- | --- |
| `~/.local/share/Quasar/Hosts/<id>/Quasar.Host` | The executable. |
| `~/.local/share/Quasar/Hosts/<id>/host.json` | Host configuration. |
| `~/.local/share/Quasar/Hosts/<id>/state/` | State directory (from `stateDirectory` in `host.json`). |
| `state/credentials.json` | Enrollment and cluster credentials (owner read/write only). |
| `state/credentials/<cluster>/` | Per-cluster executor token roster. |
| `state/attachments/`, `state/gateways/`, `state/deployments/` | Cluster attachments, Gateway specifications and activation journals. |
| `state/launch-records/`, `state/gateway-launch-records/` | Records of started nodes and Gateways, used to re-adopt them after a Host restart. |
| `state/conversions/` | Installations, worlds and mods sent by Quasar (`installations/`, `worlds/`, `mods/`), one folder per deployment with `configuration/` and `runtime/`, and the shared content store `.objects`. |
| `state/conversions/<id>/runtime/` | The cluster's run root: node data, saves and, for a single-machine cluster, `plugin-shared/`. |
| `state/snapshots/`, `state/restores/` | Backup snapshots and restore journals. |
| `state/diagnostics/` | Diagnostic data of the Host and its child processes. |
| `bin/dotnet` | Link to the system `dotnet`, put first on the service `PATH`. |
| `bin/host-update.py` | The automatic updater. |
| `~/.config/systemd/user/quasar-host-<id>*.service`, `*.timer` | systemd user units. |
| `~/.steam/sdk64/steamclient.so` | Valve's Steam client library for the Gateway, sent by Quasar during setup. |

On the Quasar machine, enrollments are stored separately in `<install root>/Hosts/<id>/host.json` and the credentials in `<install root>/cluster-credentials.json`.

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
| `connection.quasarUrl` | The Quasar origin the Host connects to. HTTPS, or HTTP only on loopback. |
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

1. The Host opens a WebSocket to `wss://<quasar>/api/v1/hosts/<id>/connect` with its enrollment credential as a bearer token (20-second keep-alive).
2. When Quasar needs to call the Host, it sends a tunnel ID over that socket. The Host opens a second WebSocket to `/api/v1/hosts/<id>/tunnels/<id>` and bridges it to its loopback control port. At most 16 tunnels per Host are open at once.
3. If the connection drops, the Host logs "Quasar connection interrupted (<error>); retrying." and reconnects every 5 seconds. Supervision of the Gateway and nodes continues while disconnected.

The Hosts page and the setup wizard show **Connected** while that WebSocket is open. A reverse proxy in front of Quasar must pass WebSocket upgrades for `/api/v1/hosts/`.

Quasar talks to the Cluster Registry directly over the LAN/VPN, not through the tunnel: a guided cluster's Registry admin URL is `http://<Gateway host address>:<public port + 16>`. Quasar must be able to reach that port.

## systemd units

| Unit | Purpose |
| --- | --- |
| `quasar-host-<id>.service` | Runs `Quasar.Host run --config host.json`. `Restart=on-failure`, `RestartSec=5`, `KillMode=process`: restarting or updating the Host leaves the Gateway and nodes running, and the new Host process re-adopts them. |
| `quasar-host-<id>-update.timer` / `-update.service` | Automatic updates, see below. |
| `quasar-host-<id>-stop.service` | Saves and stops the clusters at logout or machine shutdown. Never restart or stop this unit by hand: stopping it shuts the clusters down. |

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

Quasar installs the timer by itself for older local (loopback) enrollments that lack it. Older remote enrollments need one reinstall: choose the existing registration under **Hosts → Add host machine** and install again by command or SSH. Manual installations have no updater; replace `Quasar.Host` yourself.

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

With the manual package, have your service manager run `./Quasar.Host stop-clusters --config host.json` before stopping the Host and allow 110 seconds.

## How the Host runs the Gateway and nodes

- Quasar sends each Host a deployment (verified installation, world, configuration) and activates it while the cluster is stopped. The Host keeps the active specification on disk, so it works without Quasar.
- The Gateway Host starts the Cluster Gateway when the cluster goal is On and stops the exact recorded process when it is Off. It reconciles every `pollIntervalSeconds`.
- Each Host polls its Gateway and starts or kills nodes as the Cluster Registry plans. Nodes receive `CLUSTER_ID`, `CLUSTER_NODE_ID`, `CLUSTER_NODE_ROLE`, `CLUSTER_SLOT_ID`, `CLUSTER_GATEWAY_REGISTRY`, `CLUSTER_DEPLOYMENT_REVISION` and `CLUSTER_LOCAL_WORLD_MODS=1`, so nodes load mods only from staged copies.
- A node counts as ready only after it writes its readiness receipt; otherwise it fails with `ready_timeout`.
- A node that exits with code 0 after a drain, or whose machine rebooted, is reported as gone, not failed. Any other exit is `process_exited:<code>`.
- A Gateway that keeps failing is started again after 5 seconds, doubling up to 5 minutes; the status then ends in `;respawn_in_seconds=N`. A Gateway that ran for 2 minutes resets the delay.
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

## Host command line

| Command | Purpose |
| --- | --- |
| `Quasar.Host run --config host.json [--once]` | Run the service. `--once` does one pass and exits. |
| `Quasar.Host stop-clusters --config host.json` | Shut down this Host's running clusters with a save (used by the stop unit). |
| `Quasar.Host status --url URL --token-env ENV` | Print the Host status JSON from its control port. |
| `Quasar.Host attachment apply`, `gateway apply`, `deployment activate` `--url URL --token-env ENV --file FILE` | Apply an attachment, Gateway specification or activation directly. Quasar refuses these for managed clusters through its own API; calling the Host directly bypasses Quasar's safety checks. |
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

Exit codes of `status` and the `apply` commands: `0` success, `2` usage or unset credential variable, `3` the Host rejected the request (the error code is printed), `4` connection or protocol error.

## Logs

The Host writes to stdout/stderr, which systemd stores in the user journal (`journalctl --user -u quasar-host-<id>.service`). Typical lines:

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
| Journal repeats "Quasar connection interrupted (…); retrying." | Network, TLS or proxy problem between Host and Quasar, or Quasar is down. A rejected credential also shows this way (Quasar answers 401). |
| "Remote enrollment requires an HTTPS Quasar URL." | `connection.quasarUrl` is HTTP on a non-loopback address or has a path. The Host stops trying. Re-enroll with an HTTPS origin. |
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
- `Quasar/Services/ClusterLocalHostUpdater.cs`
- `Quasar/Services/ClusterCredentialStore.cs`
- `Quasar/Services/ClusterGatewayClient.cs`
- `Quasar/Services/ClusterSetupService.cs`
- `Quasar/Services/ClusterSteamClientLibrary.cs`
- `Quasar.Bootstrap/Quasar.Bootstrap.csproj`
- `scripts/package-linux-release.sh`
- `scripts/package-windows-release.ps1`
- `Shared/ClusterDeployment/ClusterContentStore.cs`
