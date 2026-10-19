# Troubleshooting

This page lists where Quasar and its servers write logs, and the common failures with what to check first. Details live on the topic pages linked from each section. Error reporting to CometWorks is covered in [Diagnostics](Diagnostics.md).

## Where the logs are

`<data dir>` below is the Quasar data directory (install root): `QUASAR_INSTALL_DIR`, for example `~/.local/share/Quasar` on Linux, the extracted `Quasar` folder on Windows, `/data` in Docker (`./quasar-data` on the host). See [Configuration](Configuration.md#where-files-live).

| Log | Location | Contents |
| --- | --- | --- |
| Quasar log | `<data dir>/Logs/quasar.log` | The web worker and supervisor: server starts, faults, health restarts, update and download errors |
| Discord log | `<data dir>/Logs/discord.log` | Discord bot messages only. They also go to `quasar.log`. |
| systemd journal (Linux service) | `journalctl --user -u quasar.service` (system install: `sudo journalctl -u quasar.service`) | The worker's console output, which the launcher forwards, and launcher errors |
| Foreground console | the terminal running `./Quasar serve` or `Quasar.exe` | Same as the journal, plus launcher progress lines |
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

At the default `Warn`, Info messages such as server exit codes and "recovered after restart" lines are not written. Set `QUASAR_LOG_MIN_LEVEL=Info` (or `Quasar:Logging:MinimumLevel` to `Info`) and restart Quasar while you investigate. An unknown level name falls back to `Info`. The same level applies to the console and journal output.

Quasar does not rotate `quasar.log` or `discord.log`. Delete or truncate them yourself if they grow too large.

A fatal startup error is logged as "Quasar terminated unexpectedly." and printed to the console as `[Quasar:FATAL] <type>: <message>`.

The launcher (Bootstrap) has no log file of its own. Installed services run it with `--quiet`, so only its error lines, its first-start download retries and the forwarded worker output reach the journal. On Windows the Scheduled Task `Quasar` has no visible console, so read `Logs\quasar.log`.

### Server logs in the UI

**Console** on a server card opens the log viewer with **Server log** and **Magnetar info log** tabs, a **Log file** selector, **Errors** (finds error lines with context), **Refresh** and a download button. It is read-only. Details are in [Servers](Servers.md#the-server-console). Quasar keeps the newest 5 `SpaceEngineersDedicated*.log` files per server by default (**Space Engineers DS logs to keep**, 1 to 1000) and deletes older ones at each start and stop. It does not delete Magnetar `info*.log` files.

For a crashed or faulted server, the Dashboard card shows the latest error line from these logs. Click it to jump there. See [Monitoring](Monitoring.md#latest-error-line).

## The agent never connects

A server counts as running only after Quasar.Agent inside it connects and sends a snapshot. Until then the card says **Starting** (or **Connecting** for an adopted process) with "Waiting for Quasar.Agent to attach."

How the wait works, per server (editor **Health Policy**, see [Servers](Servers.md#health-checks-and-the-watchdog)):

- **Agent startup log inactivity timeout** (default 60 s): if neither the DS log nor the Magnetar info log changed for this long, Quasar gives up: "Quasar.Agent did not attach; startup logs were inactive for 60s."
- **Agent startup hard limit** (default 600 s, 10 minutes): the absolute limit, even while logs keep changing: "Quasar.Agent did not attach within the 600s hard startup limit."
- With **Auto-restart when unhealthy** on, Quasar kills the process and relaunches ("Quasar.Agent attach timed out (1/3). Retrying in 5s."). After **Agent attach retry attempts** (default 3) the server is **Faulted** with "Quasar.Agent did not attach after 3 attempt(s). ..."
- A connected agent that goes silent for **Agent heartbeat timeout** (default 20 s) makes the server Unhealthy: "Quasar.Agent heartbeat stale beyond 20s timeout."

The agent finds Quasar through `QUASAR_BASE_URL`, which Quasar sets for each server it launches. It is `QUASAR_PUBLIC_BASE_URL` when set, otherwise `http://<Quasar:Host>:<Quasar:Port>`, with a wildcard host (`0.0.0.0`) advertised as `127.0.0.1`. The agent checks `/api/health` there, then opens the WebSocket `/ws/agent`. If it cannot connect it retries every 10 seconds plus up to 3 seconds of jitter (`QUASAR_AGENT_RECONNECT_INTERVAL_SECONDS`, `QUASAR_AGENT_RECONNECT_JITTER_SECONDS`).

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
- **Ends Faulted with "Start failed: process exited with code N before the world was ready."**: the DS gave up loading the world, every time. The card shows the error line from the DS log, usually right above "Session can not start" in it. "Unable to download mods" means a Workshop mod of the world couldn't be downloaded; without Steam (DirectTransport, for example) turn on **Disable implicit Magnetar mod load** so Magnetar doesn't add its MagnetarMod companion to the world. Fix the cause, then click **Start**.
- **Restarts with cause "Health policy"**: health went Unhealthy (stale heartbeat, low simulation frame rate, agent disconnected). The health summary on the card says which. See [Servers](Servers.md#health-checks-and-the-watchdog).
- **Restarts every 8 hours**: **Maximum uptime** defaults to `08:00`. See [Servers](Servers.md#crash-detection-and-automatic-restart).
- **Server stops by itself and stays stopped**: someone used `!stop` or `!quit` in game, or a Discord `stop`. Both set the goal Off.

## Port collisions

| Symptom | Cause and fix |
| --- | --- |
| `Error: port 8080 is already bound by another process and is not a healthy Quasar server.` | Another program has the Quasar web port. Stop it or change `Quasar:Port` in `<data dir>/appsettings.json`. See [Configuration](Configuration.md#web-ui-address-and-port). |
| Launcher says the worker never became healthy after a port change | The port was changed with `QUASAR_WEB_PORT` or `ASPNETCORE_URLS`. The launcher reads only `appsettings.json`. |
| Editor: "Port already used by server '<ID>'." | Two servers of this Quasar have the same game port. |
| Second server not reachable, or a DS fails to bind | Each server also uses game port + 1000 (Steam) and game port + 2000 (Remote API). Quasar does not check those against other servers or other programs. Never give two servers game ports that differ by exactly 1000 or 2000. Consecutive ports such as 27016, 27017, 27018 are safe. See [Servers](Servers.md#ports). |
| Players cannot connect | No firewall rule for the UDP game port. Neither Quasar nor its installers open firewall ports. |

## Dedicated Server download fails

The managed DS comes from SteamCMD (`+login anonymous +app_update 298740`, up to 3 attempts at first install).

- "SteamCMD is not installed and could not be downloaded.": Quasar could not fetch SteamCMD from Valve. Check internet access from the machine.
- "Space Engineers Dedicated Server is not installed and could not be downloaded.": SteamCMD failed 3 times. Check free disk space and network, then **Retry** on the Dashboard.
- "DedicatedServer64 path not found. Set QUASAR_DS64_PATH, ...": the DS install is missing and no override is set.
- Slow starts: **Dedicated Server update on launch** set to **Validate** re-checks every file on every start. Use **Update** or **Skip**.

See [Updates](Updates.md#dedicated-server-updates) and [Servers](Servers.md#managed-downloads-steamcmd-dedicated-server-magnetar).

## Workshop mods fail to download

- The card says "Mod download failure reported by server output." and **Live details** lists the failing lines. They clear at the next start or with **Clear Error Status**.
- Before every start Quasar repairs the server's `appworkshop_244850.acf` cache manifest. Entries for missing content are removed so Steam downloads only those items. An unreadable manifest is moved to `WorkshopCacheQuarantine/` in the DS app-data folder, and Steam rebuilds it. The Quasar log says "Repaired inconsistent Steam Workshop cache for server ..." or "Quarantined invalid Steam Workshop manifest for server ...".

See [Servers](Servers.md#steam-workshop-mods).

## .NET 10 missing

Quasar needs the .NET 10 runtime (the ASP.NET Core runtime on Linux). The installers check it:

- Linux: `.NET 10 ASP.NET Core runtime is still missing after installation attempt.` See [Install on Linux](InstallLinux.md#prerequisites).
- Windows: `.NET 10 runtime is required before installing Quasar.` See [Install on Windows](InstallWindows.md#prerequisites).

UI plugins also need a .NET 10 SDK; see [UI plugins](UiPlugins.md#net-10-sdk).

## The web UI is not reachable

- Check the service runs: `systemctl --user status quasar.service`, `Get-ScheduledTask -TaskName Quasar`, or `docker compose ps`.
- On the Quasar machine, open `http://127.0.0.1:8080/api/health`. A JSON answer means the worker runs.
- The shipped `Quasar:Host` is `0.0.0.0` (all interfaces) on port `8080`. If it is `127.0.0.1`, only local browsers can connect. Remote access also needs a firewall rule for the TCP port.
- A fresh install starts the UI only after Bootstrap has downloaded the first UI release from GitHub. Until then it retries and logs "Failed downloading the Quasar web UI; retrying at <time> ...", or "GitHub's API rate limit is used up, ..." when the anonymous limit of the public IP address is spent, in which case it waits for GitHub's reset time, unless a token is set in `QUASAR_GITHUB_TOKEN` or `GITHUB_TOKEN`. `QUASAR_UPDATES_ENABLED=false` blocks this first download; a foreground run then prints "Quasar has no running worker, so the web UI is not available." See [Install on Linux](InstallLinux.md#common-problems).
- After 3 worker start failures within 5 minutes, Bootstrap starts the UI once more with UI plugins disabled. See [UI plugins](UiPlugins.md#safe-boot).
- `Quasar:Auth:RequireHttpsForPublicAccess` is in `appsettings.json` but the current code does not act on it. Put an HTTPS reverse proxy in front of Quasar yourself for access from untrusted networks; see [Access control](AccessControl.md#behind-a-reverse-proxy).

### "Rejoining the server..."

The UI keeps a live connection to the worker. When it drops, a dialog shows "Rejoining the server...", then "Rejoin failed... trying again in N seconds.", then "Failed to rejoin. Please retry or reload the page." with **Retry**. This is expected for a few seconds after a Quasar update or a **Restart Quasar**, because the worker process changes. If it never reconnects, reload the page and check that the worker is still up.

## Sign-in problems

- "You are signed in, but this account has no Quasar role.": add the SteamID64 on **Security** or in `rbac.json`.
- Locked out: open the UI from the Quasar machine itself at `http://127.0.0.1:8080`, where loopback access is admin by default, or edit `rbac.json`.
- Steam sign-in returns to the wrong address behind a proxy: the proxy is not in `TrustedProxies` or does not send forwarded headers.

See [Access control](AccessControl.md#common-problems) and [First setup](FirstSetup.md).

## Self-update failures

Read the status line at the top of **Updates**. Bootstrap rolls back a UI release that fails its health check within 60 seconds ("Rolled Quasar worker back to version <v>."). Checksum errors ("SHA256 mismatch for <file>.") mean the download did not match; retry later. HTTP 403 or 429 from GitHub means a rate limit; add a GitHub token. Docker has no in-app updates. See [Updates](Updates.md#when-an-update-fails).

## Stopping Quasar

| How | Effect |
| --- | --- |
| Power button in the top bar, **Shutdown Quasar** (admin) | Saves server state, stops the worker and the launcher with exit code 0. The service manager does not restart it. Servers keep running unsupervised. |
| **Restart Quasar** in the same dialog | Restarts only the worker. Not available in Docker. |
| **Save and stop all servers** | Stops every server; Quasar keeps running. |
| Ctrl+C in a foreground run | Stops the launcher and worker. |
| `kill <launcher pid>` (SIGTERM, Linux) | Same as Ctrl+C. |
| `systemctl --user stop quasar.service` (or `sudo systemctl stop quasar.service`) | Same as Ctrl+C. |
| `Stop-ScheduledTask -TaskName Quasar` | Ends the launcher process. |
| `docker compose down` or `stop` | Stops the container. The image stops the managed servers too. |

By default (`Quasar:PreserveManagedServersOnShutdown` `true`) managed servers keep running when Quasar stops and Quasar adopts them again on the next start. Use **Save and stop all servers** first if you want them down. Details are in [Install on Linux](InstallLinux.md#stopping-and-the-shutdown-quasar-action), [Install on Windows](InstallWindows.md#shutdown-quasar-and-other-power-actions) and [Docker](Docker.md).

## Changing the data directory

Quasar keeps all state under one data directory, resolved from `QUASAR_INSTALL_DIR`, then a `.quasar-install-dir` file (or the file named by `QUASAR_INSTALL_DIR_FILE`), then the program's folder. There is no other data directory variable. To move it, stop Quasar and its servers, copy the whole folder and rerun the installer with the new folder; see [Install on Linux](InstallLinux.md#move-the-install-root) or [Install on Windows](InstallWindows.md#move-the-install-root).

Some parts can live elsewhere on their own:

| Part | Setting |
| --- | --- |
| Logs | `QUASAR_LOG_DIR` or `Quasar:Logging:Directory` |
| Configuration backups | `QUASAR_BACKUP_DIR`, `Quasar:BackupDirectory` or the **Backups** page, see [Backups](Backups.md) |
| Diagnostic capture | `QUASAR_DIAGNOSTICS_DIRECTORY`, see [Diagnostics](Diagnostics.md#storage-on-a-separate-disk) |
| Managed DS, SteamCMD, Magnetar | `Quasar:ManagedRuntime` keys, see [Servers](Servers.md#managed-downloads-steamcmd-dedicated-server-magnetar) |
| One server's files | **Paths** section of the server editor |

## Two Quasar instances on one machine

The code has no single-instance lock across data directories. Each launcher looks for a running Quasar only through `service-manifest.json` in its own data directory. To run two:

- Give each its own data directory.
- Give each its own `Quasar:Port` in its own `appsettings.json`. A shared port fails with the "already bound" error above.
- Install each service under its own name: `install.sh --service-name <name>` or `install.ps1 -TaskName <name>`.
- Assign game ports by hand. Each Quasar picks new ports from 27016 upward and checks only its own servers, so two instances hand out the same ports. Keep the + 1000 and + 2000 ports free as well.

Each server's agent connects to the Quasar that launched it, because that Quasar passes its own `QUASAR_BASE_URL`. The Docker Compose file uses host networking, so a container and a native install on the same machine share the port space too.

## Sources
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
- `Quasar/Models/DedicatedServerDefinition.cs`
- `Quasar/Components/Pages/ServerConsoleDialog.razor`
- `Quasar/Components/Pages/ServerEditorDialog.razor`
- `Quasar/Components/Dashboard/ServerCard.razor`
- `Quasar/Components/Layout/ReconnectModal.razor`
- `Quasar/Components/Layout/ReconnectModal.razor.js`
- `Quasar.Bootstrap/Program.cs`
- `Quasar.Agent/AgentConnection.cs`
- `Quasar.Agent/WebServiceLocator.cs`
- `Shared/Diagnostics/DiagnosticCollector.cs`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
