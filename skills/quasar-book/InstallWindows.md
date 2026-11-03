# Install on Windows

How to download, run and install Quasar on Windows: the release package, the guided installer, foreground runs, the `install.ps1` Windows services, moving from the old Scheduled Task, where Quasar keeps its files, uninstalling and upgrading. For Linux see [Install on Linux](InstallLinux.md).

Quasar is one application: the web UI and the server supervisor run in the same `Quasar.exe` process. A small separate updater, `Updater\Quasar.Updater.exe`, replaces the application during updates. The installer registers both as Windows services, `Quasar` and `QuasarUpdater`, under passwordless virtual accounts. Older versions ran a Bootstrap launcher from a Scheduled Task; current packages only migrate from it.

**Required upgrade order.** Quasar 1.5 and later start only after a completed 1.4.1 migration. Install the complete 1.4.1 package with your existing paths and service or task name, and start it successfully. That writes `Updates\migration-1.4.1.json` in the data folder. A 1.4.1 web worker staged by an old Bootstrap install does not count. Without a valid receipt, the 1.5 installer, the updater and Quasar itself refuse with `Quasar 1.5 and later require a completed Quasar 1.4.1 migration first. ...`. This applies to new installations too: a new installation that will run 1.5 starts with 1.4.1. Keep the receipt with the data folder. Don't write one by hand.

## Release assets

Every GitHub release (`https://github.com/CometWorks/quasar/releases`) carries:

| Asset | Contents |
| --- | --- |
| `quasar-win-x64.zip` | One top-level `Quasar\` folder: `Quasar.exe` with its UI assets (`wwwroot\`), `Agent\`, `Host\`, `Updater\Quasar.Updater.exe`, `install.ps1`, `uninstall.ps1`, `Install.cmd`, `install-gui.ps1`, the default `appsettings.json`, `release.json`, `launcher-files.txt`, `README.md` and `INSTALL.md` |
| `quasar-linux-x64.tar.gz` | The Linux package, see [Install on Linux](InstallLinux.md) |
| `SHA256SUMS` | SHA-256 of the archives |

Verify the zip against `SHA256SUMS` before you extract it. The UI is bundled, so the first start downloads nothing for Quasar itself; SteamCMD, the Dedicated Server, Magnetar and plugins still come from the network. The legacy `quasar-web-*` worker archives exist only on the 1.4.1 migration release, so old Bootstrap installs pick that release. Debug symbols are CI artifacts, not release assets.

`Quasar.exe`, `Quasar.Updater.exe` and the Host are self-contained single-file x64 programs.

## Guided installation

Open **Install.cmd** in the extracted `Quasar` folder. It starts `install-gui.ps1` in Windows PowerShell with the execution policy bypassed for that run. The **Install Quasar** dialog has two editable folders:

| Field | Default |
| --- | --- |
| Application folder | `%ProgramFiles%\Quasar` |
| Data folder (worlds and settings) | `%ProgramData%\Quasar` |

Choose **Install** and approve the UAC prompt. The elevated part runs `install.ps1 -InstallDir <application> -DataDir <data> -Start` and shows `Quasar and its updater are installed and running.` with the address (`http://localhost:8080` by default). Keep the extracted package until you have checked the installation.

When a service named `Quasar` already exists, the folder fields show `Existing installation (read after administrator approval)`. After UAC the installer reads the installation record and keeps its folders and service names. Custom service names, a service under a named account and a Scheduled Task migration need the manual install. Canceling UAC installs nothing. On failure an error dialog shows the message, and the console window waits for a key; run `install.ps1` from an elevated PowerShell to keep the full output. **Manual install** shows the commands.

The dialog refuses to run from an incomplete extraction: `Extract the complete Windows platform package first, then open Install.cmd from its Quasar folder.`

## Prerequisites

- Windows x64.
- An elevated PowerShell (Run as Administrator) for `install.ps1`, `uninstall.ps1` and the `Quasar.exe update` commands. The guided installer asks for elevation itself.
- No .NET installation for the packaged programs. `install.ps1` does not check for one. A source checkout needs the .NET 10 SDK, because the script runs `dotnet publish`.
- A .NET 10 SDK only if an administrator installs a source-built QuasarHub UI plugin. Quasar then uses a .NET 10 SDK on `PATH`, or asks whether to download a pinned private SDK into the data folder's `ManagedRuntime\Tools\DotNetSdk\`. You can also install the SDK with WinGet instead. See [UI plugins](UiPlugins.md).
- No service account password. The services use virtual accounts.
- Git is not required.

## Run in the foreground

```powershell
Expand-Archive quasar-win-x64.zip -DestinationPath C:\quasar
cd C:\quasar\Quasar
.\Quasar.exe serve --foreground
```

Quasar prints `Quasar` and its URL (`http://127.0.0.1:8080` with the shipped settings) and opens it in the default browser. State lives in the extracted folder; logs go to `Logs\quasar.log` there.

- Ctrl+C stops Quasar. Managed servers keep running if `Quasar:PreserveManagedServersOnShutdown` is on (the default).
- **Shutdown Quasar** saves the server state and stops Quasar; servers keep running.
- **Restart Quasar** is not available, because no service brings Quasar back.
- An update started from the UI stages the release but can't swap the running files. Stop Quasar, run `.\Quasar.exe update --retry`, then start it again. See [Updates](Updates.md).

`.\Quasar.exe serve --headless` runs the API-only mode. The commands and flags are the same as on Linux; see [Install on Linux](InstallLinux.md#launcher-commands-and-flags).

## Install native Windows services

From an elevated PowerShell in the extracted `Quasar` folder:

```powershell
.\install.ps1 -Start
Get-Service Quasar, QuasarUpdater
```

Without `-InstallDir`, a packaged install stays in the extracted folder, and the data folder is that folder too, because it holds `appsettings.json`. Pass `-InstallDir` and `-DataDir` to use other folders, as the guided installer does:

```powershell
.\install.ps1 -InstallDir 'C:\Program Files\Quasar' -DataDir 'C:\ProgramData\Quasar' -Start
```

What the installer does, in order:

1. Checks elevation, the service names and the folders. It refuses a drive root, `%SystemRoot%`, `%ProgramFiles%`, `%ProgramFiles(x86)%`, `%ProgramData%` and `%USERPROFILE%` themselves.
2. Checks the 1.4.1 migration requirement and refuses an existing Scheduled Task with the same name unless `-MigrateScheduledTask` is given.
3. Refuses existing services that run under another account, and stops existing `Quasar` and `QuasarUpdater` services.
4. Copies the previous application files to a recovery folder in `%TEMP%` when it replaces an existing installation, then copies the new files. It checks first that no `Quasar.exe` of this installation is running. An existing `appsettings.json` is kept; a new data folder gets the default one.
5. Registers both services to start automatically, under `NT SERVICE\Quasar` and `NT SERVICE\QuasarUpdater`, with restart-on-failure recovery.
6. Writes the installation record and sets folder permissions (see [Where Quasar keeps its files](#where-quasar-keeps-its-files)).
7. Gives `NT SERVICE\QuasarUpdater` the right to query, start, stop and interrogate the `Quasar` service, and no other service rights. Existing audit rules on the service are kept, and repeating the install doesn't add the grant twice.
8. With `-Start`, starts the updater and then Quasar, and waits up to 90 seconds for the health endpoint to report the new version and the service's process ID.

On success it prints `Installed native services Quasar and QuasarUpdater. Data: <folder>. Use Quasar.exe update --status for supervision and update status.`

### install.ps1 parameters

| Parameter | Default | Effect |
| --- | --- | --- |
| `-Start` | off | Starts both services and health-checks Quasar. Required with `-MigrateScheduledTask`. |
| `-InstallDir <dir>` | the script's folder for release zips; `%ProgramFiles%\Quasar` for source checkouts | Application folder |
| `-DataDir <dir>` | the existing installation's data folder; else the application folder when it holds `appsettings.json`; else `<InstallDir>\Data` | Data folder |
| `-ServiceName <name>` | `Quasar` | Main service name. An existing installation must keep its names. |
| `-UpdaterServiceName <name>` | `<ServiceName>Updater` | Updater service name |
| `-NoEnable` | off | Sets both services to Manual start |
| `-RepairSupervision` | off | Rewrites services, installation record and permissions for files already in place, without copying binaries |
| `-MigrateScheduledTask`, `-TaskName <name>` | off, `<ServiceName>` | Moves an old Scheduled Task installation to the services, see below |
| `-NoBuild`, `-Configuration <name>`, `-Runtime <rid>` | off, `Release`, `win-x64` | Source checkouts only: publish settings |

The installer does not create firewall rules.

### The services

| Setting | `Quasar` | `QuasarUpdater` |
| --- | --- | --- |
| Command | `"<InstallDir>\Quasar.exe" serve --service` | `"<InstallDir>\Updater\Quasar.Updater.exe" --installation "<record>"` |
| Account | `NT SERVICE\Quasar` | `NT SERVICE\QuasarUpdater` |
| Start type | Automatic (Manual with `-NoEnable`) | Automatic (Manual with `-NoEnable`) |
| Recovery | Restart after 5 s, 15 s, 60 s; counter resets after one day | Same |

Recovery also applies when a service stops with an error code, but a clean stop stays stopped. Each service registers an Application event log source with its own name. Manage them with `Get-Service`, `Start-Service`, `Stop-Service` and `Restart-Service`.

Quasar checks this wiring on start and shows the result on Updates: the `Quasar` service must own the running process, both commands and accounts must match the installation record, and both services need the restart recovery. When something doesn't match, Quasar reports that it can't restart itself; the fix is `install.ps1 -RepairSupervision` with the existing folders and names.

## Install as a Scheduled Task

Quasar no longer installs as a Scheduled Task. Older versions ran Bootstrap from a task named `Quasar`; the current installer moves such an installation to the services.

1. Save the worlds, then stop the old task and its game servers. The installer refuses a running task: `Stop the old Quasar task and its game servers before migration. ...`
2. Back up `appsettings.json`, credentials and `DataProtection-Keys\` together, and keep a copy of the old installation.
3. Extract the new package to a temporary folder and run, from an elevated PowerShell:

```powershell
.\install.ps1 -InstallDir 'C:\Quasar' -ServiceName Quasar -MigrateScheduledTask -TaskName Quasar -Start
```

The task must run `<InstallDir>\Quasar.exe` directly with `<InstallDir>` as its working folder, otherwise the installer stops with `The selected legacy task does not directly run this installation. ...`. The old install kept its data in its own folder, so the data folder stays there. A pending Bootstrap activation in `Updates\bootstrap-update-request.json` is abandoned and renamed to `.migrated`.

Game servers started by the task may belong to the task's job object, so moving to the services can need one planned server restart. Later service updates keep servers running.

### The Scheduled Task

The installer disables the task before it starts the services and keeps it disabled after a successful migration. Don't enable the task and the services together. If migration fails, the installer restores the previous application and the task's earlier enabled state. It leaves the task disabled when a Bootstrap activation request could not be set aside, and says so. `uninstall.ps1` removes a task with the same name. Remove it yourself once the services and their recovery work.

The old task's account and password don't carry over: the services run as their virtual accounts. Grant `NT SERVICE\Quasar` access to worlds, runtime folders, backup paths or network shares outside the application and data folders before you start migrated servers. A service that already runs under a named account is refused: `Service <name> uses another account. Migrate its account explicitly before using this virtual-account installer.`

### Shutdown Quasar and other power actions

The power button in the top bar opens **Quasar power** (admin role):

- **Shutdown Quasar** saves the managed server state and stops Quasar cleanly. The service stays stopped; servers keep running without supervision until their agent's offline policy takes over (see [Configuration](Configuration.md#shutdown-and-agent-behavior)). Run `Start-Service Quasar` to bring the UI back.
- **Restart Quasar** hands the restart to the updater service. Servers keep running. Restart is enabled only after native service verification succeeds for the current Quasar process. If verification is missing, stale, unreadable or denied, the button stays disabled and shows repair guidance. An unsupported restart request leaves Quasar running. Wait for the next verification on Updates or repair supervision with the current installer, preserving paths, service names, account and scope. A pending update must finish before a manual restart can be queued.
- **Save and stop all servers** saves and stops standalone servers and managed clusters, setting their goals to Off. Quasar stays online and shows progress.

`Stop-Service Quasar` follows `Quasar:PreserveManagedServersOnShutdown`. Updates always keep servers running and save their state for the next Quasar to adopt.

## Where Quasar keeps its files

The application folder and the data folder can differ. `QUASAR_INSTALL_DIR` is the data folder; the installation record sets it for the services.

| Location | Contents |
| --- | --- |
| Application folder | `Quasar.exe`, `wwwroot\`, `Agent\`, `Host\`, `release.json`, `launcher-files.txt`, `install.ps1`, `uninstall.ps1` |
| `<application>\Updater\` | The updater. Application updates don't replace it; the installer does. |
| `<application>\service-installation.json` | Points to the installation record |
| `<application>\Updates\LauncherTransaction\` | The complete previous application, kept until an update passes its health check |
| `<application>\.update-cache\` | The updater's private copy of a downloaded package |
| `%ProgramData%\Quasar-ServiceInstallations\<ServiceName>.json` | Installation record: folders, service names, accounts, trusted release repository and package, health URL and token |
| Data folder | `appsettings.json`, `rbac.json`, `Logs\quasar.log`, servers, templates, clusters, backups, `ManagedRuntime\`, `Diagnostics\`, `DataProtection-Keys\` and the other files listed in [Install on Linux](InstallLinux.md#where-quasar-keeps-its-files) |
| `<data>\Updates\` | `operation.json` (update status), `migration-1.4.1.json` (migration receipt), staged packages |

Permissions set by the installer:

- Data folder: inherited permissions removed; full control for SYSTEM and Administrators, Modify for both service accounts. Unrelated explicit rules, including denies, stay in place.
- Application folder: Read and Execute for `NT SERVICE\Quasar`, Modify for `NT SERVICE\QuasarUpdater`, full recovery access for SYSTEM and Administrators. Co-located data keeps the main account's Modify grant; nested data keeps its own Modify grant.
- `Updater\`: owned by Administrators, full control for SYSTEM and Administrators, Read and Execute for the updater account only. The application account can't change it.
- `.update-cache\`: private to SYSTEM, Administrators and the updater account; only the updater has Modify service access.
- The installation record and its folder: full control for SYSTEM and Administrators, read for both service accounts.

Files receive direct effective grants; directory inheritance flags apply only to directories. Repair skips protected updater, cache and metadata paths when applying ordinary grants, then restores their restricted ACLs. Recursive ACL changes reject reparse points.

Back up the data folder as one unit, with the keys. Quasar writes its log to `Logs\quasar.log` in the data folder.

## Network ports

| Port | Default | Notes |
| --- | --- | --- |
| Web UI and API (TCP) | `8080` | `Quasar:Host` / `Quasar:Port` in the data folder's `appsettings.json`. After a change, stop both services and rerun `install.ps1 -RepairSupervision -Start` with the existing folders and names, so the updater's health check uses the same address. |
| Game server port (UDP) | first free port from `27016` | Set per server |
| DS Steam port | game port + 1000 | Written into the DS config |
| DS Remote API port | game port + 2000 | Written into the DS config |

The updater calls `/api/internal/update-health` on a local address with the token from the installation record. Neither the installer nor Quasar adds firewall rules. See [Access control](AccessControl.md) before exposing the web port.

## Uninstall

```powershell
.\uninstall.ps1           # stop and remove both services and any task with the same name
.\uninstall.ps1 -Purge    # also delete the application folder
```

| Parameter | Default | Effect |
| --- | --- | --- |
| `-ServiceName <name>` | `Quasar` | Main service to remove |
| `-UpdaterServiceName <name>` | `<ServiceName>Updater` | Updater service to remove |
| `-TaskName <name>` | `<ServiceName>` | Scheduled Task to stop and unregister, if it exists |
| `-InstallDir <dir>` | from the installation record; else the script's folder; else `%ProgramFiles%\Quasar` | Folder `-Purge` deletes |
| `-Purge` | off | Deletes the application folder |

The script stops the updater, then Quasar, deletes both services, removes the task and the installation record, and prints `Native services removed. Data is retained unless -Purge was supplied. Detached game servers require their own explicit shutdown.` `-Purge` deletes only the application folder: a separate data folder, such as the guided default `%ProgramData%\Quasar`, stays. Stop the game servers first if you want them stopped.

## Upgrading

Use **Update Quasar** on the Updates page, or from an elevated PowerShell in the application folder:

```powershell
.\Quasar.exe update --version <release>   # the full version shown on the release page
.\Quasar.exe update --status
.\Quasar.exe update --retry               # after fixing a reported failure
```

Quasar downloads the complete package and checks it against `SHA256SUMS`. The updater service checks its own copy of the archive, stops only the `Quasar` service, keeps the complete previous application, replaces the files, starts Quasar and confirms the exact version and process ID through the health endpoint. A failed start restores the previous application. Game servers keep running. Details, exit codes and recovery are on [Updates](Updates.md).

The updater does not replace its own files. To upgrade it, run the new package's `install.ps1` from an elevated PowerShell with the existing folders and names, for example `.\install.ps1 -InstallDir 'C:\Program Files\Quasar' -DataDir 'C:\ProgramData\Quasar' -Start`. The installer stops both services itself.

### Move the install root

Stop both services and the managed servers. Copy the application folder and the data folder, keys included, to the new places, then run `install.ps1 -RepairSupervision -InstallDir <new-application> -DataDir <new-data> -Start` with the existing service names. Check the Updates page and `update --status` before you delete the old copy.

Blank managed server paths and relative overrides follow the new root automatically. Absolute overrides stay at their configured external locations. Older `server.json` files may hold absolute defaults; opening and saving the server rewrites them under the current root. If both the old and new server data trees exist, copy the authoritative one first, then use **Edit Server -> Paths -> Use Managed Defaults**.

## Cluster machines

The package contains `Host\`, but the local Host executor runs only on Linux x64, and cluster setup needs Quasar on Linux; the **Cluster** choice in **New server** is grayed out on Windows. See [Cluster hosts](ClusterHosts.md).

## Common problems

When `install.ps1` fails, it prints `Installation failed before recovery: <message>` first, then stops the new services, sets them to Manual, and restores the previous application. If restoring fails, it says `Automatic restoration is incomplete. Retained recovery copy: <folder>.` and leaves the old task disabled; keep the services and the task stopped until you have restored the files from that copy. The original error is repeated at the end.

| Message | Cause and fix |
| --- | --- |
| `Run this installer in an elevated PowerShell.` | Open PowerShell with Run as Administrator. |
| `Upgrade prerequisite failed. Complete and start the 1.4.1 migration package before installing 1.5 or later.` | Install and start the complete 1.4.1 package first. |
| `Use the existing service names when repairing/upgrading this installation.` | Pass the `-ServiceName` and `-UpdaterServiceName` the installation already uses. |
| `Choose a dedicated Quasar folder, not a system or drive root: <dir>` | Pick a subfolder. |
| `An existing Scheduled Task needs -MigrateScheduledTask. ...` | A task with the service name exists. Follow [Install as a Scheduled Task](#install-as-a-scheduled-task). |
| `Scheduled Task migration requires -Start so the replacement can be health-checked before completion.` | Add `-Start`. |
| `Service <name> uses another account. ...` | Move the service to its virtual account yourself first, or use another service name. |
| `Stop the existing Quasar application before copying files.` | A `Quasar.exe` of this installation still runs. Stop it. |
| `Use a complete current platform package.` / `Updater missing. Replace the complete platform package before repair.` | The folder lacks `launcher-files.txt` or `Updater\Quasar.Updater.exe`. Extract the complete zip. |
| `The new main service failed health checks. ...` | Quasar didn't report the new version within 90 seconds. Read `Logs\quasar.log` in the data folder and the Application event log, then retry. |
| `Cannot grant updater service-control permissions: <sc.exe output>` | Windows refused the service permission change. The output says why. |
| `Cannot read this installation: ...` from `Quasar.exe` | The shell isn't elevated, so it can't read the installation record. |

If service startup reports access denied to `release.json`, or automatic recovery cannot overwrite application files, keep the reported recovery copy and repair supervision with the current package using the original paths and service names. The Windows native test fixture prints service identities and path owner/protection/SDDL on failure, without credential contents. Actual service-account and administrator access needs a Windows check; a Linux parse is not that check.

The Updates page shows the restart capability and repair instructions when the services don't match the installation record. See also [deployment and migration](https://github.com/CometWorks/quasar/blob/main/Docs/QuasarUpdates.md).

## Sources
- `README.md`
- `Docs/Installation.md`
- `Docs/QuickStart.md`
- `Docs/WindowsDeploymentAndUpdates.md`
- `Docs/QuasarUpdates.md`
- `Docs/ReleaseNotes1.5.md`
- `Docs/Configuration.md`
- `scripts/install.ps1`
- `scripts/uninstall.ps1`
- `scripts/install-gui.ps1`
- `scripts/Install.cmd`
- `scripts/readme-install-windows.md`
- `scripts/package-windows-release.ps1`
- `.github/workflows/release.yml`
- `Quasar/appsettings.json`
- `Quasar/Program.cs`
- `Quasar/Services/QuasarApplicationEntry.cs`
- `Quasar.Bootstrap/UpdateCli.cs`
- `Quasar/Services/WebServiceOptions.cs`
- `Quasar/Services/WebServiceManifestHostedService.cs`
- `Quasar/Services/BrowserLauncher.cs`
- `Quasar/Services/QuasarLoggingConfigurator.cs`
- `Quasar/Services/QuasarShutdownService.cs`
- `Quasar/Services/DedicatedServerRuntimePreparer.cs`
- `Quasar/Services/ServerManagementActions.cs`
- `Quasar/Services/ClusterLocalHost.cs`
- `Quasar/Components/Layout/MainLayout.razor`
- `Quasar/Components/Layout/QuasarControlDialog.razor`
- `Quasar/Components/Pages/NewServerDialog.razor`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
- `Quasar/Services/ClusterCommandService.cs`
- `Quasar/Services/ClusterReconciler.cs`
- `Quasar/Components/Shared/QuasarOperationLoader.razor`
- `Shared/Updates/QuasarServiceInstallation.cs`
- `Shared/Updates/NativeQuasarServices.cs`
- `Shared/Updates/QuasarUpgradePath.cs`
- `Shared/Updates/LauncherReplacement.cs`
- `Shared/ServiceHosting/NativeServiceFailures.cs`
- `Quasar.Updater/Program.cs`
- `Quasar.Updater/ConfigureInstallation.cs`
- `Quasar.Updater/InstallationProcesses.cs`
- `Quasar.Updater/TrustedUpdatePackage.cs`
- `Quasar.Tests/QuasarShutdownTests.cs`
- `Tests/Updates/NativeFixture.cs`
- `Tests/Updates/windows-services.ps1`
