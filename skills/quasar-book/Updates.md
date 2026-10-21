# Updates

How Quasar updates itself (the web UI worker and the Bootstrap launcher), how it keeps the managed Magnetar and Space Engineers Dedicated Server (DS) runtime current, and what to do when an update fails. Cluster Hosts and Docker installs have their own update paths, linked below.

## What gets updated and by whom

| Component | Source | Who checks | Who installs |
| --- | --- | --- | --- |
| Quasar UI worker (the web app) | GitHub releases of `CometWorks/quasar` | Worker, every 15 minutes and on **Check Quasar** | Worker stages, Bootstrap cuts over |
| Bootstrap launcher | Same GitHub release | Worker, same check | Bootstrap, only when the worker asks |
| Magnetar | Latest full GitHub release of `CometWorks/magnetar` | Worker: startup, every hour, **Check Magnetar**, each managed server start | Worker |
| Dedicated Server | Steam app `298740` through SteamCMD | Worker: startup (install only), each managed server start, **Check Dedicated Server** | Worker |
| Quasar UI plugins | QuasarHub catalog | Worker, every 15 minutes | Worker (opt-in), see [UI plugins](UiPlugins.md) |
| Cluster packages and Hosts | GitHub releases / Quasar origin | See [Cluster hosts](ClusterHosts.md) and [Clusters](Clusters.md) | |

The Quasar release checks need Linux or Windows. On other platforms the Updates page shows "Automatic updates are not supported on this platform."

## The Updates page

Open **Updates** in the nav menu (Settings group). The URL is `/settings/updates`. Only the Admin role can open it.

The page has these panels:

- **Quasar UI**: current version, a **Version to install** dropdown, and **Stage** and **Activate** buttons. Each entry in the dropdown is tagged `current`, `newer` or `rollback`, plus `prerelease` and `staged` where they apply.
- **Bootstrap**: current launcher version (or "not managed by Bootstrap") and a **Force activate** button when a newer launcher exists.
- **Cluster packages** (Linux, only when a cluster has a package selected): latest cluster release and **Check cluster release**. Release checks here only notify. Staging and updating a cluster happens on the cluster's page, see [Clusters](Clusters.md).
- **Managed Runtime**: installed Magnetar and Dedicated Server versions, status, install path and last check time, with **Check Magnetar** and **Check Dedicated Server** buttons, and the **Dedicated Server update on launch** setting.
- **Source**: GitHub owner/repository, check interval, release stream, the GitHub token field, **Include prerelease versions**, **Automatically update Quasar UI and Bootstrap together**, **Automatically stage UI updates after checks**, last check time and the asset names.

## How a Quasar UI update works

1. **Check.** The worker reads up to 100 releases from `https://api.github.com/repos/<Owner>/<Repository>/releases`, skips drafts, and skips prereleases unless prereleases are enabled. It finds the platform asset by name: `quasar-web-linux-x64.tar.gz` on Linux, `quasar-web-win-x64.zip` on Windows. The first check runs 10 seconds after startup, then every `CheckIntervalSeconds` (default 900).
2. **Stage.** Quasar downloads the asset into `<install root>/ManagedRuntime/Cache/`, verifies it against the release's `SHA256SUMS` file, extracts it to `<install root>/Updates/Staged/<version>/`, and merges `appsettings.json` (see below). With **Automatically stage UI updates after checks** on (the default), staging happens right after a check finds a newer version. With it off, the newer version waits as "ready to download" until you press **Stage**.
3. **Activate.** Press **Activate**. Quasar copies the staged files to `<install root>/ManagedRuntime/WebService/<version>/`, copies the resolved `appsettings.json` back to the install root, writes `<install root>/Updates/active-release.json`, and deletes other staged payloads.
4. **Cutover.** Bootstrap sees the pointer change, asks the old worker to drain, starts the new worker on the same port and waits up to 60 seconds for `/api/health`. The browser shows a progress overlay and reloads the Updates page when the new version answers. After a successful cutover Bootstrap deletes the other release folders under `ManagedRuntime/WebService/`.

Activation needs Quasar to run under Bootstrap (the installed systemd service or Scheduled Task). Otherwise the page shows "Quasar is not running under Bootstrap, so this update cannot be activated from the UI."

### Running game servers during a cutover

Managed servers keep running. Quasar starts them detached, so the web worker can restart under them. `Quasar.Agent` inside each server reconnects to the new worker, so expect a short disconnect in the UI.

A running server keeps the `Quasar.Agent.dll` it loaded at start. When the new release bundles a different agent DLL, the server shows "Bundled Quasar.Agent differs from the deployed Magnetar local DLL; restart this server manually to load the bundled agent." Quasar does not schedule that restart. Use the server's **Restart** action when convenient. Quasar then does a full stop and start and deploys the new agent.

**Urgent DIS-0042 upgrade:** save and stop worlds before activating this security release. Back up `cluster-credentials.json` together with `DataProtection-Keys/`, even for standalone-only installs. Update Quasar first, then every Host. Existing clusters must use **Stage profile changes** with the existing profile to re-pin the bundled agent, then **Apply pending update**; a node restart alone retains the old pin. Finally start standalone servers and verify authenticated telemetry everywhere. Follow the complete [security upgrade instructions](https://github.com/CometWorks/quasar/blob/main/Docs/SecurityUpgradeDIS0042.md).

Old tokenless agents are refused even on loopback. An adopted server stays disconnected until its agent can authenticate. Automatic attach recovery, health restarts and planned restarts wait for authenticated running telemetry; explicit admin actions remain possible. If you already upgraded with servers running, save in game before restarting because Quasar cannot save through a refused agent. An old standalone agent that had connected can save and stop after its own offline window (one hour by default). Current cluster agents never self-stop for a Quasar outage; Gateway/Host owns their lifecycle.

### Rolling back the UI

Pick an older version (tagged `rollback`) in **Version to install**, press **Stage**, then **Activate**. Automatic staging never picks an older version. The release workflow keeps only the two newest full releases on GitHub, so the dropdown offers few rollback targets.

### appsettings.json merge during staging

Staging does a three-way merge of `appsettings.json`:

- base: the previous release's defaults, stored in `<install root>/Updates/appsettings.base.json`
- current: your `<install root>/appsettings.json`
- incoming: the new release's `appsettings.json`

Settings that only you changed, or only the release changed, merge on their own. If both changed the same key differently, staging stops with status Failed and a message like "appsettings.json rollover conflict at <keys>. Resolve it manually or force the release defaults." The Updates page then shows the current and incoming files side by side and an editable **Final appsettings.json** box:

1. Press **Take current** or **Take incoming** to copy one of them into the final box (this does not save).
2. Edit the final box as needed. It must be valid JSON with no `<<<<<<<`, `=======` or `>>>>>>>` markers left, or saving fails with "Resolve all conflict markers before saving appsettings.json."
3. Press **Save resolution**. The version becomes staged and you can activate it.

Automatic activation never proceeds past an unresolved conflict.

## Bootstrap launcher updates

Bootstrap is the small launcher that owns the public port and runs the worker. It installs a new launcher only when the worker asks it to, by writing `<install root>/Updates/bootstrap-update-request.json`. That happens when you press **Force activate** on the Updates page, or through the automatic update policy below. Bootstrap does not apply releases on its own timer.

When Bootstrap handles a request it:

1. Finds the requested release and the installer asset (`quasar-installer-linux.tar.gz` or `quasar-installer-windows.zip`). It does nothing if that version is not newer than the running launcher.
2. Downloads it into `ManagedRuntime/Cache/`, verifies `SHA256SUMS`, and extracts it under `Updates/Staged/Bootstrap-<version>/`.
3. Skips the restart if the new launcher binary is byte-identical to the installed one.
4. Copies the archive files into the install root. It never overwrites the root `appsettings.json` or anything under `WebService/`. The old launcher binary stays next to it as `Quasar.previous` (`Quasar.exe.previous` on Windows).
5. Drains the worker (20 second grace) without stopping game servers, then restarts itself. On Linux it exits with code 75 and systemd restarts the service. On Windows it starts a detached `Quasar.exe serve --quiet` and exits with 0. If that replacement executable is missing, it exits with 75 and the Scheduled Task's restart-on-failure policy brings it back.

Bootstrap does not rerun `install.sh` or `install.ps1`, so service unit or Scheduled Task changes in a new release need a manual reinstall.

## Automatic updates (opt-in)

Two policies, both off by default and both needing Bootstrap:

| Setting (UI) | Page | Key in `Quasar:Updates` | Env override |
| --- | --- | --- | --- |
| Automatically update Quasar UI and Bootstrap together | Updates | `AutoApplyUpdates` | `QUASAR_UPDATES_AUTO_APPLY` |
| Automatically update installed UI plugins and restart Quasar | UI Plugins | `AutoUpdateUiPlugins` | `QUASAR_UPDATES_AUTO_UPDATE_UI_PLUGINS` |

With automatic Quasar updates on, every check:

- Picks the newest release that has both the UI asset and the Bootstrap asset at the same version, where at least one of them is newer than what runs now. It never downgrades either one.
- Stages the UI (including the appsettings merge), even if automatic staging is off. A merge conflict stops here.
- If the launcher is current, activates the staged UI as in a manual update.
- If the launcher is newer, sends Bootstrap one request with the prepared UI attached. Bootstrap installs itself first, then publishes the UI pointer and restarts into that release. A worker started by an older launcher does not attach the UI. In that case the launcher updates first and the next check activates the staged UI.

Running game servers stay up through all of this.

UI plugin auto-updates reuse the 15-minute QuasarHub catalog refresh. Only installed, enabled catalog plugins with a new commit and compatible requirements update. Successful updates restart the worker through Bootstrap. Quasar updates and plugin builds share a maintenance lock (`<install root>/maintenance.lock`), so one cannot interrupt the other. Details are in [UI plugins](UiPlugins.md).

## Update settings

All keys live under `Quasar:Updates` in `<install root>/appsettings.json`. The worker and Bootstrap both read that file. Environment variables override it.

| Key | Default | Env override | Notes |
| --- | --- | --- | --- |
| `Enabled` | `true` | `QUASAR_UPDATES_ENABLED` | `false` stops UI/Bootstrap release checks and Bootstrap update requests. Magnetar and DS checks still run. |
| `Owner` | `CometWorks` | `QUASAR_UPDATES_OWNER` | GitHub owner |
| `Repository` | `quasar` | `QUASAR_UPDATES_REPOSITORY` | GitHub repository |
| `IncludePrerelease` | `false` | `QUASAR_UPDATES_INCLUDE_PRERELEASE` | Also the **Include prerelease versions** switch |
| `AutoStageWebUpdates` | `true` | `QUASAR_UPDATES_AUTO_STAGE_WEB` | Also the **Automatically stage UI updates after checks** box |
| `AutoApplyUpdates` | `false` | `QUASAR_UPDATES_AUTO_APPLY` | See above |
| `AutoUpdateUiPlugins` | `false` | `QUASAR_UPDATES_AUTO_UPDATE_UI_PLUGINS` | See above |
| `CheckIntervalSeconds` | `900` | `QUASAR_UPDATES_CHECK_INTERVAL_SECONDS` | Values below 60 fall back to 900 |
| `LinuxWebAssetName` | `quasar-web-linux-x64.tar.gz` | `QUASAR_UPDATES_LINUX_WEB_ASSET` | |
| `LinuxBootstrapAssetName` | `quasar-installer-linux.tar.gz` | `QUASAR_UPDATES_LINUX_BOOTSTRAP_ASSET` | |
| `WindowsWebAssetName` | `quasar-web-win-x64.zip` | `QUASAR_UPDATES_WINDOWS_WEB_ASSET` | |
| `WindowsBootstrapAssetName` | `quasar-installer-windows.zip` | `QUASAR_UPDATES_WINDOWS_BOOTSTRAP_ASSET` | |

The switches and check boxes on the Updates page write to the install-root `appsettings.json` and apply at once in the worker. Bootstrap reads `IncludePrerelease` again only after it restarts.

Prerelease builds are for testing. The page shows a red warning while they are enabled, and asks for confirmation before turning them on.

## GitHub token for update checks

Quasar works without a token, but GitHub rate-limits unauthenticated API calls per IP address, which shared hosts and cloud IPs can hit. On a fresh install the limit can block the first UI download before there is a page to save a token on; Bootstrap then waits for GitHub's reset and continues, see [Install on Linux](InstallLinux.md#common-problems). To add one:

1. Open <https://github.com/settings/tokens/new> and create a classic personal access token. Give it a note such as `Quasar update checks`, pick an expiration you will renew, and select no scopes at all.
2. On **Updates**, paste it into **GitHub token** and press **Save token**.
3. Press **Check Quasar** to test it.

Quasar encrypts the token with its Data Protection keyring (`<install root>/DataProtection-Keys/`) and stores it in `<install root>/github-updates.json` (owner-only permissions on Linux). **Clear** removes it. Bootstrap, the Magnetar release check and the cluster release check use the same token. Quasar also passes it to every managed Magnetar start in the `PULSAR_GITHUB_TOKEN` environment variable for plugin downloads, and uses it to read [plugin hubs](Plugins.md#plugin-hubs) other than MagnetarHub, so a private hub repository needs a token that can read it.

The token chip shows `not set`, `set`, `expires <date>`, `expires soon`, `expired` or `rejected`. Quasar warns 14 days before expiry, using the expiry date GitHub reports. The warnings are:

- "GitHub update token expires on <date>. Renew it before update checks start failing."
- "GitHub update token expired on <date>. Save a new token."
- "GitHub update token was rejected. It may be expired or revoked. Save a new token."

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

## Managed runtime warmup

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

## When an update fails

Bootstrap protects the running UI:

- On a manual activation, if the new worker fails to answer `/api/health` within 60 seconds (or exits), Bootstrap kills it, starts the previous version again and rewrites `active-release.json` to it. The log says "Rolled Quasar worker back to version <v>."
- On a combined UI and Bootstrap update, Bootstrap saves the old pointer as `Updates/active-release.json.previous`. If the new UI cannot start after the launcher restart, Bootstrap restores it and logs "Coordinated UI update failed health checks; restoring <v>."
- After 3 worker startup failures within 5 minutes, Bootstrap creates `<install root>/ui-plugins.safe-mode` and tries once more with UI plugins disabled. See [UI plugins](UiPlugins.md).

What to do:

1. Read the status line at the top of **Updates**. A failed check or download shows the error there, with status Failed. Press **Check Quasar** to retry.
2. "Release asset has no SHA256SUMS entry." or "SHA256 mismatch for <file>." means the download did not match the published checksum. Retry later. Quasar never installs an unverified archive.
3. Check the logs. Bootstrap mirrors the worker's output to its own console, so on Linux run `journalctl --user -u quasar.service` (or `sudo journalctl -u quasar.service` for a `--system` install). Log locations are in [Troubleshooting](Troubleshooting.md).
4. For an appsettings conflict, resolve it on the Updates page as described above.
5. If the UI is gone entirely, restart the service: `systemctl --user restart quasar.service` on Linux, `Start-ScheduledTask -TaskName Quasar` on Windows. Bootstrap starts the release that `Updates/active-release.json` points to.

## Common problems

- **"Quasar is not running under Bootstrap, so staged UI updates cannot be activated from the UI."** Quasar was started directly (development run or a custom launch). Install and run it as the service or Scheduled Task.
- **"No Bootstrap update is available to activate."** The last check found no newer launcher. Press **Check Quasar** first.
- **"Quasar UI <v> is already active."** You selected the running version.
- **"No GitHub release found."** The check found no release with your platform's asset. Check `Owner`, `Repository` and the asset names, and whether the release is a prerelease while prereleases are off.
- **Update checks fail with HTTP 403 or 429.** GitHub rate limit. Add a GitHub token.
- **"Update checks disabled."** `Quasar:Updates:Enabled` or `QUASAR_UPDATES_ENABLED` is `false` (always the case in Docker).
- **"Space Engineers Dedicated Server is not installed and could not be downloaded."** SteamCMD failed three times. Check disk space and network, then press **Retry** on the Dashboard.
- **"SteamCMD is not installed and could not be downloaded."** Quasar could not fetch SteamCMD from Valve's CDN.
- **A server shows "Bundled Quasar.Agent differs from the deployed Magnetar local DLL..."** Restart that server after a Quasar update.

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
