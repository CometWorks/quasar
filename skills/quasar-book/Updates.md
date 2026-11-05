# Updates

How Quasar updates its unified application, how it keeps the managed Magnetar and Space Engineers Dedicated Server (DS) runtime current, and what to do when an update fails. Cluster Hosts and Docker installs have their own update paths, linked below.

**Required upgrade order:** install and successfully start the complete 1.4.1
migration release before 1.5 or later. Native service health (or successful startup
of the full manual package) records `Updates/migration-1.4.1.json`. A staged legacy
web worker is insufficient. Keep the receipt with the data directory. Missing or
invalid completion evidence blocks later staging, installation and startup.

### Broken 1.4.1.1 package

The published `v1.4.1.1` package records `1.4.1-1` in `release.json`, but its application reports `1.4.1.1`. Neither the foreground nor native-service path can record migration with those different identities. Its Windows installer can also remove file access when application and data share a folder. Do not rerun it. Use the corrected full 1.4.1.2 or newer 1.4.1 package once published, preserving paths and service names. Stop both services and any old task first; restore administrator file access if the old installer removed it. Keep data, credentials, keys and the recovery copy. Start the corrected bridge and confirm `Updates/migration-1.4.1.json` in the intended data folder before installing 1.5 or later. Do not fabricate the receipt. See [bridge recovery](https://github.com/CometWorks/quasar/blob/main/Docs/QuasarUpdates.md#broken-1411-package).

Release publication rejects the broken 1.4.1.1 bridge. Cleanup keeps the newest complete 1.4.1 bridge, including both platform packages, both legacy workers and checksums, even when a newer incomplete bridge exists.

## What gets updated and by whom

| Component | Source | Who checks | Who installs |
| --- | --- | --- | --- |
| Quasar application | GitHub releases of `CometWorks/quasar` | Quasar, every 15 minutes and on **Check Quasar** | Quasar stages; native updater verifies, replaces and health-checks |
| Native updater | Complete platform package | Installer migration/repair | Installer with services stopped |
| Magnetar | Latest full GitHub release of `CometWorks/magnetar` | Worker: startup, every hour, **Check Magnetar**, each managed server start | Worker |
| Dedicated Server | Steam app `298740` through SteamCMD | Worker: startup (install only), each managed server start, **Check Dedicated Server** | Worker |
| Quasar UI plugins | QuasarHub catalog | Worker, every 15 minutes | Worker (opt-in), see [UI plugins](UiPlugins.md) |
| Cluster packages and Hosts | GitHub releases / Quasar origin | See [Cluster hosts](ClusterHosts.md) and [Clusters](Clusters.md) | |

The Quasar release checks need Linux or Windows. On other platforms the Updates page shows "Automatic updates are not supported on this platform."

## The Updates page

Select a release and press **Update Quasar**. The complete platform package
contains the unified application, its updater and Host. The UI and CLI use one
durable operation. Advanced recovery shows status/retry instructions and, for
legacy Bootstrap installations selecting an older worker asset, explicit worker-only
recovery controls. Those controls do not migrate the installation.

The page persistently shows verified native restart capability. Linux checks the
actual owning systemd unit, action, account and failure policy. Windows checks
native main/updater service PIDs, executable actions, virtual accounts and SCM
recovery. A token, mode flag or `INVOCATION_ID` is insufficient.

## How a Quasar UI update works

The UI and server supervisor now run in one application process. The application
selects a complete package, verifies its checksum and layout, and merges settings.
The separate updater checks provenance independently using the installed trusted
repository, copies the archive into private staging, stops Quasar, replaces the
application, starts it and verifies the requested version and service PID through
a token-protected local health endpoint. It never replaces its own loaded files.
Updater upgrades use the platform installer from the same package, with both
services stopped. The package includes a guided installer using Windows UAC or
Linux polkit, plus manual installation options.

### Running game servers during a cutover

Application updates preserve detached game-server processes and persist adoption
state before Quasar stops. World and agent security migrations can still require
an explicit server restart. Follow [DIS-0042 migration instructions](https://github.com/CometWorks/quasar/blob/main/Docs/SecurityUpgradeDIS0042.md) when crossing that security boundary.

### Rolling back Quasar UI and Bootstrap

For current packages, selecting an older release and pressing **Update Quasar**
restores the complete application. There is no separate Bootstrap/UI pair to drift.
Only releases with the unified package layout and supported updater protocol can
be activated normally. Restoring an older Bootstrap-era installation requires its
retained complete migration backup and original supervision setup.

The updater retains a complete prior application and configuration until the new
release passes health checks. Failed launches and interrupted replacement restore
that snapshot. Disable automatic application to remain on an older release.

### Command-line UI recovery

Run `Quasar update --version <version>`, `Quasar update --status` or
`Quasar update --retry` from the installation, using `Quasar.exe` on Windows.
The CLI resolves the data directory from installation metadata and uses local
filesystem permissions. Exit 0 for an update means the requested application is
healthy and running; staging, failure and pending work have separate exit codes.

`Quasar --rollback` requests a retained complete application rollback through the
native updater. Older Bootstrap installations retain their previous worker-only
rollback and internal `activate-release` recovery command. Current internal file
recovery uses the retained updater with both services stopped. See the
[complete update and migration guide](https://github.com/CometWorks/quasar/blob/main/Docs/QuasarUpdates.md).

### appsettings.json merge during staging

The existing three-way merge uses prior release defaults, current operator settings
and incoming defaults. Conflicts stop activation. Updates shows both files and an
editable final resolution. Save valid JSON without conflict markers, then retry.
The resolution and failure survive reconnects and application restarts. Unix
settings containing credentials remain private. Update failure restores the prior
operator configuration together with the previous application.

## Bootstrap launcher updates

Current installations use native Quasar and updater services instead of Bootstrap.
Bootstrap remains a legacy recovery tool. Download the complete platform package
and follow the installer migration procedure; updating only an old UI worker does
not install the native updater or migrate a Scheduled Task.

## Automatic updates (opt-in)

**Automatically update Quasar** uses `Quasar:Updates:AutoApplyUpdates`, default off.
Automatic staging remains available through `AutoStageWebUpdates`. It stages the
same complete package and durable operation; **Update Quasar** activates that staged
operation without another download. Checks never downgrade automatically or replay
failed/completed operations. Conflicts require an administrator's resolution.

UI plugin auto-updates remain separately opt-in. Builds and application updates
share the existing maintenance gate and cross-process lease. A native updater
restarts the application after a successful plugin update; legacy Bootstrap worker
restarts remain supported during migration.

## Update settings

All keys live under `Quasar:Updates` in the configured data-directory `appsettings.json`. Environment variables override it.

| Key | Default | Env override | Notes |
| --- | --- | --- | --- |
| `Enabled` | `true` | `QUASAR_UPDATES_ENABLED` | `false` stops Quasar application release checks and update preparation. Magnetar and DS checks still run. |
| `Owner` | `CometWorks` | `QUASAR_UPDATES_OWNER` | GitHub owner |
| `Repository` | `quasar` | `QUASAR_UPDATES_REPOSITORY` | GitHub repository |
| `IncludePrerelease` | `false` | `QUASAR_UPDATES_INCLUDE_PRERELEASE` | Also the **Include prerelease versions** switch |
| `AutoStageWebUpdates` | `true` | `QUASAR_UPDATES_AUTO_STAGE_WEB` | Also the **Automatically stage Quasar updates after checks** box |
| `AutoApplyUpdates` | `false` | `QUASAR_UPDATES_AUTO_APPLY` | See above |
| `AutoUpdateUiPlugins` | `false` | `QUASAR_UPDATES_AUTO_UPDATE_UI_PLUGINS` | See above |
| `CheckIntervalSeconds` | `900` | `QUASAR_UPDATES_CHECK_INTERVAL_SECONDS` | Values below 60 fall back to 900 |
| `LinuxWebAssetName` | `quasar-web-linux-x64.tar.gz` | `QUASAR_UPDATES_LINUX_WEB_ASSET` | |
| `LinuxPackageAssetName` | `quasar-linux-x64.tar.gz` | `QUASAR_UPDATES_LINUX_PACKAGE_ASSET` | |
| `WindowsWebAssetName` | `quasar-web-win-x64.zip` | `QUASAR_UPDATES_WINDOWS_WEB_ASSET` | |
| `WindowsPackageAssetName` | `quasar-win-x64.zip` | `QUASAR_UPDATES_WINDOWS_PACKAGE_ASSET` | |

The switches and check boxes on Updates write to the data-directory `appsettings.json` and apply immediately. Repository/package trust-source changes require installer repair for the native updater.

Prerelease builds are for testing. The page shows a red warning while they are enabled, and asks for confirmation before turning them on.

## GitHub token for update checks

Cluster release discovery also works without a token for public `CometWorks/cluster` releases. The cluster page shows the result of the actual GitHub request; a missing token alone does not make the latest version unavailable. Private releases still require repository read access.

Quasar works without a token, but GitHub rate-limits unauthenticated API calls per IP address, which shared hosts and cloud IPs can hit. The UI is bundled; rate limits affect later release/runtime requests. To add one:

1. Open <https://github.com/settings/tokens/new> and create a classic personal access token. Give it a note such as `Quasar update checks`, pick an expiration you will renew, and select no scopes at all.
2. On **Updates**, paste it into **GitHub token** and press **Save token**.
3. Press **Check Quasar** to test it.

Token precedence for Quasar and its updater is `QUASAR_GITHUB_TOKEN`, then the saved token, then `GITHUB_TOKEN`. The page then says "Quasar uses the GitHub token from the <variable> environment variable"; a token in `QUASAR_GITHUB_TOKEN` wins over one saved on the page, so change the variable instead, while a saved token wins over `GITHUB_TOKEN`. Prefer the saved token shared by both native roles. For Linux environment overrides, add `EnvironmentFile=%h/.config/quasar-github-token.env` under `[Service]` to both user units, protect the file with `chmod 600` and restart both services. System units need an account-readable absolute path. Windows virtual accounts do not inherit an interactive user's `setx` environment; configure both service environments if using overrides.

Quasar encrypts the saved token with its Data Protection keyring (`<install root>/DataProtection-Keys/`) and stores it in `<install root>/github-updates.json` (owner-only permissions on Linux). **Clear** is available only when a saved token exists. It removes that token; environment tokens still apply. The native updater, Magnetar release check and cluster release check use the same token. Quasar also passes it to every managed Magnetar start in the `PULSAR_GITHUB_TOKEN` environment variable for plugin downloads, and uses it to read [plugin hubs](Plugins.md#plugin-hubs) other than MagnetarHub, so a private hub repository needs a token that can read it.

The token chip shows `not set`, `set`, `expires <date>`, `expires soon`, `expired` or `rejected`. Quasar warns 14 days before expiry, using the expiry date GitHub reports. The warnings are:

- "GitHub update token expires on <date>. Renew it before update checks start failing."
- "GitHub update token expired on <date>. Save a new token."
- "GitHub update token was rejected. It may be expired or revoked. Save a new token."

For a token from the environment they end in "Set a new token in <variable> and restart Quasar." instead.
Saving or clearing a token on this page does not dismiss a warning for the overriding `QUASAR_GITHUB_TOKEN`; replace that variable and restart Quasar.

GitHub requests retry network errors and HTTP 408, 429 and 5xx up to four times, with delays from 1 second up to 30 seconds and respect for `Retry-After`.

## Update notices on the Notifications page

The bell in the app bar and the **Notifications** page (`/notifications`) list update notices for every signed-in user with at least the Viewer role. The bell links to the first notice and turns red when the GitHub token has expired.

| Notice title | When |
| --- | --- |
| Quasar update credentials | The GitHub token is expiring, expired or rejected |
| Quasar update available | A newer UI exists: "Quasar UI <v> is ready to download." or "Quasar UI <v> is staged. Open Updates to activate." |
| Quasar launcher update available | A newer Bootstrap exists |
| Cluster update available | A newer cluster package exists for a cluster you can query |
| Cluster content changes available | Mods or plugins of a deployed cluster changed |
| Quasar UI plugin update available | Admins only, opt-in |

Under **Notification preferences**, **Quasar and cluster updates** is on by default. **Quasar UI plugin updates** is off by default and only shown to admins. Preferences are per account. Browser push uses the same choices. Push and the instance alerts on that page are covered in [Monitoring](Monitoring.md).

During a planned restart, the restart progress panel replaces the normal lost-
connection popup and owns health/version checks and page reload. Clearing a failed
restart restores normal reconnection handling. Unexpected connection loss keeps
the usual Retry and Resume controls.

## Managed runtime warmup

Active runtime tasks on Dashboard and Updates show the current activity, elapsed
time and reported download percentages. Checks and installation steps without a
percentage use an indeterminate bar. A note appears after 30 seconds; elapsed
time is not a completion estimate.

At startup Quasar prepares SteamCMD, the Dedicated Server and Magnetar under `<install root>/ManagedRuntime/Tools/`:

| Component | Folder |
| --- | --- |
| SteamCMD | `ManagedRuntime/Tools/SteamCMD/` (Linux `HOME` for it: `ManagedRuntime/Tools/SteamCmdHome/`) |
| Dedicated Server | `ManagedRuntime/Tools/SpaceEngineersDedicatedServer/DedicatedServer64/` |
| Magnetar | `ManagedRuntime/Tools/Magnetar/` |

The Dashboard shows a **Managed Runtime** panel with progress for each component until all are ready. While warmup is not complete, starting a managed server stops with "Managed SteamCMD, Magnetar, and Dedicated Server runtime are still preparing." If warmup fails, the start shows the failure message instead, and the Dashboard shows a **Retry** button on a failed Dedicated Server or Magnetar row (needs permission to edit servers).

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

- Enrolled cluster Hosts update their `Quasar.Host` binary from Quasar through a `quasar-host-<id>-update.timer` systemd user timer (2 minutes after boot, then 15 minutes after each run). The local Host on the Quasar machine has no timer: after an update Quasar restarts it from the new release. Cluster processes keep running in both cases. See [Cluster hosts](ClusterHosts.md).
- The Docker image sets `QUASAR_UPDATES_ENABLED=false`, so the in-app Quasar updater is off. Upgrade by pulling a new image (`docker compose pull` then `docker compose up -d`) or pin `QUASAR_VERSION`. Magnetar and DS updates inside the container work as described above. See [Docker](Docker.md).

## Completed update cleanup

Current installations retain the durable operation and complete prior application
transaction. Do not remove files involved in pending recovery. Installer upgrade
owns the updater binaries. The following cleanup behavior is specific to legacy
Bootstrap installations.

After the worker passes its startup health check, Bootstrap automatically removes
completed payloads from `<install root>/Updates/Staged/`. UI versions at or below
the running UI version and `Bootstrap-<version>` folders at or below the running
launcher version are removed. This runs after manual and combined updates and
on ordinary startup, so older leftovers are cleared too.

Newer staged updates stay available for activation. A directory containing the
running worker is also kept, including older installs that run from Staged.
Cleanup also preserves directories referenced by the persistent `.rollback`
pointer or the transient `.previous` recovery pointer.
Failed worker starts do not trigger cleanup. If files cannot be removed,
Bootstrap logs a warning and retries after the next successful worker start.

## When an update fails

Read the operation ID, target, stage and error on Updates or in
`Updates/operation.json`. `Ready` can mean a safe manual stage or unavailable native
restart capability. `Failed` remains recorded after the prior application is
restored. Correct permissions, supervision, download or configuration problems,
then choose **Retry update**. Do not delete the request/journal/maintenance files to
force activation. Completed and abandoned operations are not replayed.

Foreground launches can stage safely. Stop Quasar and any registered updater,
run `Quasar update --retry`, then start Quasar manually. If the executable cannot
load, stop both native services and use the retained updater recovery command.
The previous complete application is under `Updates/LauncherTransaction` in the
application directory. Do not remove it while an operation needs recovery.

## Common problems

- **"Quasar is not running under Bootstrap, so staged UI updates cannot be activated from the UI."** Quasar was started directly (development run or a custom launch). This message belongs to legacy worker recovery. Migrate to native services for current application updates.
- **"No Bootstrap update is available to activate."** The last check found no newer launcher. Press **Check Quasar** first.
- **"Quasar UI <v> is already active."** You selected the running version.
- **"No GitHub release found."** The check found no release with your platform's asset. Check `Owner`, `Repository` and the asset names, and whether the release is a prerelease while prereleases are off.
- **Update checks fail with HTTP 403 or 429.** GitHub rate limit. Add a GitHub token.
- **"Update checks disabled."** `Quasar:Updates:Enabled` or `QUASAR_UPDATES_ENABLED` is `false` (always the case in Docker).
- **"Space Engineers Dedicated Server is not installed and could not be downloaded."** SteamCMD failed three times. Check disk space and network, then press **Retry** on the Dashboard.
- **"SteamCMD is not installed and could not be downloaded."** Quasar could not fetch SteamCMD from Valve's CDN.
- **A server shows "Bundled Quasar.Agent differs from the deployed Magnetar local DLL..."** Restart that server after a Quasar update.

Dedicated Server downloads and validation report SteamCMD percentages as they happen.
When SteamCMD provides no percentage, the operation loader shows the current stage
and elapsed time. First-time preparation can take several minutes.

Release package metadata uses the application's reported version, including
four-part CI build identities. NuGet's internal version is separate and must not
be used to identify the running release or its 1.4.1 migration receipt.

## Sources
- `Docs/SecurityUpgradeDIS0042.md`
- `Docs/StateMachines/SelfUpdateAndRelease.md`
- `Docs/StateMachines/ManagedRuntimeProvisioning.md`
- `Docs/LinuxDeploymentAndUpdates.md`
- `Docs/WindowsDeploymentAndUpdates.md`
- `Docs/Configuration.md`
- `Docs/Docker.md`
- `Dockerfile`
- `.github/workflows/release.yml`
- `scripts/cleanup-releases.py`
- `Quasar/Services/Updates/QuasarUpdateService.cs`
- `Quasar/Services/AgentSocketHandler.cs`
- `Quasar/Services/Updates/QuasarUpdateOptions.cs`
- `Quasar/Services/Updates/QuasarUpdateSnapshot.cs`
- `Quasar/Services/Updates/UpdateNotice.cs`
- `Quasar/Services/Updates/PushNotificationService.cs`
- `Quasar/Services/Updates/NotificationPreferences.cs`
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
- `Quasar/Program.cs`
- `Quasar/Services/WebServiceProbeEndpoints.cs`
- `Quasar/wwwroot/quasar-configs.js`
- `Quasar/Components/Pages/Updates.razor`
- `Quasar/Components/Pages/Notifications.razor`
- `Quasar/Components/Pages/Home.razor`
- `Quasar/Components/Layout/NavMenu.razor`
- `Quasar/Components/Layout/MainLayout.razor`
- `Quasar.Bootstrap/Program.cs`
- `Shared/Updates/QuasarMaintenanceLease.cs`
- `Shared/Networking/GitHubRetryHandler.cs`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
- `Quasar.Tests/AutomaticUpdateTests.cs`
- `Quasar.Tests/BootstrapUpdateTests.cs`
- `Quasar/Components/Shared/QuasarOperationLoader.razor`
- `Quasar/Components/Shared/QuasarOperationLoader.razor.css`
- `Quasar/Components/Layout/ReconnectModal.razor.js`
- `Directory.Build.props`

- `Docs/QuasarUpdates.md`
- `Quasar.Updater/Program.cs`
- `Quasar.Updater/QuasarUpdater.cs`
- `Quasar.Updater/TrustedUpdatePackage.cs`
- `Quasar.Updater/ConfigureInstallation.cs`
- `Quasar/Services/QuasarApplicationEntry.cs`
- `Quasar.Bootstrap/UpdateCli.cs`
- `Shared/Updates/QuasarUpdateOperation.cs`
- `Shared/Updates/QuasarServiceInstallation.cs`
- `Shared/Updates/NativeQuasarServices.cs`
- `Shared/Updates/LauncherReplacement.cs`
- `Shared/Updates/VerifiedUpdatePackage.cs`
- `Shared/Updates/QuasarApplicationPackage.cs`
- `Shared/Updates/UpdateDurability.cs`
- `Quasar.Tests/ReliableUpdateTests.cs`
- `Tests/Updates/systemd.py`
- `Tests/Updates/windows-services.ps1`
- `.github/workflows/update-integration.yml`

- `Shared/Updates/GitHubUpdateTokenReader.cs`

- `Shared/ServiceHosting/NativeServiceFailures.cs`

- `Docs/Installation.md`

- `Shared/Updates/QuasarUpgradePath.cs`

- `Docs/BuildingAndDevelopment.md`
- `scripts/test_cleanup_releases.py`
- `Tests/Updates/Program.cs`
