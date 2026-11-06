# Updates

How Quasar updates itself, how it keeps the managed Magnetar and Space Engineers Dedicated Server (DS) runtime current, and what to do when an update fails. Cluster Hosts and Docker installs have their own update paths, linked below.

## Direct installation and upgrades

Install the current complete platform package directly. Fresh installs and upgrades
need no intermediate release or migration receipt. Existing
`Updates/migration-1.4.1.json` files are historical and are no longer read or written.
Older published packages may still report the prerequisite; use the next release's
complete package instead of writing a receipt by hand.

A Bootstrap or Scheduled Task install still needs the explicit
[native-service migration](#migrating-from-bootstrap), preserving paths, account,
service names, settings, credentials and Data Protection keys together. Package
checksums, validation, service ownership, health checks and rollback still apply.
The newest full 1.4.1 bridge stays available for old Bootstrap worker discovery;
current release publication does not depend on it.

### Broken 1.4.1.1 package

Do not rerun the historical 1.4.1.1 Windows installer: its identity and file-access
bugs can leave the installation unusable. Stop both services and any old task,
back up data, credentials and keys together, restore administrator access only to
the dedicated Quasar folders if needed, and install the current complete package
with the original paths and service names. Keep the old application for recovery
until the new service passes health checks. No receipt is needed.

## What gets updated and by whom

| Component | Source | Who checks | Who installs |
| --- | --- | --- | --- |
| Quasar application | GitHub releases of `CometWorks/quasar` | Quasar, 10 seconds after start, then every 15 minutes, and on **Check Quasar** | Quasar downloads and verifies, the updater service replaces and health-checks |
| Updater service | Complete platform package | Nobody automatically | The installer, with both services stopped |
| Magnetar | Latest full GitHub release of `CometWorks/magnetar` | Quasar: startup, every hour, **Check Magnetar**, each managed server start | Quasar |
| Dedicated Server | Steam app `298740` through SteamCMD | Quasar: startup (install only), each managed server start, **Check Dedicated Server** | Quasar |
| Quasar UI plugins | QuasarHub catalog | Quasar, every 15 minutes | Quasar (opt-in), see [UI plugins](UiPlugins.md) |
| Cluster packages and Hosts | GitHub releases / Quasar | See [Cluster hosts](ClusterHosts.md) and [Clusters](Clusters.md) | |

The Quasar release checks need Linux or Windows. On other platforms the Updates page shows "Automatic updates are not supported on this platform."

## The Updates page

**Updates** (`/settings/updates`, in the Tools & settings group, admins only) has, from top to bottom:

- **Check Quasar**, and the result of the last check.
- The restart capability: whether Quasar verified its native services. It names the supervisor, for example "systemd user units quasar.service + quasar-updater.service" or "Windows Services Quasar + QuasarUpdater", or says what is wrong and how to repair it. See [Native service checks](#native-service-checks).
- The current update operation: target version, stage, message, operation ID and time.
- An appsettings.json conflict editor, when a merge failed (see below).
- **Update Quasar**: pick a release in **Version to install** and press **Update Quasar**. **Retry update** appears when the last operation failed or is staged.
- **Advanced recovery**, a collapsed panel with the command-line commands. On an old Bootstrap installation it also has **Stage legacy worker recovery** and **Activate legacy worker recovery**. They update only the web worker and do not migrate the installation.
- **Cluster packages** (Linux, when clusters exist), the **Managed Runtime** table with **Check Magnetar** and **Check Dedicated Server**, the update switches and the GitHub token.

An old Bootstrap installation also shows a warning that 1.4.1 prepares the migration, with a link to the migration instructions.

## How a Quasar UI update works

The UI and the server supervisor run in one application process. An update replaces that whole application.

1. **Update Quasar**, `Quasar update --version <v>` or an automatic check writes an operation to `<data directory>/Updates/operation.json` (stage `Queued`). A second request for the same version reuses it.
2. Quasar downloads the complete package for the platform (`Downloading`), checks it against the release's `SHA256SUMS`, checks that every file in its manifest exists and that its version matches (`Verified`), and merges `appsettings.json` (see below).
3. The operation becomes `Ready`.
4. The updater service picks up `Ready` operations. It does nothing while the main service is stopped, so a staged update never starts a Quasar you stopped on purpose. It checks the services again, downloads `SHA256SUMS` from the release repository recorded at install time, and verifies its own private copy of the archive. A staged folder alone is not trusted.
5. The updater stops the main service (`Restarting`), keeps a complete copy of the current application and settings, replaces the application files, writes the merged settings, starts the service, and waits up to 60 seconds for the new process to report the requested version and the service's PID on a token-protected loopback endpoint.
6. On success the operation becomes `Healthy`. On failure, or if the updater itself was interrupted, it restores the previous application and settings, starts it again and marks the operation `Failed`.

The updater never replaces its own files or the installation metadata (`service-installation.json`). Upgrade the updater by running the installer from a newer complete package with both services stopped.

Quasar records its version with all four parts, for example `1.5.0.0`. The sidebar shows `1.5.0`, while Updates, the command line, packages and health checks keep the full version.

### Running game servers during a cutover

Application updates and restarts leave detached game servers running and save their adoption state before Quasar stops, even when Quasar is set to stop servers on an ordinary shutdown. The local cluster Host keeps running as well. A machine reboot still stops everything; the servers' goal states apply after it. Some security upgrades need a server restart anyway. Follow the [DIS-0042 instructions](https://github.com/CometWorks/quasar/blob/main/Docs/SecurityUpgradeDIS0042.md) when crossing that boundary.

### Native service checks

Updates go through the updater only when both native services are verified. Quasar checks this at startup and every 30 seconds. The power dialog and UI Plugins enable restart only when verification belongs to the current Quasar process. Missing, stale, unreadable or denied capability disables restart with repair guidance; a rejected request leaves Quasar running. Manual restart also refuses while an update is pending:


- **Linux:** the main PID must belong to the configured systemd unit, the unit's executable and account must match the installation, and the unit needs `Restart=on-failure` and `KillMode=process`, with exit code 75 not counted as success. The updater unit must run the installed updater as its configured account with `Restart=on-failure`. System installs run the updater as the main account, with a polkit rule that lets that account start, stop and restart only its Quasar unit.
- **Windows:** both services must be running, the main service must own the Quasar process, executables and accounts must match, and both services need restart-on-failure recovery that also covers reported service failures. New installs use `NT SERVICE\<service name>` virtual accounts.

A token, a launch flag or an environment variable is not enough. If the check fails, the page shows the reason and says to run the current installer in repair mode with the existing names, paths and account (`install.sh --repair-supervision`, `install.ps1 -RepairSupervision`). Until then updates are only staged: "Staged safely. Stop Quasar, run Quasar update --retry, then start Quasar manually."

### Rolling back Quasar UI and Bootstrap

To go back, select an older release and press **Update Quasar**. It installs that complete package the same way. Only releases with the complete package layout can be installed. Automatic checks never downgrade.

`Quasar rollback` (or `Quasar --rollback`) from the install folder queues a rollback to the application kept from the last update ("Rollback queued. Use Quasar update --status to confirm its outcome."). It needs the native services and refuses while an update is pending. To stay on an older release, turn off automatic updates.

An installation that was migrated from Bootstrap can only go back to the Bootstrap era from the complete backup kept during migration, with its old supervision.

### Command-line UI recovery

Run these from the install folder, as the service account on Linux or from an elevated shell on Windows (`Quasar.exe` there):

```sh
./Quasar update --version 1.5.0.0
./Quasar update --status
./Quasar update --retry
```

The command finds the data directory through the installation metadata. The running Quasar does the work; the command waits for the outcome. Exit codes:

| Code | Meaning |
| --- | --- |
| 0 | Status read, or the requested release is healthy and running |
| 2 | Invalid command or version |
| 3 | Quasar not running, access denied, or recovery needs an operator |
| 4 | Staged; a manual restart is needed |
| 5 | Update failed |
| 6 | Still pending when the command stopped waiting; check `--status` |
| 130 | Waiting was canceled; the update may continue |

If the application files are broken, stop both services and run `Quasar activate-release --restore` (or `Updater/Quasar.Updater recover --installation <install folder>/service-installation.json`). It restores the files kept from the last update. Start Quasar afterward and check `update --status`. See the [update and migration guide](https://github.com/CometWorks/quasar/blob/main/Docs/QuasarUpdates.md).

### appsettings.json merge during staging

Quasar merges `appsettings.json` three ways: the previous release's defaults, your current settings and the new release's defaults. When a key conflicts, the update stops and Updates shows **Current appsettings.json**, **Incoming appsettings.json** and an editable **Final appsettings.json**, with **Take current**, **Take incoming** and **Save resolution**. Save valid JSON without conflict markers, then press **Retry update**. The resolution is kept across reconnects and restarts. On Linux the files are owner-only, since settings can hold credentials. A failed update also restores your previous settings.

## Migrating from Bootstrap

Installations from before 1.4.1 run a Bootstrap launcher that starts a separate web worker, on Linux under systemd and on Windows as a Scheduled Task. Updating only the web worker does not install the updater or replace the Scheduled Task. To migrate:

1. Save worlds. Back up the old installation, including `appsettings.json`, credentials and the Data Protection keys, together.
2. Extract the current complete package to a temporary folder.
3. Run its installer against the existing installation. It keeps the existing `appsettings.json` and data.
   - Linux: `./install.sh --no-build --install-dir /path/to/Quasar --service-name quasar --start`. For a system unit add `sudo`, `--system` and `--user <account>`.
   - Windows, elevated PowerShell: `.\install.ps1 -InstallDir 'C:\Quasar' -ServiceName Quasar -MigrateScheduledTask -TaskName Quasar -Start`. Stop the old task and its game servers first; the installer refuses a running task. It disables the task, keeps a backup and checks the new service. On failure it restores the old application and the task's previous state.
4. Check Updates and `Quasar update --status` before removing the old copy or task.

Moving off a Windows Scheduled Task can need one game server restart, because running servers may belong to the task's job object. The new virtual accounts get access to the install and data folders only. Grant the main account access to worlds, backup folders or network shares outside them before starting servers. Details are in [Install on Linux](InstallLinux.md), [Install on Windows](InstallWindows.md) and the [migration guide](https://github.com/CometWorks/quasar/blob/main/Docs/QuasarUpdates.md#migrating-existing-installations).

## Automatic updates (opt-in)

**Automatically update Quasar** (`Quasar:Updates:AutoApplyUpdates`, default off) installs the newest release after a check and restarts Quasar while game servers keep running. **Automatically stage Quasar updates after checks** (`AutoStageWebUpdates`, default on) only downloads and verifies the newest release. **Update Quasar** then installs the staged operation without a new download.

Checks never downgrade, and they don't repeat a failed or completed operation. A settings conflict always needs an admin.

UI plugin auto-updates are a separate opt-in. Plugin builds and Quasar updates share the same maintenance lock. After a successful plugin update the application restarts through verified native services or legacy Bootstrap. Missing capability pauses automatic plugin updates and leaves any automatic restart pending until a later catalog refresh sees successful verification.

## Update settings

All keys live under `Quasar:Updates` in the data directory's `appsettings.json`. Environment variables override them.

| Key | Default | Env override | Notes |
| --- | --- | --- | --- |
| `Enabled` | `true` | `QUASAR_UPDATES_ENABLED` | `false` stops Quasar release checks and updates. Magnetar and DS checks still run. |
| `Owner` | `CometWorks` | `QUASAR_UPDATES_OWNER` | GitHub owner |
| `Repository` | `quasar` | `QUASAR_UPDATES_REPOSITORY` | GitHub repository |
| `IncludePrerelease` | `false` | `QUASAR_UPDATES_INCLUDE_PRERELEASE` | Also the **Include prerelease versions** switch |
| `AutoStageWebUpdates` | `true` | `QUASAR_UPDATES_AUTO_STAGE_WEB` | Also the **Automatically stage Quasar updates after checks** box |
| `AutoApplyUpdates` | `false` | `QUASAR_UPDATES_AUTO_APPLY` | See above |
| `AutoUpdateUiPlugins` | `false` | `QUASAR_UPDATES_AUTO_UPDATE_UI_PLUGINS` | See above |
| `CheckIntervalSeconds` | `900` | `QUASAR_UPDATES_CHECK_INTERVAL_SECONDS` | Values below 60 fall back to 900 |
| `LinuxPackageAssetName` | `quasar-linux-x64.tar.gz` | `QUASAR_UPDATES_LINUX_PACKAGE_ASSET` | Complete package |
| `WindowsPackageAssetName` | `quasar-win-x64.zip` | `QUASAR_UPDATES_WINDOWS_PACKAGE_ASSET` | Complete package |
| `LinuxWebAssetName` | `quasar-web-linux-x64.tar.gz` | `QUASAR_UPDATES_LINUX_WEB_ASSET` | Legacy Bootstrap installs only |
| `WindowsWebAssetName` | `quasar-web-win-x64.zip` | `QUASAR_UPDATES_WINDOWS_WEB_ASSET` | Legacy Bootstrap installs only |

The switches and check boxes on Updates write to the data directory's `appsettings.json` and apply at once. The updater does not read `Owner`, `Repository` or the package names from there. The installer records them in the installation metadata, so run the installer in repair mode after changing them.

Prerelease builds are for testing. The page shows a red warning while they are enabled, and asks for confirmation before turning them on.

## GitHub token for update checks

Cluster release discovery also works without a token for public `CometWorks/cluster` releases. The cluster page shows the result of the actual GitHub request; a missing token alone does not make the latest version unavailable. Private releases still require repository read access.

Quasar works without a token, but GitHub rate-limits unauthenticated API calls per IP address, which shared hosts and cloud IPs can hit. To add one:

1. Open <https://github.com/settings/tokens/new> and create a classic personal access token. Give it a note such as `Quasar update checks`, pick an expiration you will renew, and select no scopes at all.
2. On **Updates**, paste it into **GitHub token** and press **Save token**.
3. Press **Check Quasar** to test it.

Token precedence for Quasar and its updater is `QUASAR_GITHUB_TOKEN`, then the saved token, then `GITHUB_TOKEN`. The page then says "Quasar uses the GitHub token from the <variable> environment variable". A token in `QUASAR_GITHUB_TOKEN` wins over one saved on the page, so change the variable instead, while a saved token wins over `GITHUB_TOKEN`. Prefer the saved token, which both services read. To use an environment variable on Linux, add `EnvironmentFile=%h/.config/quasar-github-token.env` under `[Service]` in both user units (a system unit needs an absolute path the account can read), protect the file with `chmod 600` and restart both services. Windows virtual accounts don't see a user's `setx` variables, so set the variable in both services' environments.

Quasar encrypts the saved token with its Data Protection keyring (`<install root>/DataProtection-Keys/`) and stores it in `<install root>/github-updates.json` (owner-only permissions on Linux). **Clear** is available only when a saved token exists. It removes that token; environment tokens still apply. The updater, the Magnetar release check and the cluster release check use the same token. Quasar also passes it to every managed Magnetar start in the `PULSAR_GITHUB_TOKEN` environment variable for plugin downloads, and uses it to read [plugin hubs](Plugins.md#plugin-hubs) other than MagnetarHub, so a private hub repository needs a token that can read it.

The token chip shows `not set`, `set`, `expires <date>`, `expires soon`, `expired` or `rejected`. Quasar warns 14 days before expiry, using the expiry date GitHub reports. The warnings are:

- "GitHub update token expires on <date>. Renew it before update checks start failing."
- "GitHub update token expired on <date>. Save a new token."
- "GitHub update token was rejected. It may be expired or revoked. Save a new token."

For a token from the environment they end in "Set a new token in <variable> and restart Quasar." instead. Saving or clearing a token on this page does not dismiss a warning for the overriding `QUASAR_GITHUB_TOKEN`; replace that variable and restart Quasar.

GitHub requests retry network errors and HTTP 408, 429 and 5xx up to four times, with delays from 1 second up to 30 seconds and respect for `Retry-After`.

## Update notices on the Notifications page

The bell in the app bar and the **Notifications** page (`/notifications`) list update notices for every signed-in user with at least the Viewer role. The bell links to the first notice and turns red when the GitHub token has expired.

| Notice title | When |
| --- | --- |
| Quasar update credentials | The GitHub token is expiring, expired or rejected |
| Quasar update available | A newer release exists: "Quasar UI <v> is ready to download." or "Quasar UI <v> is staged. Open Updates to activate." |
| Quasar launcher update available | A newer launcher package exists. On current installs this is the newer complete package, so it shows together with the notice above. |
| Cluster update available | A newer cluster package exists for a cluster you can query |
| Cluster content changes available | Mods or plugins of a deployed cluster changed |
| Quasar UI plugin update available | Admins only, opt-in |

Under **Notification preferences**, **Quasar and cluster updates** is on by default. **Quasar UI plugin updates** is off by default and only shown to admins. Preferences are per account. Browser push uses the same choices. Push and the instance alerts on that page are covered in [Monitoring](Monitoring.md).

During a planned restart, a restart progress panel replaces the normal lost-connection popup, checks health and version, and reloads the page. Clearing a failed restart brings back the normal reconnect handling. The normal reconnect dialog also checks for a replacement worker, including after native updates or updates started from another browser. Once that worker is ready, it reloads automatically. A temporary interruption to the same worker keeps the usual Retry and Resume controls without forcing a reload.

## Managed runtime warmup

At startup Quasar prepares SteamCMD, the Dedicated Server and Magnetar under `<install root>/ManagedRuntime/Tools/`:

| Component | Folder |
| --- | --- |
| SteamCMD | `ManagedRuntime/Tools/SteamCMD/` (Linux `HOME` for it: `ManagedRuntime/Tools/SteamCmdHome/`) |
| Dedicated Server | `ManagedRuntime/Tools/SpaceEngineersDedicatedServer/DedicatedServer64/` |
| Magnetar | `ManagedRuntime/Tools/Magnetar/` |

The Dashboard shows a **Managed Runtime** panel with progress for each component until all are ready. Running tasks on the Dashboard and Updates show the current step, the elapsed time and the download percentage SteamCMD reports. Steps without a percentage show a moving bar, and a note appears after 30 seconds. The elapsed time is not an estimate. A first-time download can take several minutes.

While warmup is not complete, starting a managed server stops with "Managed SteamCMD, Magnetar, and Dedicated Server runtime are still preparing." If warmup fails, the start shows the failure message instead, and the Dashboard shows a **Retry** button on a failed Dedicated Server or Magnetar row (needs permission to edit servers).

At startup Quasar downloads the DS only when `DedicatedServer64` is missing or incomplete. The DS download runs SteamCMD up to three times before it gives up. A valid install needs the dedicated server executable plus `VRage.dll`, `Sandbox.Game.dll` and `SpaceEngineers.Game.dll`. On Linux, warmup also prepares SteamCMD's `linux64` native runtime.

## Magnetar updates

Quasar asks `https://api.github.com/repos/CometWorks/magnetar/releases/latest` for the latest full release and picks the one asset matching `MagnetarForLinux-*.7z` (Linux) or `MagnetarForWindows-*.7z` (Windows). It compares the release tag and asset name with the marker file `.quasar-magnetar-release.json` in the Magnetar install folder. If they differ, it downloads the archive, deletes the install folder and installs the new build.

Checks run at startup, every hour, when you press **Check Magnetar**, and when a managed server starts (a release lookup is reused for 5 minutes). If the lookup or the install fails while an older Magnetar is installed, Quasar keeps the old one and shows "Using existing Magnetar runtime after update failure." Quasar does not restart running servers after a Magnetar update.

Overrides (env var, or key under `Quasar:ManagedRuntime`):

| Env var | Key | Purpose |
| --- | --- | --- |
| `QUASAR_MAGNETAR_RELEASE_API_URL` | `MagnetarReleaseApiUrl` | Other release endpoint |
| `QUASAR_MAGNETAR_ARCHIVE_ASSET_PATTERN` | `MagnetarArchiveAssetPattern` | Other asset name pattern |
| `QUASAR_MAGNETAR_ARCHIVE_URL` | `MagnetarArchiveUrl` | Fixed archive URL or `file://` path, skips the GitHub lookup |
| `QUASAR_MAGNETAR_INSTALL_DIR` | `MagnetarInstallDirectory` | Other install folder |

## Dedicated Server updates

Quasar updates the managed DS with SteamCMD (`+login anonymous +app_update 298740`). There is no timer. Updates happen:

- when a managed server starts, controlled by **Dedicated Server update on launch** on the Updates page (key `Quasar:ManagedRuntime:DedicatedServerLaunchUpdateMode`, env `QUASAR_DS_LAUNCH_UPDATE_MODE`):

  | Mode | Behavior |
  | --- | --- |
  | `Skip` | Use the installed DS as is. Fastest. |
  | `Update` (default) | Run `app_update`. Steam downloads only if a newer build exists. |
  | `Validate` | Run `app_update ... validate`, re-hashing the whole multi-GB install. Adds minutes to every start. |

- when you press **Check Dedicated Server**, which always runs `app_update 298740 validate`.

The launch mode applies only to the managed DS install. A server whose executable path points at its own `DedicatedServer64`, or a `QUASAR_DS64_PATH` override, uses that folder. Quasar does not restart running servers when the DS changes. Each server picks up the new build at its next start.

## Cluster Hosts and Docker

- Enrolled cluster Hosts update their `Quasar.Host` binary from Quasar through a `quasar-host-<id>-update.timer` systemd user timer (2 minutes after boot, then 15 minutes after each run). The local Host on the Quasar machine has no timer: Quasar restarts it when its binary or configuration changed, for example after an update. Cluster processes keep running in both cases. See [Cluster hosts](ClusterHosts.md).
- The Docker image sets `QUASAR_UPDATES_ENABLED=false`, so the in-app Quasar updater is off. Upgrade by pulling a new image (`docker compose pull` then `docker compose up -d`) or pin `QUASAR_VERSION`. Magnetar and DS updates inside the container work as described above. See [Docker](Docker.md).

## Completed update cleanup

On current installations, leave `Updates/operation.json` and `Updates/application-maintenance` in the data directory, and the retained application copy in `Updates/LauncherTransaction` under the install folder, alone. The updater needs them for recovery and replaces the retained copy at the next update.

Legacy Bootstrap installations clean up on their own. After the worker passes its startup health check, Bootstrap removes completed payloads from `<install root>/Updates/Staged/`: UI versions at or below the running UI version and `Bootstrap-<version>` folders at or below the running launcher version. Newer staged updates, the folder of the running worker, and folders named by the `.rollback` or `.previous` pointers stay. If a file cannot be removed, Bootstrap logs a warning and tries again after the next successful worker start.

## When an update fails

Read the operation ID, target, stage and message on Updates, with `Quasar update --status`, or in `<data directory>/Updates/operation.json`. The stages are `Queued`, `Downloading`, `Verified`, `Ready`, `Restarting`, `Healthy`, `Failed` and `Abandoned`.

- `Ready` with a "Staged safely" message means the native services are missing or not verified. Repair them with the installer, or stop Quasar, run `Quasar update --retry` and start Quasar by hand.
- `Failed` stays recorded after the previous application is restored. Fix the cause (permissions, service setup, download, settings conflict), then press **Retry update** or run `update --retry`. A retry downloads and verifies again.
- If Quasar restarts during `Downloading` or `Verified`, the operation fails with "Update preparation was interrupted. Use update --retry to download and verify again."
- If the updater stops during `Restarting`, it finishes or rolls back that update when it starts again, before it accepts a new one.

Don't delete `operation.json`, `application-maintenance` or `Updates/LauncherTransaction` to force an update. If the Quasar executable does not load at all, stop both services and use `Quasar activate-release --restore` (see [Command-line UI recovery](#command-line-ui-recovery)).

## Common problems

- **"Quasar 1.5 and later require a completed Quasar 1.4.1 migration first..."** This is an older package's removed gate. Install the current complete package directly; keep the real native-service migration and recovery precautions. See [Direct installation and upgrades](#direct-installation-and-upgrades).
- **"Release <v> has no complete quasar-linux-x64.tar.gz package..."** (or the Windows zip). The selected release has no complete package for this platform, or it is a prerelease while prereleases are off.
- **"The previous application replacement still needs recovery..."** The updater has not finished restoring an interrupted update. Make sure the updater service runs, then check `update --status`.
- **"Staged safely. This is a legacy Bootstrap installation..."** A Bootstrap installation cannot install the complete package from the UI. [Migrate](#migrating-from-bootstrap) with the installer.
- **"Quasar is not running under Bootstrap, so staged UI updates cannot be activated from the UI."** Shown by the legacy worker recovery controls. Current installations use **Update Quasar**.
- **"No Bootstrap update is available to activate."** Legacy recovery only. The last check found no newer launcher. Press **Check Quasar** first.
- **"Quasar UI <v> is already active."** You selected the running version.
- **"No GitHub release found."** The check found no release with your platform's asset. Check `Owner`, `Repository` and the asset names, and whether the release is a prerelease while prereleases are off.
- **Update checks fail with HTTP 403 or 429.** GitHub rate limit. Add a GitHub token.
- **"Update checks disabled."** `Quasar:Updates:Enabled` or `QUASAR_UPDATES_ENABLED` is `false` (always the case in Docker).
- **"Space Engineers Dedicated Server is not installed and could not be downloaded."** SteamCMD failed three times. Check disk space and network, then press **Retry** on the Dashboard.
- **"SteamCMD is not installed and could not be downloaded."** Quasar could not fetch SteamCMD from Valve's CDN.
- **A server shows "Bundled Quasar.Agent differs from the deployed Magnetar local DLL..."** Restart that server after a Quasar update.

## Sources
- `Docs/QuasarUpdates.md`
- `Docs/ReleaseNotes1.5.md`
- `Docs/Installation.md`
- `Docs/SecurityUpgradeDIS0042.md`
- `Docs/StateMachines/SelfUpdateAndRelease.md`
- `Docs/StateMachines/ManagedRuntimeProvisioning.md`
- `Docs/LinuxDeploymentAndUpdates.md`
- `Docs/WindowsDeploymentAndUpdates.md`
- `Docs/Configuration.md`
- `Docs/Docker.md`
- `Docs/BuildingAndDevelopment.md`
- `Dockerfile`
- `Directory.Build.props`
- `.github/workflows/release.yml`
- `.github/workflows/update-integration.yml`
- `scripts/cleanup-releases.py`
- `install.sh`
- `scripts/install.ps1`
- `Quasar/Program.cs`
- `Quasar/Services/QuasarApplicationEntry.cs`
- `Quasar/Services/Updates/QuasarUpdateService.cs`
- `Quasar/Services/Updates/QuasarUpdateOptions.cs`
- `Quasar/Services/Updates/QuasarUpdateSnapshot.cs`
- `Quasar/Services/Updates/UpdateNotice.cs`
- `Quasar/Services/Updates/PushNotificationService.cs`
- `Quasar/Services/Updates/NotificationPreferences.cs`
- `Quasar/Services/AgentSocketHandler.cs`
- `Quasar/Services/GitHubUpdateCredentialsCatalog.cs`
- `Quasar/Services/ManagedRuntimeWarmupService.cs`
- `Quasar/Services/ManagedDedicatedServerRuntimeResolver.cs`
- `Quasar/Services/ManagedRuntimeOptions.cs`
- `Quasar/Services/DedicatedServerSupervisor.cs`
- `Quasar/Services/DedicatedServerRuntimePreparer.cs`
- `Quasar/Services/ClusterReleaseMonitor.cs`
- `Quasar/Services/ClusterHostInstaller.cs`
- `Quasar/Services/ClusterLocalHost.cs`
- `Quasar/Services/Plugins/QuasarUiPluginHubCatalogService.cs`
- `Quasar/Services/Plugins/QuasarUiPluginHubRefreshService.cs`
- `Quasar/Services/WebServiceProbeEndpoints.cs`
- `Quasar/wwwroot/quasar-configs.js`
- `Quasar/Components/Pages/Updates.razor`
- `Quasar/Components/Pages/Notifications.razor`
- `Quasar/Components/Pages/Home.razor`
- `Quasar/Components/Layout/NavMenu.razor`
- `Quasar/Components/Layout/MainLayout.razor`
- `Quasar/Components/Layout/ReconnectModal.razor.js`
- `Quasar/Components/Shared/QuasarOperationLoader.razor`
- `Quasar/Components/Shared/QuasarOperationLoader.razor.css`
- `Quasar.Bootstrap/Program.cs`
- `Quasar.Bootstrap/UpdateCli.cs`
- `Quasar.Updater/Program.cs`
- `Quasar.Updater/QuasarUpdater.cs`
- `Quasar.Updater/TrustedUpdatePackage.cs`
- `Quasar.Updater/RecoverInstallation.cs`
- `Quasar.Updater/ConfigureInstallation.cs`
- `Shared/Updates/QuasarUpdateOperation.cs`
- `Shared/Updates/QuasarServiceInstallation.cs`
- `Shared/Updates/NativeQuasarServices.cs`
- `Shared/Updates/LauncherReplacement.cs`
- `Shared/Updates/VerifiedUpdatePackage.cs`
- `Shared/Updates/QuasarApplicationPackage.cs`
- `Shared/Updates/UpdateDurability.cs`
- `Shared/Updates/GitHubUpdateTokenReader.cs`
- `Shared/Updates/QuasarMaintenanceLease.cs`
- `Shared/ServiceHosting/NativeServiceFailures.cs`
- `Shared/Networking/GitHubRetryHandler.cs`
- `Magnetar.Protocol/Runtime/QuasarReleaseVersion.cs`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
- `Quasar.Tests/AutomaticUpdateTests.cs`
- `Quasar.Tests/BootstrapUpdateTests.cs`
- `Quasar.Tests/ReliableUpdateTests.cs`
- `Tests/Updates/systemd.py`
- `Tests/Updates/windows-services.ps1`
- `Quasar/Services/WebServiceOptions.cs`
- `Quasar/Services/QuasarShutdownService.cs`
- `Tests/Updates/NativeFixture.cs`
- `scripts/test_cleanup_releases.py`
- `Tests/Updates/Program.cs`
