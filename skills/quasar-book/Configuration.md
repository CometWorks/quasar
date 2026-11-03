# Configuration

This page covers how Quasar itself is configured: where its config and state files live, the `appsettings.json` keys an admin is likely to change, environment variables, the `Quasar` command line, and which changes need a restart. Settings for single features are on their own pages, linked below.

## Where files live

Quasar keeps its state in one data directory. Quasar resolves it in this order:

1. The installation record of a service install (`service-installation.json` next to the program, or the protected record it points to). Quasar sets `QUASAR_INSTALL_DIR` to the recorded data directory.
2. `QUASAR_INSTALL_DIR` environment variable. The Linux unit and the Docker image set it.
3. A `.quasar-install-dir` file (or the file named by `QUASAR_INSTALL_DIR_FILE`) holding a path. This is for development runs.
4. The folder of the running program.

| Install | Data directory |
| --- | --- |
| Linux | The install directory: the folder you extracted and ran `install.sh` from, the `--install-dir` folder, or `~/.local/share/Quasar` for the guided installer |
| Windows, guided install | The data folder chosen in the installer, `%ProgramData%\Quasar` by default. The application lives in `%ProgramFiles%\Quasar`. |
| Windows, `install.ps1` | `-DataDir`; without it the application folder, for example `C:\quasar\Quasar` |
| Foreground run | The extracted folder |
| Docker | `/data` in the container, `./quasar-data` on the host with the shipped Compose file |

Files and folders in the data directory:

| Path | Contents |
| --- | --- |
| `appsettings.json` | Main settings (this page) |
| `rbac.json` | Role mappings, see [Access control](AccessControl.md#the-rbacjson-file) |
| `data-handling-consent.json` | Data-sharing decision, see [Diagnostics](Diagnostics.md) |
| `discord.json` | Discord bot settings, see [Monitoring](Monitoring.md) |
| `steam-workshop.json`, `github-updates.json` | Stored Workshop and GitHub credentials (encrypted) |
| `branding.json`, `Branding/` | Appearance settings and uploaded logos |
| `backup-settings.json`, `Backups/` | Backup settings and stored backups, see [Backups](Backups.md) |
| `dev-folders.json` | Magnetar plugin dev folders, see [Plugins](Plugins.md) |
| `plugin-hubs.json` | Extra Magnetar plugin hubs, see [Plugins](Plugins.md#plugin-hubs) |
| `known-players.json`, `known-player-settings.json` | Player list data |
| `PushNotifications.json` | Browser push subscriptions |
| `Magnetars/<server>/` | One folder per server: `server.json`, `DedicatedServer/`, `Magnetar/`, `History/`, `analytics.jsonl` |
| `ConfigProfiles/`, `WorldTemplates/` | Config profiles and world templates, see [Config profiles](ConfigProfiles.md) |
| `Clusters/`, `Operations/` | Cluster definitions and durable cluster operations, see [Clusters](Clusters.md) |
| `ManagedRuntime/Tools/` | Downloaded `SteamCMD`, `SteamCmdHome`, `SpaceEngineersDedicatedServer` and `Magnetar` |
| `ManagedRuntime/WebService/<version>/` | Web worker releases left from a Bootstrap install |
| `Updates/` | Update status (`operation.json`), the 1.4.1 migration receipt (`migration-1.4.1.json`) and staged packages, see [Updates](Updates.md) |
| `Logs/` | `quasar.log` and `discord.log` |
| `Diagnostics/` | Diagnostic capture, see [Diagnostics](Diagnostics.md) |
| `DataProtection-Keys/` | Keys that encrypt sign-in cookies and stored secrets. Back them up with the rest; losing them signs everyone out and makes stored credentials unreadable. |
| `service-manifest.json` | URL and process of the running Quasar, used by the CLI and the installers |
| `supervisor-state.json` | Supervisor state of managed servers |

The application files (`Quasar`, `Updater/`, `Host/`, `wwwroot/`) live in the install directory, which is the same folder on Linux and can be a separate one on Windows. See [Install on Linux](InstallLinux.md#where-quasar-keeps-its-files) and [Install on Windows](InstallWindows.md#where-quasar-keeps-its-files).

Back up the whole data directory as one unit (see [Backups](Backups.md)).

## How Quasar reads its settings

Quasar is one program (`Quasar` on Linux, `Quasar.exe` on Windows) that runs the web UI and the server supervisor. It loads, from lowest to highest priority:

1. `appsettings.json` and `appsettings.<Environment>.json` next to the program, in the current folder and in a `WebService` folder next to the program. A development run inside the source tree also reads the files of the `Quasar` project folder. Folders above the program are not read, so another Quasar install further up, such as `~/Quasar`, does not change this one.
2. `appsettings.json` and `appsettings.<Environment>.json` in the data directory. This is the file you edit.
3. Environment variables in .NET form, with `__` as the section separator: `QUASAR__AUTH__ENABLED=false` sets `Quasar:Auth:Enabled`.
4. Command-line arguments in .NET form, for example `--Quasar:Port=9000`.

On top of that, many settings have a short `QUASAR_*` variable that Quasar checks first and that wins over every other source (table under [Environment variables](#environment-variables)).

The updater service doesn't read `appsettings.json` at run time. The installer copies what it needs into the installation record: the health address from `Quasar:Host` and `Quasar:Port` (or `QUASAR_WEB_HOST` and `QUASAR_WEB_PORT` at install time), and the release repository and package name from `Quasar:Updates`. Rerun the installer's repair mode after changing those keys (see [Web UI address and port](#web-ui-address-and-port)).

Quasar updates keep your `appsettings.json`. When a new release changes defaults, Quasar merges them into your file; a conflict is shown on the **Updates** page for you to resolve. The installers keep an existing `appsettings.json` too. See [Updates](Updates.md).

## When changes apply

Quasar reads `appsettings.json` and environment variables once at startup and doesn't watch the file. After editing either, restart Quasar:

- Any install: **Restart Quasar** in the **Quasar power** dialog (service installs only)
- Linux user service: `systemctl --user restart quasar.service` (system service: `sudo systemctl restart quasar.service`)
- Windows: `Restart-Service Quasar` in an elevated PowerShell
- Docker: `docker compose up -d` after editing `.env`, or `docker compose restart` after editing `quasar-data/appsettings.json`
- Foreground: Ctrl+C, then start again

Changes that apply without a restart:

| Change | Where |
| --- | --- |
| Role mappings | `rbac.json` or **Security** page |
| Trust loopback, trust same subnet | **Security** page (trusted proxy changes need a restart) |
| Data handling consent | **Security** page or `data-handling-consent.json` |
| Discord settings | **Discord** page or `discord.json` |
| Backup folder | **Backups** page (a hand edit of `Quasar:BackupDirectory` needs a restart) |
| Automatic update and prerelease toggles | **Updates** page |

Settings passed to Quasar.Agent inside a server (`Agent*` keys) need a Quasar restart and then apply to each server on its next start.

## Web UI address and port

| Key | Default | Meaning |
| --- | --- | --- |
| `Quasar:Host` | `0.0.0.0` | Interface the UI listens on. `0.0.0.0`, `*`, `+` or `[::]` mean all interfaces. Use `127.0.0.1` for local access only. |
| `Quasar:Port` | `8080` | TCP port of the UI and API |

To change the port, edit `Quasar:Port` in the data directory's `appsettings.json`:

```json
{
  "Quasar": {
    "Host": "0.0.0.0",
    "Port": 9000
  }
}
```

A service install also has to tell the updater, which checks Quasar's health at the address in the installation record. Stop both services and run the installer's repair mode with the existing paths, names and scope, then start them:

- Linux: `./install.sh --repair-supervision --start` (system services: `sudo ./install.sh --system --user <account> --repair-supervision --start`), plus `--install-dir` and `--service-name` if they differ from the defaults.
- Windows: `.\install.ps1 -RepairSupervision -InstallDir <application> -DataDir <data> -Start` in an elevated PowerShell.
- Foreground run: stop it (Ctrl+C) and start it again.

Prefer `appsettings.json` over `QUASAR_WEB_HOST`, `QUASAR_WEB_PORT` and `ASPNETCORE_URLS` for a service install: an override that only the main service sees leaves the updater checking the old address, so updates and **Restart Quasar** can't confirm the new process. The Docker image listens through `ASPNETCORE_URLS=http://+:8080`; see [Docker](Docker.md).

When the host is a wildcard, Quasar advertises `http://127.0.0.1:<port>`. This URL is printed at startup in a foreground run, written to `service-manifest.json`, and given to Quasar.Agent in each managed server. Set `QUASAR_PUBLIC_BASE_URL` to advertise another URL.

Port `8080` is also the Space Engineers DS Remote API default. Quasar sets each managed server's Remote API port to its game port plus 2000, so they do not collide by default.

## Run mode, headless mode and browser launch

| Key | Env | Default | Meaning |
| --- | --- | --- | --- |
| `Quasar:Mode` | `QUASAR_MODE` | `Console` | `service` for installed services. A service install sets it from the installation record. |
| `Quasar:Headless` | `QUASAR_HEADLESS` | `false` | API-only mode |
| `Quasar:OpenBrowserOnStart` | `QUASAR_OPEN_BROWSER_ON_START` | `true` | Opens a browser on start; only in console mode, not headless |

### Headless mode

Headless mode runs the supervisor, background jobs, HTTP APIs and the agent socket `/ws/agent`, but no web UI, UI plugins or branding assets, and no `/login`. `GET /` returns a small JSON document. Server definitions, profiles and templates are the same in both modes, so you can switch back and forth.

Start it with `Quasar serve --headless`, with `QUASAR_HEADLESS=true`, or with `Quasar:Headless: true` in `appsettings.json`. `--headless` becomes the command-line setting `--Quasar:Headless=true`, so `QUASAR_HEADLESS` wins over it; `--no-headless` and `--headless=<true|false>` also work. For a service, set the key in `appsettings.json`, because the service command line is fixed.

Authentication stays on in headless mode; see [Access control](AccessControl.md).
With current-policy YES and diagnostic-report consent, headless Quasar polls
CometWorks announcements and writes eligible notices to its existing service logs.
Browser push services remain disabled. Headless log receipts survive restart and
regrant, retain up to 4,096 IDs until their original expiry, and pause new logging
at capacity or on invalid state. See
[CometWorks announcements](Diagnostics.md#cometworks-announcements) for limits.

Health endpoints, available in both modes:

- `GET /api/health`: process liveness; returns only `{"status":"ok"}`.
- `GET /api/ready`: returns only `status`, with 200 when the cluster operation store is ready and 503 otherwise.
- `GET /api/discovery`: process identity for local stale-process checks; restricted by the machine endpoint policy.
- `GET /api/version`: running version for the update overlay, requires dashboard read access when authentication is enabled and is not cached.
- `GET /api/internal/update-health`: process ID, version and start time for the updater and the installers. Answers only local callers that send the installation's token.

### Browser launch

The installed services never open a browser: the installation record switches them to service mode with `QUASAR_OPEN_BROWSER_ON_START=false`. A console run (`Quasar serve --foreground`, or `Quasar serve` without an installation record) opens the UI in the default browser after it starts, if a desktop session is present: always on Windows when interactive, on Linux when `DISPLAY` or `WAYLAND_DISPLAY` is set. It also prints `Quasar` and the URL. `--foreground` forces console mode and browser launch even in an installed folder.

## Logging

| Key | Env | Default | Meaning |
| --- | --- | --- | --- |
| `Quasar:Logging:Directory` | `QUASAR_LOG_DIR` | `<data>/Logs` | Folder for `quasar.log` and `discord.log` |
| `Quasar:Logging:Format` | `QUASAR_LOG_FORMAT` | `text` | `text` or `json` |
| `Quasar:Logging:MinimumLevel` | `QUASAR_LOG_MIN_LEVEL` | `Warn` (`Info` in Development) | `Trace`, `Debug`, `Info`, `Warn`, `Error`, `Fatal` |

`discord.log` gets the Discord bot's messages. Cluster update step timings go to `quasar.log` at Info even when the level is `Warn`. Quasar writes its log to the console when `QUASAR_CONSOLE_LOGGING=true` (or `1`). Linux native units and the Docker image set it, so the systemd journal and `docker compose logs` receive the same configured log level and format as `quasar.log`. Windows main-service messages remain in `quasar.log`; the updater uses the Application event log. Existing Linux installs need `install.sh --repair-supervision` with their existing paths, service name, account and scope, then a service restart, to enable the journal copy. Server logs (DS and Magnetar) are in each server's folder; see [Troubleshooting](Troubleshooting.md).

## Shutdown and agent behavior

| Key | Env | Default | Meaning |
| --- | --- | --- | --- |
| `Quasar:PreserveManagedServersOnShutdown` | `QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN` | `true` (Docker image: `false`) | Leave managed servers running when Quasar stops and adopt them again on start. Updates, supported **Restart Quasar** requests and **Shutdown Quasar** always leave them running. Unsupported restarts leave Quasar running too. |
| `Quasar:AgentOfflineShutdownSeconds` | `QUASAR_AGENT_OFFLINE_SHUTDOWN_SECONDS` | `3600` | How long a server keeps running after Quasar.Agent loses contact with Quasar before it saves and stops. Zero or less stops promptly. Only after the agent connected once; not for cluster nodes. |
| `Quasar:AgentReconnectIntervalSeconds` | `QUASAR_AGENT_RECONNECT_INTERVAL_SECONDS` | `10` | Agent reconnect delay |
| `Quasar:AgentReconnectJitterSeconds` | `QUASAR_AGENT_RECONNECT_JITTER_SECONDS` | `3` | Random jitter on the reconnect delay |
| `Quasar:AgentProfilerMode` | `QUASAR_AGENT_PROFILER_MODE` | `SafeContinuous` | Fallback profiler mode for servers without their own setting: `SafeContinuous`, `DeepContinuous`, `Off`. See [Monitoring](Monitoring.md). |
| `Quasar:AvoidSimultaneousScheduledRestarts` | `QUASAR_AVOID_SIMULTANEOUS_SCHEDULED_RESTARTS` | `true` | Spread scheduled restarts of several servers, see [Servers](Servers.md) |
| `Quasar:DisableServerHealthMonitoring` | `QUASAR_DISABLE_SERVER_HEALTH_MONITORING` | `false` (`true` in Development) | Turns off health checks; the Dashboard shows a notice |

## Managed runtime

Quasar downloads SteamCMD, the Space Engineers Dedicated Server and Magnetar into `ManagedRuntime/Tools/` by default. The `Quasar:ManagedRuntime` keys override that; leave them empty for the defaults.

| Key | Env | Meaning |
| --- | --- | --- |
| `DedicatedServerInstallDirectory` | `QUASAR_DS_INSTALL_DIR` | Managed DS install folder |
| `DedicatedServer64OverridePath` | `QUASAR_DS64_PATH` (or `DS64`) | Use an existing DS `DedicatedServer64` folder |
| `PreferManagedDedicatedServerInstall` | `QUASAR_PREFER_MANAGED_DS` | Default `true` |
| `DedicatedServerLaunchUpdateMode` | `QUASAR_DS_LAUNCH_UPDATE_MODE` | `Skip` (never run SteamCMD on start), `Update` (default, light update check on start), `Validate` (full file validation on start, slow) |
| `SteamCmdInstallDirectory`, `SteamCmdPath`, `SteamCmdHomeDirectory` | `QUASAR_STEAMCMD_INSTALL_DIR`, `QUASAR_STEAMCMD_PATH`, `QUASAR_STEAMCMD_HOME_DIR` | SteamCMD location. On Linux SteamCMD runs with its own `HOME` (`ManagedRuntime/Tools/SteamCmdHome`) so it does not touch the desktop Steam client's files. |
| `MagnetarInstallDirectory` | `QUASAR_MAGNETAR_INSTALL_DIR` | Managed Magnetar folder |
| `MagnetarArchiveUrl`, `ClusterArchiveUrl`, `SteamCmdArchiveUrl` | `QUASAR_MAGNETAR_ARCHIVE_URL`, `QUASAR_CLUSTER_ARCHIVE_URL`, `QUASAR_STEAMCMD_ARCHIVE_URL` | Download sources; empty means the official ones |

Magnetar and DS updates are on [Updates](Updates.md).

## Feature settings on other pages

| Setting | Page |
| --- | --- |
| `Quasar:Auth:*`, `QUASAR_ADMIN_STEAM_ID` | [Access control](AccessControl.md#settings-reference) |
| `Quasar:MachineEndpoints:*`, `QUASAR_MACHINE_ALLOWED_SOURCES`, `QUASAR_MACHINE_ALLOWED_HOSTS` | [Access control](AccessControl.md#machine-endpoints) |
| `Quasar:Updates:*` (`Enabled`, `Owner`, `Repository`, `IncludePrerelease`, `AutoStageWebUpdates`, `AutoApplyUpdates`, `AutoUpdateUiPlugins`, `CheckIntervalSeconds`, `LinuxPackageAssetName`, `WindowsPackageAssetName`) | [Updates](Updates.md) |
| `Quasar:BackupDirectory`, `QUASAR_BACKUP_DIR` | [Backups](Backups.md) |
| `Quasar:Analytics:RetentionDays`, `QUASAR_ANALYTICS_RETENTION_DAYS` (30, 45, 60 or 90; other values fall back to 30) | [Monitoring](Monitoring.md) |
| `Quasar:Diagnostics:*`, `QUASAR_DIAGNOSTICS_DIRECTORY`, `Quasar:ServerList:*` | [Diagnostics](Diagnostics.md) |
| `Quasar:Plugins:*`, `QUASAR_SAFE_MODE`, `QUASAR_DISABLE_UI_PLUGINS`, `QUASAR_UI_PLUGIN_DIRS` | [UI plugins](UiPlugins.md) |
| `Quasar:ClusterCatalogPath`, `Quasar:ClusterDependencies:*` | [Clusters](Clusters.md) |
| Outbound proxy for Discord (`HTTPS_PROXY` and similar) | [Monitoring](Monitoring.md) |

`appsettings.json` still ships the Bootstrap-era keys `LinuxWebAssetName`, `WindowsWebAssetName`, `LinuxBootstrapAssetName` and `WindowsBootstrapAssetName`; current packages don't use them.

## Environment variables

Short `QUASAR_*` variables for admins. Each wins over the matching `appsettings.json` key.

| Variable | Key or effect |
| --- | --- |
| `QUASAR_INSTALL_DIR` | Data directory (a service install overrides it with the recorded data directory) |
| `QUASAR_ADMIN_STEAM_ID` | First admin when `rbac.json` is missing |
| `QUASAR_WEB_HOST`, `QUASAR_WEB_PORT` | `Quasar:Host`, `Quasar:Port`. The installers also read them for the health address when they write the installation record. |
| `QUASAR_PUBLIC_BASE_URL` | URL Quasar advertises to browsers, the CLI and agents |
| `QUASAR_HOST_ID` | Host identifier, default the lowercase machine name |
| `QUASAR_MODE` | `Quasar:Mode` |
| `QUASAR_HEADLESS` | `Quasar:Headless` |
| `QUASAR_OPEN_BROWSER_ON_START` | `Quasar:OpenBrowserOnStart` |
| `QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN` | `Quasar:PreserveManagedServersOnShutdown` |
| `QUASAR_LOG_DIR`, `QUASAR_LOG_FORMAT`, `QUASAR_LOG_MIN_LEVEL` | Logging keys |
| `QUASAR_CONSOLE_LOGGING` | `true` or `1` also writes the log to the console |
| `QUASAR_BACKUP_DIR` | `Quasar:BackupDirectory` |
| `QUASAR_ANALYTICS_RETENTION_DAYS` | `Quasar:Analytics:RetentionDays` |
| `QUASAR_DIAGNOSTICS_DIRECTORY` | `Quasar:Diagnostics:StorageDirectory` |
| `QUASAR_UPDATES_ENABLED`, `QUASAR_UPDATES_OWNER`, `QUASAR_UPDATES_REPOSITORY`, `QUASAR_UPDATES_INCLUDE_PRERELEASE`, `QUASAR_UPDATES_AUTO_STAGE_WEB`, `QUASAR_UPDATES_AUTO_APPLY`, `QUASAR_UPDATES_AUTO_UPDATE_UI_PLUGINS`, `QUASAR_UPDATES_CHECK_INTERVAL_SECONDS`, `QUASAR_UPDATES_LINUX_PACKAGE_ASSET`, `QUASAR_UPDATES_WINDOWS_PACKAGE_ASSET` | `Quasar:Updates` keys |
| `QUASAR_AGENT_*` | Agent keys in [Shutdown and agent behavior](#shutdown-and-agent-behavior) |
| `QUASAR_DS_*`, `QUASAR_STEAMCMD_*`, `QUASAR_MAGNETAR_*` | Managed runtime keys |
| `QUASAR_GITHUB_TOKEN`, `GITHUB_TOKEN` | GitHub token for release checks, managed runtime downloads, registered plugin hubs and Magnetar starts. `QUASAR_GITHUB_TOKEN` wins over the token saved on [Updates](Updates.md#github-token-for-update-checks), `GITHUB_TOKEN` comes last. For a service install, set it for the updater service too. |
| `QUASAR_URL`, `QUASAR_API_TOKEN` | Used by the `Quasar cluster` CLI |

A service install reads its installation record at start and sets `QUASAR_INSTALL_DIR` (to the data directory), `QUASAR_MODE=service`, `QUASAR_OPEN_BROWSER_ON_START=false` and `QUASAR_LAUNCHER_TOKEN` (the updater's health token). The Linux unit also sets `QUASAR_INSTALL_DIR`, `QUASAR_HOST_STOP_UNIT` and `QUASAR_CONSOLE_LOGGING=true`, with stdout and stderr routed to the journal. The Docker image sets `QUASAR_INSTALL_DIR=/data`, `QUASAR_MODE=service`, `QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN=false`, `QUASAR_UPDATES_ENABLED=false`, `QUASAR_CONSOLE_LOGGING=true`, `DOTNET_RUNNING_IN_CONTAINER=true` and `ASPNETCORE_URLS=http://+:8080`. Don't set `QUASAR_LAUNCHER_TOKEN`, `QUASAR_BOOTSTRAP_VERSION` or `QUASAR_HOST_STOP_UNIT` by hand, nor the variables Quasar sets for each server.

## Command line

The program is `./Quasar` on Linux and `Quasar.exe` on Windows, in the install directory. `Quasar --help` (also `help` or `-h`) prints:

```
Quasar [serve] [--headless] [--foreground]
Quasar update --version <release> | --status | --retry
Quasar cluster --help
Quasar activate-release --restore (internal recovery; Quasar must be stopped)
```

| Command | What it does |
| --- | --- |
| `serve` (or no command) | Runs Quasar until stopped. The services run `serve --service`. |
| `--version` | Prints the full version, for example `1.5.0.0`, and exits with 0 |
| `update --version <release>` | Asks the running Quasar to update to that release and waits up to 15 minutes for the outcome |
| `update --status` | Prints the recorded update operation and whether Quasar can restart itself |
| `update --retry` | Retries the last update after you fixed its cause. In a foreground install with Quasar stopped, installs the staged files; start Quasar afterwards. |
| `update --help` | Prints the update usage and exit codes |
| `rollback`, `--rollback` | Queues a return to the retained previous application for the updater. Prints `Rollback queued. Use Quasar update --status to confirm its outcome.` |
| `activate-release --restore` | Internal recovery: runs `Updater/Quasar.Updater recover` to restore the retained previous application. Stop Quasar and its updater first. |
| `cluster <verb> ...` | Cluster API client, see below |

| Flag | Effect |
| --- | --- |
| `--headless`, `--no-headless`, `--headless=<true or false>` | API-only mode, see [Headless mode](#headless-mode) |
| `--foreground`, `--console` | Console mode with browser launch |
| `--service` | Accepted; service settings come from the installation record |
| `--quiet`, `ensure-running` | Accepted and ignored, so old service command lines still start |
| `--<Section>:<Key>=<value>` | Any setting in .NET command-line form, for example `--Quasar:Port=9000` |

Run `update`, `rollback` and `activate-release` as the service account on Linux, and from an elevated PowerShell on Windows. Exit codes of `update`: `0` status read or the release runs healthy, `2` usage, `3` service unavailable, access denied or recovery needs you, `4` staged and a manual restart is needed, `5` update failed, `6` still pending after the wait, `130` waiting canceled (the update may continue). `rollback` exits with `3` when no previous release is retained (`No previous complete application release is retained. Select a release on Updates.`), an update is pending, or recovery is running.

Quasar itself exits with `3` before it starts when it can't read its installation record (`Cannot read this installation: ...`) or when a 1.5 or later release finds no 1.4.1 migration receipt (`Quasar 1.5 and later require a completed Quasar 1.4.1 migration first. ...`).

In the foreground, Ctrl+C stops Quasar. Managed servers keep running if `PreserveManagedServersOnShutdown` is on.

### Cluster CLI

`Quasar cluster` calls the cluster API of a running Quasar and prints one JSON envelope on stdout and messages on stderr.

```bash
export QUASAR_API_TOKEN='<service principal token>'
./Quasar cluster list --url https://quasar.example
./Quasar cluster goal production off --idempotency-key stop-2026-10-13 --wait
```

- URL: `--url`, else `QUASAR_URL`, else the URL in the local `service-manifest.json`.
- Token: read from `QUASAR_API_TOKEN`, or from the variable named by `--token-env`. Without a token the request has no `Authorization` header, which works only through the loopback bypass on the Quasar machine.
- Changes need `--idempotency-key <key>`. `--wait` polls until the operation finishes (`--wait-timeout`, default 900 seconds). `--timeout` sets the request timeout (default 30 seconds).
- Read verbs include `list`, `health`, `status`, `lifecycle`, `plan`, `recovery-readiness`, `config` and `operation`. Running `Quasar cluster` with a wrong verb prints the full list.
- Exit codes: `0` success, `2` usage, `3` local configuration (no URL), `4` connection or timeout, `5` authentication or authorization, `6` API rejection, `7` operation failed, `8` incompatible protocol.

Cluster commands are described on [Clusters](Clusters.md); tokens on [Access control](AccessControl.md#api-authentication-for-automation).

## Common problems

| Message or symptom | Cause and fix |
| --- | --- |
| The UI doesn't come up and `quasar.log` shows `Quasar terminated unexpectedly.` with an address-in-use error | Another program uses the port. Stop it or change `Quasar:Port`, then repair the service install (see [Web UI address and port](#web-ui-address-and-port)). |
| Updates or **Restart Quasar** fail health checks after a port change | The port was changed only with `QUASAR_WEB_PORT` or `ASPNETCORE_URLS`, or the installer's repair mode wasn't run. Change `Quasar:Port` in `appsettings.json` and run the repair. |
| `Cannot read this installation: ...` | The command runs as a user that can't read the installation record. Use the Linux service account or an elevated Windows shell. |
| `Quasar 1.5 and later require a completed Quasar 1.4.1 migration first. ...` | Install and start the complete 1.4.1 package first, see [Install on Linux](InstallLinux.md). |
| `No running local Quasar update service. Start Quasar from this installation, then retry. ...` | `update --version` or `update --retry` found no running Quasar for this data directory. Start it, or use `update --status`. |
| `Quasar URL is required (--url, QUASAR_URL, or local discovery).` | The cluster CLI found no URL. Pass `--url`. |
| `Mutations require --idempotency-key <key>.` | Add `--idempotency-key`. |
| Edited `appsettings.json` but nothing changed | Restart Quasar. Check that you edited the file in the data directory and that no `QUASAR_*` variable overrides the key. |
| `Quasar authentication can be disabled only on loopback listeners. ...` at startup | `Quasar:Auth:Enabled=false` needs `Quasar:Host`, `QUASAR_WEB_HOST` and every `ASPNETCORE_URLS` binding on `localhost`, `127.0.0.1` or `::1`. |
| `QUASAR_ADMIN_STEAM_ID must be a 17-digit SteamID64.` at startup | Fix or remove the variable, see [First setup](FirstSetup.md#option-3-quasar_admin_steam_id). |

## Sources
- `Docs/Configuration.md`
- `Docs/QuickStart.md`
- `Docs/Docker.md`
- `Docs/QuasarUpdates.md`
- `Quasar/appsettings.json`
- `Quasar/appsettings.Development.json`
- `Quasar/Program.cs`
- `Quasar/Services/QuasarApplicationEntry.cs`
- `Quasar/Services/WebServiceOptions.cs`
- `Quasar/Services/Auth/PublicAccessSecurity.cs`
- `Quasar/Services/MachineEndpointSecurity.cs`
- `Quasar/Services/WebServiceProbeEndpoints.cs`
- `Quasar/Services/ManagedRuntimeOptions.cs`
- `Quasar/Services/ManagedRuntimeWarmupService.cs`
- `Quasar/Services/QuasarLoggingConfigurator.cs`
- `Quasar/Services/BrowserLauncher.cs`
- `Quasar/Services/WebServiceManifestHostedService.cs`
- `Quasar/Services/QuasarShutdownService.cs`
- `Quasar/Services/DedicatedServerSupervisor.cs`
- `Quasar/Services/DedicatedServerRuntimePreparer.cs`
- `Quasar/Services/QuasarConfigProfileCatalog.cs`
- `Quasar/Services/QuasarDevFolderCatalog.cs`
- `Quasar/Services/ClusterCatalog.cs`
- `Quasar/Services/ClusterLocalHost.cs`
- `Quasar/Services/DiagnosticsUplinkService.cs`
- `Quasar/Services/Analytics/AnalyticsStoreOptions.cs`
- `Quasar/Services/Updates/QuasarUpdateOptions.cs`
- `Quasar/Services/Updates/QuasarUpdateService.cs`
- `Quasar/Services/Updates/PushNotificationService.cs`
- `Quasar/Services/Plugins/QuasarUiPluginCatalog.cs`
- `Quasar/Services/ServerList/ServerListOptions.cs`
- `Quasar/Services/Auth/QuasarAuthSettingsService.cs`
- `Quasar/Services/Discord/DiscordOptionsCatalog.cs`
- `Quasar/Components/Pages/Security.razor`
- `Quasar.Agent/AgentOptions.cs`
- `Quasar.Bootstrap/UpdateCli.cs`
- `Quasar.Bootstrap/ClusterCli.cs`
- `Shared/Updates/QuasarServiceInstallation.cs`
- `Shared/Updates/QuasarUpgradePath.cs`
- `Quasar.Updater/ConfigureInstallation.cs`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
- `install.sh`
- `scripts/install.ps1`
- `Dockerfile`
- `docker-compose.yml`
- `.env.example`
- `Shared/Updates/BootstrapUpdateSupport.cs`
