# Configuration

This page covers how Quasar itself is configured: where its config and state files live, the `appsettings.json` keys an admin is likely to change, environment variables, the `Quasar` command line, and which changes need a restart. Settings for single features are on their own pages, linked below.

## Where files live

Quasar keeps all state in one data directory, which is also its install root. Quasar resolves it in this order:

1. `QUASAR_INSTALL_DIR` environment variable. The launcher sets it to its own folder when it is not set, and the installers write it into the service.
2. A `.quasar-install-dir` file (or the file named by `QUASAR_INSTALL_DIR_FILE`) holding a path. This is for development runs.
3. The folder of the running program.

| Install | Data directory |
| --- | --- |
| Linux packaged install | The folder you extracted and ran `install.sh` from, for example `~/.local/share/Quasar`. `--install-dir <dir>` copies Quasar elsewhere. |
| Windows packaged install | The folder you extracted and ran `install.ps1` from, for example `C:\quasar\Quasar`. `-InstallDir <dir>` copies Quasar elsewhere. |
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
| `ManagedRuntime/WebService/<version>/` | Installed versions of the Quasar web worker |
| `Updates/` | Update staging and the active release pointer, see [Updates](Updates.md) |
| `Logs/` | `quasar.log` and `discord.log` |
| `Diagnostics/` | Diagnostic capture, see [Diagnostics](Diagnostics.md) |
| `DataProtection-Keys/` | Keys that encrypt sign-in cookies and stored secrets. Back them up with the rest; losing them signs everyone out and makes stored credentials unreadable. |
| `service-manifest.json` | URL and process of the running worker, used by the launcher and the CLI |
| `supervisor-state.json` | Supervisor state of managed servers |

Back up the whole data directory as one unit (see [Backups](Backups.md)).

## How Quasar reads its settings

Quasar has two programs: the launcher (`Quasar` on Linux, `Quasar.exe` on Windows, also called Bootstrap) and the web worker it starts. Both read `appsettings.json`, so keep settings there.

The worker loads, from lowest to highest priority:

1. `appsettings.json` and `appsettings.<Environment>.json` next to the worker program, in the current folder and in the `WebService` folder next to the worker. A development run inside the source tree also reads the files of the `Quasar` project folder. Folders above the worker are not read, so another Quasar install further up, such as `~/Quasar`, does not change this one.
2. `appsettings.json` and `appsettings.<Environment>.json` in the data directory. This is the file you edit.
3. Environment variables in .NET form, with `__` as the section separator: `QUASAR__AUTH__ENABLED=false` sets `Quasar:Auth:Enabled`.
4. Command-line arguments to the worker in .NET form, for example `--Quasar:Port=9000`.

On top of that, many settings have a short `QUASAR_*` variable that Quasar checks first and that wins over every other source (table under [Environment variables](#environment-variables)).

The launcher reads only `appsettings.json` (from its own folder and the data directory, without the .NET environment variables) plus a few `QUASAR_*` variables. It takes `Quasar:Host`, `Quasar:Port`, `Quasar:PreserveManagedServersOnShutdown`, the `Quasar:Updates` keys and `Quasar:Diagnostics:StorageDirectory` from there.

Quasar updates keep your `appsettings.json`. When a new release changes defaults, Quasar merges them into your file; a conflict is shown on the **Updates** page for you to resolve. See [Updates](Updates.md).

## When changes apply

Most settings are read once at startup. After editing `appsettings.json` or an environment variable, restart Quasar:

- Linux user service: `systemctl --user restart quasar.service` (system service: `sudo systemctl restart quasar.service`)
- Windows: `Stop-ScheduledTask -TaskName Quasar; Start-ScheduledTask -TaskName Quasar`
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

To change the port, edit `Quasar:Port` in the data directory's `appsettings.json` and restart Quasar:

```json
{
  "Quasar": {
    "Host": "0.0.0.0",
    "Port": 9000
  }
}
```

Use `appsettings.json`, not environment variables, for an installed Quasar. The launcher health-checks the worker on the port from `appsettings.json`. `QUASAR_WEB_HOST`, `QUASAR_WEB_PORT` and `ASPNETCORE_URLS` change only the worker, so the launcher would consider the worker unhealthy. They are fine when running the worker directly. The Docker image runs the worker directly and listens through `ASPNETCORE_URLS=http://+:8080`; see [Docker](Docker.md).

When the host is a wildcard, Quasar advertises `http://127.0.0.1:<port>`. This URL is printed at startup, written to `service-manifest.json`, and given to Quasar.Agent in each managed server. Set `QUASAR_PUBLIC_BASE_URL` to advertise another URL.

Port `8080` is also the Space Engineers DS Remote API default. Quasar sets each managed server's Remote API port to its game port plus 2000, so they do not collide by default.

## Run mode, headless mode and browser launch

| Key | Env | Default | Meaning |
| --- | --- | --- | --- |
| `Quasar:Mode` | `QUASAR_MODE` | `Console` | `service` for installed services. The launcher always starts the worker in service mode. |
| `Quasar:Headless` | `QUASAR_HEADLESS` | `false` | API-only mode |
| `Quasar:OpenBrowserOnStart` | `QUASAR_OPEN_BROWSER_ON_START` | `true` | Worker opens a browser on start; only in console mode, not headless |

### Headless mode

Headless mode runs the supervisor, background jobs, HTTP APIs and the agent socket `/ws/agent`, but no web UI, UI plugins or branding assets, and no `/login`. `GET /` returns a small JSON document. Server definitions, profiles and templates are the same in both modes, so you can switch back and forth.

Start it with `Quasar serve --headless` or with `QUASAR_HEADLESS=true` in the launcher's environment. The launcher passes the mode to the worker and keeps it across updates. Under the launcher the `Quasar:Headless` key in `appsettings.json` has no effect, because the launcher always sets `QUASAR_HEADLESS` for the worker. The key works when running the worker directly.

Authentication stays on in headless mode; see [Access control](AccessControl.md).

Health endpoints, available in both modes:

- `GET /api/health`: process liveness, version, `headless`, counts of servers, clusters and agents.
- `GET /api/ready`: returns 200 with `"status": "ready"` when the cluster operation store is ready, 503 otherwise.

### Browser launch

The installed services and `Quasar serve` never open a browser. `Quasar ensure-running --open-browser` opens the UI in the default browser once the worker is healthy, if a desktop session is present (always on Windows; on Linux when `DISPLAY` or `WAYLAND_DISPLAY` is set). The URL is always printed.

## Logging

| Key | Env | Default | Meaning |
| --- | --- | --- | --- |
| `Quasar:Logging:Directory` | `QUASAR_LOG_DIR` | `<data>/Logs` | Folder for `quasar.log` and `discord.log` |
| `Quasar:Logging:Format` | `QUASAR_LOG_FORMAT` | `text` | `text` or `json` |
| `Quasar:Logging:MinimumLevel` | `QUASAR_LOG_MIN_LEVEL` | `Warn` (`Info` in Development) | `Trace`, `Debug`, `Info`, `Warn`, `Error`, `Fatal` |

`discord.log` gets the Discord bot's messages. Under the launcher the worker also writes its log to the console, which ends up in the systemd journal on Linux (`journalctl --user -u quasar.service`) and in `docker compose logs` for Docker. Server logs (DS and Magnetar) are in each server's folder; see [Troubleshooting](Troubleshooting.md).

## Shutdown and agent behavior

| Key | Env | Default | Meaning |
| --- | --- | --- | --- |
| `Quasar:PreserveManagedServersOnShutdown` | `QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN` | `true` (Docker image: `false`) | Leave managed servers running when Quasar stops and adopt them again on start |
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
| `Quasar:Updates:*` (`Enabled`, `IncludePrerelease`, `AutoStageWebUpdates`, `AutoApplyUpdates`, `AutoUpdateUiPlugins`, `CheckIntervalSeconds`) | [Updates](Updates.md) |
| `Quasar:BackupDirectory`, `QUASAR_BACKUP_DIR` | [Backups](Backups.md) |
| `Quasar:Analytics:RetentionDays`, `QUASAR_ANALYTICS_RETENTION_DAYS` (30, 45, 60 or 90; other values fall back to 30) | [Monitoring](Monitoring.md) |
| `Quasar:Diagnostics:*`, `QUASAR_DIAGNOSTICS_DIRECTORY`, `Quasar:ServerList:*` | [Diagnostics](Diagnostics.md) |
| `Quasar:Plugins:*`, `QUASAR_SAFE_MODE`, `QUASAR_DISABLE_UI_PLUGINS`, `QUASAR_UI_PLUGIN_DIRS` | [UI plugins](UiPlugins.md) |
| `Quasar:ClusterCatalogPath`, `Quasar:ClusterDependencies:*` | [Clusters](Clusters.md) |
| Outbound proxy for Discord (`HTTPS_PROXY` and similar) | [Monitoring](Monitoring.md) |

## Environment variables

Short `QUASAR_*` variables for admins. Each wins over the matching `appsettings.json` key.

| Variable | Key or effect |
| --- | --- |
| `QUASAR_INSTALL_DIR` | Data directory |
| `QUASAR_ADMIN_STEAM_ID` | First admin when `rbac.json` is missing |
| `QUASAR_WEB_HOST`, `QUASAR_WEB_PORT` | `Quasar:Host`, `Quasar:Port`, worker only |
| `QUASAR_PUBLIC_BASE_URL` | URL Quasar advertises to browsers, the CLI and agents |
| `QUASAR_HOST_ID` | Host identifier, default the lowercase machine name |
| `QUASAR_MODE` | `Quasar:Mode` |
| `QUASAR_HEADLESS` | Headless mode, also read by the launcher |
| `QUASAR_OPEN_BROWSER_ON_START` | `Quasar:OpenBrowserOnStart` |
| `QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN` | `Quasar:PreserveManagedServersOnShutdown`, also read by the launcher |
| `QUASAR_LOG_DIR`, `QUASAR_LOG_FORMAT`, `QUASAR_LOG_MIN_LEVEL` | Logging keys |
| `QUASAR_BACKUP_DIR` | `Quasar:BackupDirectory` |
| `QUASAR_ANALYTICS_RETENTION_DAYS` | `Quasar:Analytics:RetentionDays` |
| `QUASAR_DIAGNOSTICS_DIRECTORY` | `Quasar:Diagnostics:StorageDirectory` |
| `QUASAR_UPDATES_ENABLED`, `QUASAR_UPDATES_INCLUDE_PRERELEASE`, `QUASAR_UPDATES_AUTO_STAGE_WEB`, `QUASAR_UPDATES_AUTO_APPLY`, `QUASAR_UPDATES_AUTO_UPDATE_UI_PLUGINS`, `QUASAR_UPDATES_CHECK_INTERVAL_SECONDS` | `Quasar:Updates` keys |
| `QUASAR_AGENT_*` | Agent keys in [Shutdown and agent behavior](#shutdown-and-agent-behavior) |
| `QUASAR_DS_*`, `QUASAR_STEAMCMD_*`, `QUASAR_MAGNETAR_*` | Managed runtime keys |
| `QUASAR_GITHUB_TOKEN`, `GITHUB_TOKEN` | GitHub token for the launcher's first web UI download and its update checks. `QUASAR_GITHUB_TOKEN` wins over the token saved on [Updates](Updates.md#github-token-for-update-checks), `GITHUB_TOKEN` comes last. Launcher only |
| `QUASAR_URL`, `QUASAR_API_TOKEN` | Used by the `Quasar cluster` CLI |

The installed Linux service sets `QUASAR_MODE=Service`, `QUASAR_OPEN_BROWSER_ON_START=false`, `HOME` and `QUASAR_INSTALL_DIR`. The Docker image sets `QUASAR_INSTALL_DIR=/data`, `QUASAR_MODE=service`, `QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN=false`, `QUASAR_UPDATES_ENABLED=false`, `QUASAR_CONSOLE_LOGGING=true` and `ASPNETCORE_URLS=http://+:8080`. Some `QUASAR_*` variables are internal and set by the launcher for the worker (`QUASAR_LAUNCHER_TOKEN`, `QUASAR_BOOTSTRAP_VERSION`, `QUASAR_COORDINATED_UPDATES`, `QUASAR_CONSOLE_LOGGING`) or by Quasar for each server; do not set them by hand.

## Command line

The launcher is `./Quasar` on Linux and `Quasar.exe` on Windows, in the data directory.

```
Quasar [ensure-running|serve|activate-release|cluster|help] [options]
```

| Command | What it does |
| --- | --- |
| `ensure-running` (default when no command is given) | If a healthy Quasar is already running, prints `Quasar` and its URL and exits. Otherwise starts one: in the foreground when run from a terminal, detached otherwise, then waits up to 60 seconds for it to become healthy. |
| `serve` | Runs the launcher and worker until stopped. Used by the services (`serve --quiet` on Linux, `serve --quiet --service` on Windows). Exits at once with 0 if a healthy Quasar is already running, unless in service mode, where it replaces that worker. |
| `activate-release` | Internal, used during updates |
| `cluster <verb> ...` | Cluster API client, see below |
| `help` | Prints the commands and options and exits with 0 |

| Flag | Effect |
| --- | --- |
| `--headless` | API-only mode, see [Headless mode](#headless-mode) |
| `--open-browser` | With `ensure-running`: open the UI in a browser |
| `--force` | With `ensure-running`: stop the running Quasar worker and start a new one |
| `--quiet` | No console output, never foreground |
| `--foreground`, `--console` | Stay in the foreground even when not attached to a terminal |
| `--service` | Service mode |
| `-h`, `--help` | Same as `help` (`cluster --help` prints the cluster usage) |

`ensure-running` and `serve` reject any other option or extra argument with exit code `2` instead of starting Quasar.

Exit codes of `ensure-running`: `0` running, `2` unknown command, option or argument, `3` launcher entry point not found, `4` "Quasar launcher did not become healthy before timeout.", `5` port in use by something that is not Quasar. `serve` exits with `1` when the port is taken.

In the foreground, Ctrl+C stops the launcher and the worker. Managed servers keep running if `PreserveManagedServersOnShutdown` is on.

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
| `Error: port 8080 is already bound by another process and no healthy Quasar server was detected. Use --force to terminate the existing process and restart.` | Another program uses the port. Stop it or change `Quasar:Port`. |
| `Error: port N is already bound by another process and is not a healthy Quasar server.` (from `serve`) | Same as above. |
| Launcher reports the worker as unhealthy after a port change | The port was changed with `QUASAR_WEB_PORT` or `ASPNETCORE_URLS`, so the launcher checks the old port. Change `Quasar:Port` in `appsettings.json` instead. |
| `Quasar launcher did not become healthy before timeout.` | The worker did not answer `/api/health` within 60 seconds. Check `Logs/quasar.log` and the console or journal. While the launcher retries the first web UI download, `ensure-running` prints its wait lines and keeps waiting instead. |
| `Quasar URL is required (--url, QUASAR_URL, or local discovery).` | The cluster CLI found no URL. Pass `--url`. |
| `Mutations require --idempotency-key <key>.` | Add `--idempotency-key`. |
| Edited `appsettings.json` but nothing changed | Restart Quasar. Check that you edited the file in the data directory and that no `QUASAR_*` variable overrides the key. |
| `Quasar:Headless` set but the UI still loads | Under the launcher, use `--headless` or `QUASAR_HEADLESS=true`. |
| `QUASAR_ADMIN_STEAM_ID must be a 17-digit SteamID64.` at startup | Fix or remove the variable, see [First setup](FirstSetup.md#option-3-quasar_admin_steam_id). |

## Sources
- `Docs/Configuration.md`
- `Docs/QuickStart.md`
- `Docs/Docker.md`
- `Quasar/appsettings.json`
- `Quasar/appsettings.Development.json`
- `Quasar/Program.cs`
- `Quasar/Services/WebServiceOptions.cs`
- `Quasar/Services/ManagedRuntimeOptions.cs`
- `Quasar/Services/ManagedRuntimeWarmupService.cs`
- `Quasar/Services/QuasarLoggingConfigurator.cs`
- `Quasar/Services/BrowserLauncher.cs`
- `Quasar/Services/WebServiceManifestHostedService.cs`
- `Quasar/Services/DedicatedServerSupervisor.cs`
- `Quasar/Services/DedicatedServerRuntimePreparer.cs`
- `Quasar/Services/QuasarConfigProfileCatalog.cs`
- `Quasar/Services/QuasarDevFolderCatalog.cs`
- `Quasar/Services/ClusterCatalog.cs`
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
- `Quasar.Bootstrap/Program.cs`
- `Quasar.Bootstrap/ClusterCli.cs`
- `Quasar.Bootstrap/Properties/launchSettings.json`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
- `install.sh`
- `scripts/install.ps1`
- `Dockerfile`
- `docker-compose.yml`
- `.env.example`
