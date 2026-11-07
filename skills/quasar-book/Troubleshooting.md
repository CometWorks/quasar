# Troubleshooting

This page lists where Quasar and its servers write logs, and the common failures with what to check first. Details live on the topic pages linked from each section. Error reporting to CometWorks is covered in [Diagnostics](Diagnostics.md).

## Guided installation problems

Windows starts with `Install.cmd` and requests UAC only after **Install**. Linux
starts with `Install.desktop` and uses polkit; the file manager may require
**Allow Launching** or **Trust** first. Linux needs kdialog or zenity, pkexec and a
desktop authentication agent. Choose **Manual install** for headless hosts,
custom service accounts or an unavailable desktop dependency.

Linux keeps private installer output in
`${XDG_STATE_HOME:-~/.local/state}/Quasar/install-*.log`. Windows shows the backend
console and error dialog; run the same `install.ps1` command in an elevated shell
to retain output. Failed or declined elevation never counts as a successful
installation. See [installation](https://github.com/CometWorks/quasar/blob/main/Docs/Installation.md).

## Windows native service file access

An access-denied error reading `release.json`, or an automatic restore that cannot
overwrite application files, can indicate missing effective file grants. Keep the
reported recovery copy. Use the current complete package's `install.ps1 -RepairSupervision` with the original application/data paths and service names.
Files need direct rules; directory inheritance flags alone do not establish their
access. SYSTEM and Administrators need recovery access, both service users need
data access, and the updater needs application replacement access. The updater,
private package cache and metadata retain their separate restricted ACLs.
See [Windows permissions](InstallWindows.md#where-quasar-keeps-its-files).

The Windows fixture checks actual virtual-service identities and administrator
access for co-located and nested data. On failure it prints owner/protection/SDDL
and service paths/accounts, without credentials. Linux syntax checks cannot prove
Windows ACL or SCM behavior.

## Where the logs are

`<data dir>` below is the Quasar data directory, for example `~/.local/share/Quasar` on Linux, `%ProgramData%\Quasar` for a new Windows service installation, `/data` in Docker (`./quasar-data` on the host). See [Changing the data directory](#changing-the-data-directory) for how Quasar finds it, and [Configuration](Configuration.md#where-files-live).

| Log | Location | Contents |
| --- | --- | --- |
| Quasar log | `<data dir>/Logs/quasar.log` | Quasar's own messages: server starts, faults, health restarts, update and download errors, startup warnings |
| Discord log | `<data dir>/Logs/discord.log` | Discord bot messages only. They also go to `quasar.log`. |
| systemd journal (Linux service) | `journalctl --user -u quasar.service` (system service: `sudo journalctl -u quasar.service`) | Quasar log messages at the configured minimum level and format, alongside `quasar.log`, plus startup and fatal console lines. Existing units need supervision repair and a restart to enable the log copy. |
| Updater journal (Linux service) | `journalctl --user -u quasar-updater.service` (or `sudo journalctl -u ...`) | The updater service's messages: application replacement, health checks and recovery |
| Windows event log | **Application** log, source `QuasarUpdater` (the updater service name) | The updater service's messages. Quasar's own messages are in `quasar.log`. |
| Foreground console | the terminal running `./Quasar serve --foreground` or `Quasar.exe --foreground` | Startup and fatal console lines. Set `QUASAR_CONSOLE_LOGGING=true` to copy the application log there too. |
| Docker | `docker compose logs -f quasar`, and `./quasar-data/Logs/quasar.log` | Console output and the log file |
| DS log | `<data dir>/Magnetars/<server>/DedicatedServer/SpaceEngineersDedicated*.log` | The Space Engineers Dedicated Server's own log |
| Magnetar info log | `<data dir>/Magnetars/<server>/Magnetar/info*.log` | Magnetar and plugin startup, plugin exceptions, Quasar.Agent lines logged through PluginSdk |
| DS console output | `<data dir>/Diagnostics/runs/<run id>/process.log` | Standard output of each launched process, including `[Quasar.Agent] ...` connection messages. Rotated at 8 MiB, run folders kept 7 days. See [Diagnostics](Diagnostics.md). |
| Quasar.Host (cluster machines) | `journalctl --user -u quasar-host-<id>.service` | See [Cluster hosts](ClusterHosts.md#logs) |
| Quasar.Host (local Host on the Quasar machine) | `<install root>/LocalHost/host.log` | See [Cluster hosts](ClusterHosts.md#the-local-host) |

The DS and Magnetar paths are the defaults. A server with **Paths** overrides writes where those point.

### Log settings

| Key | Env | Default |
| --- | --- | --- |
| `Quasar:Logging:Directory` | `QUASAR_LOG_DIR` | `<data dir>/Logs` |
| `Quasar:Logging:Format` | `QUASAR_LOG_FORMAT` | `text` (or `json`) |
| `Quasar:Logging:MinimumLevel` | `QUASAR_LOG_MIN_LEVEL` | `Warn` (`Info` when `ASPNETCORE_ENVIRONMENT=Development`) |

At the default `Warn`, Info messages such as server exit codes and "recovered after restart" lines are not written. Set `QUASAR_LOG_MIN_LEVEL=Info` (or `Quasar:Logging:MinimumLevel` to `Info`) and restart Quasar while you investigate. An unknown level name falls back to `Info`. The same level applies to the console when `QUASAR_CONSOLE_LOGGING=true` copies the log there, as native Linux units and the Docker image do.

Cluster update timings are the exception: they are written at every level. To see where an update spent its time, look for `update step` and `references reconciled` in `quasar.log`, and for `step '` in the Host's `host.log`, which also has the bundle load and data seed of each start.

Quasar does not rotate `quasar.log` or `discord.log`. Delete or truncate them yourself if they grow too large.

A fatal startup error is logged as "Quasar terminated unexpectedly." and printed to the console as `[Quasar:FATAL] <type>: <message>`.

Since 1.5 Quasar runs as the service itself, next to a separate updater service, with no launcher in between. When an update or restart goes wrong, read both: `quasar.log` for Quasar and the updater's journal or event log for the replacement. Legacy Bootstrap output matters only for an installation not migrated yet.

### Startup warnings about system limits

At every start Quasar checks limits that make it or its servers fail in ways that are hard to trace back, and writes one warning per limit into `quasar.log`. Each warning names the value, the expected minimum, what fails when it runs out and how to raise it.

| Platform | Limit | Expected at least |
| --- | --- | --- |
| Linux | Open files (`ulimit -n`) | 65536 |
| Linux | Processes and threads of the user (`ulimit -u`) | 16384 |
| Linux | `fs.inotify.max_user_instances` | 1024 |
| Linux | `fs.inotify.max_user_watches` | 524288 |
| Windows | Long path support (`LongPathsEnabled`) | 1 |

The checks only warn; a small setup may work with less. A limit Quasar can't read, as in some containers, is skipped. See [Install on Linux](InstallLinux.md#system-limit-warnings).

### Server logs in the UI

**Console** on a server card opens the log viewer with **Server log** and **Magnetar info log** tabs, a **Log file** selector, **Errors** (finds error lines with context), **Refresh** and a download button. It is read-only. Details are in [Servers](Servers.md#the-server-console). Quasar keeps the newest 5 `SpaceEngineersDedicated*.log` files per server by default (**Space Engineers DS logs to keep**, 1 to 1000) and deletes older ones at each start and stop. It does not delete Magnetar `info*.log` files.

For a crashed or faulted server, the Dashboard card shows the latest error line from these logs. Click it to jump there. See [Monitoring](Monitoring.md#latest-error-line).

## The agent never connects

A server counts as running only after Quasar.Agent inside it connects and sends a snapshot. Until then the card says **Starting** (or **Connecting** for an adopted process) with "Waiting for Quasar.Agent to attach."

How the wait works, per server (editor **Health Policy**, see [Servers](Servers.md#health-checks-and-the-watchdog)):

- **Agent startup log inactivity timeout** (default 60 s): if neither the DS log nor the Magnetar info log changed for this long, Quasar gives up: "Quasar.Agent did not attach; startup logs were inactive for 60s."
- **Agent startup hard limit** (default 600 s, 10 minutes): the absolute limit, even while logs keep changing: "Quasar.Agent did not attach within the 600s hard startup limit."
- For newly launched processes with **Auto-restart when unhealthy** on, Quasar kills the process and relaunches ("Quasar.Agent attach timed out (1/3). Retrying in 5s."). After **Agent attach retry attempts** (default 3) the server is **Faulted** with "Quasar.Agent did not attach after 3 attempt(s). ..."
- A connected agent that goes silent for **Agent heartbeat timeout** (default 20 s) makes the server Unhealthy: "Quasar.Agent heartbeat stale beyond 20s timeout."

The agent finds Quasar through `QUASAR_BASE_URL`, which Quasar sets for each server it launches. It is `QUASAR_PUBLIC_BASE_URL` when set, otherwise `http://<Quasar:Host>:<Quasar:Port>`, with a wildcard host (`0.0.0.0`) advertised as `127.0.0.1`. The agent checks `/api/health` there, then opens the WebSocket `/ws/agent`. If it cannot connect it retries every 10 seconds plus up to 3 seconds of jitter (`QUASAR_AGENT_RECONNECT_INTERVAL_SECONDS`, `QUASAR_AGENT_RECONNECT_JITTER_SECONDS`).

Quasar refuses an agent that has no valid agent token, or whose server already has a connected agent; see [Plugins](Plugins.md). The Quasar log then has "Refused an agent connection from … without an agent token.", "Refused agent connection …: the agent token does not match server …" or "… already has a connected agent.", and the agent logs "Quasar refused the connection: …" or a 401 connection error. A server started by an older Quasar needs a restart to load the current agent (see [Updates](Updates.md)).

After Quasar restarts, automatic attach/health/planned restart waits for authenticated running telemetry from an adopted process. This prevents killing a world without a save when an old agent is refused. A 403 on agent/Host routes means the original socket source, exact Host authority or Origin policy failed; forwarded headers do not grant access. Check [DIS-0042 migration and network policy](https://github.com/CometWorks/quasar/blob/main/Docs/SecurityUpgradeDIS0042.md) before manually restarting a disconnected server.

What to check:

1. The Magnetar info log: did Magnetar load its plugins and reach world load? A plugin or mod error, or a long Workshop download, keeps the world from loading. Big modded worlds may need a larger hard limit.
2. The DS log for a crash during load.
3. The process console log under `Diagnostics/runs/` for `[Quasar.Agent]` lines.
4. If you set `QUASAR_PUBLIC_BASE_URL` or `Quasar:Host` to a specific address, that address must be reachable from the DS process on the same machine.
5. After a Quasar update the card may say "Bundled Quasar.Agent differs from the deployed Magnetar local DLL; restart this server manually to load the bundled agent." Restart the server.

If Quasar itself stays down, a server that had connected keeps running and its agent saves and stops it after `QUASAR_AGENT_OFFLINE_SHUTDOWN_SECONDS` (default 3600). The process output then says "Quasar has been unreachable for N second(s); saving the world and stopping the server." See [Plugins](Plugins.md#when-quasar-is-unreachable).

## Server stuck starting or restarting in a loop

- **Stays Stopped with "Managed SteamCMD, Magnetar, and Dedicated Server runtime are still preparing."**: the managed runtime is still downloading or failed. Watch the **Managed Runtime** panel on the Dashboard and click **Retry** on a failed row. See [Updates](Updates.md#managed-runtime-warmup).
- **Faulted right after Start**: the card shows the reason, for example "World save required." or the oxygen guard message. The full table is in [Servers](Servers.md#launch-failures-that-fault-a-server).
- **Faulted with "More than one plugin hub lists ..."**: two plugin hubs list a plugin the profile selects, or a compatibility plugin Magnetar loads by itself. Remove it from one hub, or remove a hub. See [Plugins](Plugins.md#two-hubs-with-the-same-plugin-id).
- **Crashes, restarts, and ends Faulted with "Process exited with code N. Restart attempt limit (3) reached."**: the server crashes within a minute of each start. Read the DS log and the Magnetar info log of the last run. Quasar does not retry a Faulted server; click **Start** after fixing the cause, or **Clear Error Status**.
- **"Start failed: process exited with code N before the world was ready"**: the DS gave up loading the world and exited on its own. Quasar treats it like a crash: it restarts within the restart budget, then ends **Faulted** with "... Restart attempt limit (3) reached.", or **Crashed** when **Restart on crash** is off. The card shows the error line from the DS log, usually right above "Session can not start" in it. "Unable to download mods" means a Workshop mod of the world couldn't be downloaded; without Steam (DirectTransport, for example) turn on **Disable implicit Magnetar mod load** so Magnetar doesn't add its MagnetarMod companion to the world. Fix the cause, then click **Start**.
- **High process priority shows a warning and does nothing on Linux**: install or reinstall `/usr/local/bin/quasar-renice`. See [Servers](Servers.md#process-priority-and-cpu-affinity).
- **Restarts with cause "Health policy"**: health went Unhealthy (stale heartbeat, low simulation frame rate, agent disconnected). The health summary on the card says which. See [Servers](Servers.md#health-checks-and-the-watchdog).
- **Restarts every 8 hours**: **Maximum uptime** defaults to `08:00`. See [Servers](Servers.md#crash-detection-and-automatic-restart).
- **Server stops by itself and stays stopped**: someone used `!stop` or `!quit` in game, or a Discord `stop`. Both set the goal Off.

### Agent message rejection

If the agent reports **Invalid agent JSON**, **Agent message must use text frames**,
or a message-size limit, check `Logs/quasar.log`. The warning gives the connection
ID and the rejection category. JSON failures include only a line and byte position;
logs do not include the message body, credentials or JSON exception text.

The initial Hello is limited to 64 KiB and 30 seconds. After authentication, the
limit is 64 MiB of encoded UTF-8 per message, including base64 and nested JSON.
A command reply above that limit returns **Agent command response is too large**
to the caller while the agent stays connected. Reduce the requested scene or
response size. Increasing the 60-second startup inactivity timeout does not change
these limits.

Update Quasar and restart the server through the normal saved-world maintenance
flow to load the matching agent. Existing clusters also need to stage and apply
the bundled agent as described in [Cluster hosts](ClusterHosts.md).

## Port collisions

| Symptom | Cause and fix |
| --- | --- |
| Quasar stops at startup with `[Quasar:FATAL] IOException: Failed to bind to address ...` | Another program, or another Quasar, has the web port. Stop it or change `Quasar:Port` in `<data dir>/appsettings.json`. The service manager keeps restarting Quasar until the port is free. See [Configuration](Configuration.md#web-ui-address-and-port). |
| Updates fail their health check after a port change | The installer records Quasar's health address from `appsettings.json` (`Quasar:Port`, `Quasar:Host`) when it installs, and the updater checks that address. After changing the port, run the installer again with `--repair-supervision` (Linux) or `-RepairSupervision` (Windows), keeping the same paths and names. |
| Editor: "Port already used by server '<ID>'." | Two servers of this Quasar have the same game port. |
| Second server not reachable, or a DS fails to bind | Each server also uses game port + 1000 (Steam) and game port + 2000 (Remote API). Quasar does not check those against other servers or other programs. Never give two servers game ports that differ by exactly 1000 or 2000. Consecutive ports such as 27016, 27017, 27018 are safe. See [Servers](Servers.md#ports). |
| Players cannot connect | No firewall rule for the UDP game port. Neither Quasar nor its installers open firewall ports. |

## Dedicated Server download fails

The managed DS comes from SteamCMD (`+login anonymous +app_update 298740`, up to 3 attempts at first install).

- "SteamCMD is not installed and could not be downloaded.": Quasar could not fetch SteamCMD from Valve. Check internet access from the machine.
- "Space Engineers Dedicated Server is not installed and could not be downloaded.": SteamCMD failed 3 times. Check free disk space and network, then **Retry** on the Dashboard.
- On Linux a message ending in "SteamCMD is a 32-bit program and this host can't run it. Install its 32-bit libraries, on Debian and Ubuntu: sudo apt install lib32gcc-s1": SteamCMD exited with code 127, or the 32-bit loader `/lib/ld-linux.so.2` is missing. Install the package, then **Retry**. See [Install on Linux](InstallLinux.md#prerequisites).
- Only the SteamCMD row failed and the panel says "Magnetar and Dedicated Server ready. SteamCMD failed, so Quasar can't download or update the Dedicated Server.": a valid DS is already in place, for example through `QUASAR_DS_INSTALL_DIR`. Servers still start, but Quasar can't update the DS until SteamCMD works. Fix SteamCMD and click **Retry** on its row, or set **Dedicated Server update on launch** to **Skip**.
- "DedicatedServer64 path not found. Set QUASAR_DS64_PATH, ...": the DS install is missing and no override is set.
- Slow starts: **Dedicated Server update on launch** set to **Validate** re-checks every file on every start. Use **Update** or **Skip**.

See [Updates](Updates.md#dedicated-server-updates) and [Servers](Servers.md#managed-downloads-steamcmd-dedicated-server-magnetar).

## Workshop mods fail to download

- The card says "Mod download failure reported by server output." and **Live details** lists the failing lines. They clear at the next start or with **Clear Error Status**.
- Before every start Quasar repairs the server's `appworkshop_244850.acf` cache manifest. Entries for missing content are removed so Steam downloads only those items. An unreadable manifest is moved to `WorkshopCacheQuarantine/` in the DS app-data folder, and Steam rebuilds it. The Quasar log says "Repaired inconsistent Steam Workshop cache for server ..." or "Quarantined invalid Steam Workshop manifest for server ...".

See [Servers](Servers.md#steam-workshop-mods).

## .NET 10 missing

On Linux, `install.sh` checks for the .NET 10 runtime and the ASP.NET Core runtime and offers to install them with the system package manager:

- `.NET 10 ASP.NET Core runtime is required before installing Quasar.` followed by `No supported package manager found. ...`: install the runtime by hand first. See [Install on Linux](InstallLinux.md#prerequisites).
- `.NET 10 ASP.NET Core runtime is still missing after installation attempt.`: the package install did not provide it. The script prints the runtimes it found.

On Windows the packaged programs need no .NET installation, and `install.ps1` does not check for one. See [Install on Windows](InstallWindows.md#prerequisites).

UI plugins also need a .NET 10 SDK; see [UI plugins](UiPlugins.md#net-10-sdk).

## The web UI is not reachable

- Check the service runs: `systemctl --user status quasar.service` (system service: `systemctl status quasar.service`), `Get-Service Quasar` in PowerShell, or `docker compose ps`. If it keeps restarting, read the journal or `quasar.log` for the `[Quasar:FATAL]` line.
- On the Quasar machine, open `http://127.0.0.1:8080/api/health`. A JSON answer means Quasar runs.
- The shipped `Quasar:Host` is `0.0.0.0` (all interfaces) on port `8080`. If it is `127.0.0.1`, only local browsers can connect. Remote access also needs a firewall rule for the TCP port.
- Quasar exits at once with "Quasar 1.5 and later require a completed Quasar 1.4.1 migration first. ...": see [Self-update failures](#self-update-failures).
- "Cannot read this installation: ...": Quasar can't read `service-installation.json` or the protected file it points to. Run the command as the service account or from an elevated Windows shell, or repair the installation with its installer.
- A UI plugin breaks the web UI: start in safe boot by creating `ui-plugins.safe-mode` in the data directory, then restart Quasar. Quasar no longer turns safe boot on by itself after repeated start failures. See [UI plugins](UiPlugins.md#safe-boot).
- Quasar defaults to private LAN HTTP mode. If `Quasar:Auth:RequireHttpsForPublicAccess=true`, remote plain-HTTP requests return HTTP 400. Use that setting with an HTTPS reverse proxy that sends trusted `X-Forwarded-For`, `X-Forwarded-Host`, and `X-Forwarded-Proto` headers; loopback HTTP remains available for local checks. See [Access control](AccessControl.md#behind-a-reverse-proxy).
- `Quasar authentication can be disabled only on loopback listeners.` means `Auth:Enabled=false` was combined with a public `Quasar:Host`, `QUASAR_WEB_HOST`, or `ASPNETCORE_URLS` binding. Restore authentication or bind every listener to localhost/`127.0.0.1`/`::1`.

### "Rejoining the server..."

**Quasar connection unavailable** means the browser could not finish connecting.
The same warning appears after ten seconds if startup is still pending. Until
the connection succeeds, live updates and controls are unavailable, even if the
page already displays server data. Check your network connection, then click
**Reload page** to retry or sign in again. This button works even when the page
never becomes interactive. A successful connection clears the warning.

If an established connection drops, **Quasar connection interrupted** warns that
displayed data may be out of date. The dialog shows reconnect progress and always
offers **Reload page**. Hosted WebRTC and ordinary socket connections use the
same warnings. A silent connection stall is reported after the framework detects
it, so the warning may not appear immediately when traffic stops.

The UI keeps a live connection to Quasar. When it drops, a dialog shows "Rejoining the server...", then "Rejoin failed... trying again in N seconds.", then "Failed to rejoin. Please retry or reload the page." with **Retry**. During the disconnect, the page checks readiness and worker identity through the same address, including a Nebula panel prefix. When a replacement Quasar process is ready, the page reloads automatically instead of waiting for all circuit retries to fail. A temporary network interruption to the same process keeps normal reconnection behavior. A restart started from this browser shows its own progress panel (request, stop, health check, reload) instead of the dialog. If it never reconnects, reload the page and check that Quasar is still up.

## Sign-in problems

- "You are signed in, but this account has no Quasar role.": add the SteamID64 on **Security** or in `rbac.json`.
- Locked out: open the UI from the Quasar machine itself at `http://127.0.0.1:8080`, where loopback access is admin by default, or edit `rbac.json`.
- Steam sign-in returns to the wrong address behind a proxy: the proxy is not in `TrustedProxies` or does not send forwarded headers.

See [Access control](AccessControl.md#common-problems) and [First setup](FirstSetup.md).

## Self-update failures

Open the Updates page or run `Quasar update --status` to see the recorded update operation and its cause. A failed launch or an interrupted replacement restores the complete previous application. Fix the recorded cause, then run `Quasar update --retry`. When Quasar can't restart itself through the updater, the Updates page shows an alert about it. Current Windows installations run as two native services under virtual accounts; an old Scheduled Task installation has to be migrated first. See [Updates](Updates.md#when-an-update-fails).

The mandatory 1.4.1 intermediate-release gate is removed. If an older package reports that prerequisite, use the current complete package directly, preserving existing paths, accounts, settings, credentials and Data Protection keys. Native-service migration, package validation and recovery checks still apply; see [Updates](Updates.md#direct-installation-and-upgrades).

## Stopping Quasar

| How | Effect |
| --- | --- |
| Power button in the top bar, **Shutdown Quasar** (admin) | Saves server state and stops Quasar with exit code 0. The service manager does not restart it. Servers keep running unsupervised. |
| **Restart Quasar** in the same dialog | In a service installation, asks the updater service to restart Quasar; servers keep running. Grayed out with "Restart needs the Quasar launcher." in Docker and in a run without a service installation. |
| **Save and stop all servers** | Saves and stops standalone servers and managed clusters, keeping Quasar online. Shows a loader and per-target progress until completion or a visible failure. |
| Ctrl+C in a foreground run | Stops Quasar. |
| `kill <pid>` (SIGTERM, Linux) | Same as Ctrl+C. |
| `systemctl --user stop quasar.service` (or `sudo systemctl stop quasar.service`) | Same as Ctrl+C. The unit allows 120 seconds for the stop. |
| `Stop-Service Quasar` (Windows) | Same as Ctrl+C. |
| `docker compose down` or `stop` | Stops the container. The image stops the managed servers too. |

For **Save and stop all servers**, goals are set to Off so the stopped fleet stays down. Clusters use the normal graceful save/drain flow and wait for a clean, fenced Gateway stop. A failed or dirty cluster is reported rather than force-killed. Other stop requests still proceed. The Off goal remains active after a waiting timeout; check the cluster page before retrying. Observed-only clusters are excluded.

An open browser tab doesn't hold up the stop: Quasar closes the web server after a 5-second drain and then stops the rest.

By default (`Quasar:PreserveManagedServersOnShutdown` `true`) managed servers keep running when Quasar stops and Quasar adopts them again on the next start. During an application update or restart by the updater, servers are kept even when that setting is `false`. On Linux the clusters unit (`quasar-clusters.service`) saves and stops this PC's clusters at logout or machine shutdown; a plain Quasar stop leaves them running. See [Cluster hosts](ClusterHosts.md#systemd-units). Use **Save and stop all servers** first if you want them down. Details are in [Install on Linux](InstallLinux.md#stopping-and-the-shutdown-quasar-action), [Install on Windows](InstallWindows.md#shutdown-quasar-and-other-power-actions) and [Docker](Docker.md).

## Changing the data directory

Quasar keeps all state under one data directory. An installed copy takes it from `service-installation.json` next to the executable (or the protected file that one points to), which the installer writes. Otherwise Quasar uses `QUASAR_INSTALL_DIR`, then a `.quasar-install-dir` file (or the file named by `QUASAR_INSTALL_DIR_FILE`), then the program's folder. On Linux the data directory is the install folder by default; a new Windows service installation keeps program files in `%ProgramFiles%\Quasar` and data in `%ProgramData%\Quasar`. To move it, stop Quasar and its servers, copy the whole folder and rerun the installer with the new folder; see [Install on Linux](InstallLinux.md#move-the-install-root) or [Install on Windows](InstallWindows.md#move-the-install-root).

Some parts can live elsewhere on their own:

| Part | Setting |
| --- | --- |
| Logs | `QUASAR_LOG_DIR` or `Quasar:Logging:Directory` |
| Configuration backups | `QUASAR_BACKUP_DIR`, `Quasar:BackupDirectory` or the **Backups** page, see [Backups](Backups.md) |
| Diagnostic capture | `QUASAR_DIAGNOSTICS_DIRECTORY`, see [Diagnostics](Diagnostics.md#storage-on-a-separate-disk) |
| Managed DS, SteamCMD, Magnetar | `Quasar:ManagedRuntime` keys, see [Servers](Servers.md#managed-downloads-steamcmd-dedicated-server-magnetar) |
| One server's files | **Paths** section of the server editor |

## Two Quasar instances on one machine

The code has no single-instance lock across data directories. To run two:

- Give each its own install folder and data directory.
- Give each its own `Quasar:Port` in its own `appsettings.json` before installing it. A shared port fails with the "Failed to bind" error above.
- Install each under its own service name: `install.sh --service-name <name>` (units `<name>.service`, `<name>-updater.service` and `<name>-clusters.service`) or `install.ps1 -ServiceName <name>` (updater `<name>Updater` unless `-UpdaterServiceName` is given).
- Assign game ports by hand. Each Quasar picks new ports from 27016 upward and checks only its own servers, so two instances hand out the same ports. Keep the + 1000 and + 2000 ports free as well.

Each server's agent connects to the Quasar that launched it, because that Quasar passes its own `QUASAR_BASE_URL`. The Docker Compose file uses host networking, so a container and a native install on the same machine share the port space too.

## Sources
- `Docs/SecurityUpgradeDIS0042.md`
- `Docs/StateMachines/AgentConnection.md`
- `Docs/StateMachines/BlazorReconnect.md`
- `Docs/StateMachines/DedicatedServerLifecycle.md`
- `Docs/Configuration.md`
- `Docs/Docker.md`
- `Docs/LinuxDeploymentAndUpdates.md`
- `Dockerfile`
- `docker-compose.yml`
- `install.sh`
- `scripts/install.ps1`
- `Quasar/appsettings.json`
- `Quasar/Program.cs`
- `Quasar/Services/QuasarLoggingConfigurator.cs`
- `Quasar/Services/WebServiceOptions.cs`
- `Quasar/Services/DedicatedServerSupervisor.cs`
- `Quasar/Services/ServerManagementActions.cs`
- `Quasar/Services/DedicatedServerRuntimePreparer.cs`
- `Quasar/Services/ManagedRuntimeWarmupService.cs`
- `Quasar/Services/Auth/QuasarAuthOptions.cs`
- `Quasar/Services/Auth/PublicAccessSecurity.cs`
- `Quasar/Models/DedicatedServerDefinition.cs`
- `Quasar/Components/Pages/ServerConsoleDialog.razor`
- `Quasar/Components/Pages/ServerEditorDialog.razor`
- `Quasar/Components/Dashboard/ServerCard.razor`
- `Quasar/Components/Layout/ReconnectModal.razor`
- `Quasar/Components/Layout/ReconnectModal.razor.js`
- `Quasar/Components/Layout/ReconnectModal.razor.css`
- `Quasar/wwwroot/_quasar/sdk/v1/boot.js`
- `Quasar/Services/WebServiceProbeEndpoints.cs`
- `Quasar.Agent/AgentConnection.cs`
- `Quasar/Services/AgentSocketHandler.cs`
- `Magnetar.Protocol/Transport/AgentMessageLimits.cs`
- `Quasar.Agent/WebServiceLocator.cs`
- `Shared/Diagnostics/DiagnosticCollector.cs`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
- `Quasar/Services/QuasarShutdownService.cs`
- `Quasar/Services/ClusterCommandService.cs`
- `Quasar/Services/ClusterReconciler.cs`
- `Quasar/Components/Shared/QuasarOperationLoader.razor`
- `Docs/QuasarUpdates.md`
- `Quasar.Updater/QuasarUpdater.cs`
- `Shared/Updates/GitHubUpdateTokenReader.cs`
- `Shared/ServiceHosting/NativeServiceFailures.cs`
- `Shared/Updates/NativeQuasarServices.cs`
- `Docs/Installation.md`
- `scripts/install-gui.sh`
- `scripts/install-gui.ps1`
- `Quasar.Updater/Program.cs`
- `Quasar.Updater/ConfigureInstallation.cs`
- `Shared/Updates/QuasarServiceInstallation.cs`
- `Shared/Updates/BootstrapUpdateSupport.cs`
- `Shared/Diagnostics/SystemLimits.cs`
- `Shared/Diagnostics/LinuxLimits.cs`
- `Quasar/Services/ManagedDedicatedServerRuntimeResolver.cs`
- `Quasar/Services/QuasarApplicationEntry.cs`
- `Quasar/Components/Layout/QuasarControlDialog.razor`
- `Quasar/wwwroot/quasar-configs.js`
- `Quasar/Services/Plugins/QuasarUiPluginCatalog.cs`
- `Tests/Updates/service_unit_config.py`
- `Quasar.Tests/QuasarLoggingConfiguratorTests.cs`
- `Docs/BuildingAndDevelopment.md`
- `Tests/Updates/NativeFixture.cs`
- `Tests/Updates/windows-services.ps1`
