# Install on Windows

Quasar runs its UI and supervisor in one application. Native Windows Services
run that application and a small external updater under dedicated virtual accounts.
For Linux see [Install on Linux](InstallLinux.md).

**Required upgrade order:** install and successfully start the complete 1.4.1
migration release before 1.5 or later. Native service health (or successful startup
of the full manual package) records `Updates/migration-1.4.1.json`. A staged legacy
web worker is insufficient. Keep the receipt with the data directory. Missing or
invalid completion evidence blocks later staging, installation and startup.

## Release assets

Download `quasar-win-x64.zip`. Its `Quasar/` folder contains the application,
UI assets, `Updater/Quasar.Updater.exe`, Host, installers and defaults. Check it
against `SHA256SUMS`. Legacy `quasar-web-*` assets remain only on the retained 1.4.1 migration release;
old launcher asset names are deliberately absent to prevent unsafe Bootstrap
self-activation. Symbols remain CI artifacts.

## Guided installation

Open **Install.cmd** from the extracted package and choose **Install**.
Windows requests UAC approval. New installs use Program Files for the application and ProgramData for data, with editable paths and passwordless virtual accounts. Existing default-service paths and names are read after elevation and preserved.

Choose **Manual install** for terminal commands and custom settings. The same
platform package contains the updater; no separate download is needed. See
[installation details](https://github.com/CometWorks/quasar/blob/main/Docs/Installation.md).

## Prerequisites

Windows x64 and an elevated PowerShell for installation. Packaged binaries are
self-contained. Source builds and source-built UI plugins need a .NET 10 SDK;
Quasar can offer its pinned private SDK for plugin builds. Git is unnecessary for
packaged installs. Virtual service accounts need no password.

## Run in the foreground

```powershell
Expand-Archive quasar-win-x64.zip -DestinationPath C:\quasar
cd C:\quasar\Quasar
.\Quasar.exe serve --foreground
```

The bundled UI is available at `http://localhost:8080`. Foreground updates stage
safely but need manual stop, `update --retry` and restart. **Shutdown Quasar**
preserves running servers; Ctrl+C follows the configured shutdown policy.

## Install native Windows services

```powershell
.\install.ps1 -Start
Get-Service Quasar, QuasarUpdater
```

The installer grants data access to `NT SERVICE\Quasar` and application replacement
and main-service control to `NT SERVICE\QuasarUpdater`. It protects updater files,
service metadata and shared Data Protection keys. Use an elevated shell for local
CLI maintenance. Application updates stop only Quasar; the updater remains outside
its file locks and restores the complete previous application on failure.

## Install as a Scheduled Task

This heading is retained for old links. Scheduled Tasks are now a migration source,
not the recommended installation. Save worlds, stop the old task and game servers,
then use `-MigrateScheduledTask -TaskName <name> -Start` from the new package. A
one-time server restart may be needed to leave the old task's job object. The old
task and complete application backup remain available; migration failure restores
them. Do not run old and new supervision together.

### install.ps1 parameters

| Parameter | Purpose |
| --- | --- |
| `-InstallDir`, `-DataDir` | Existing/custom application and data locations |
| `-ServiceName`, `-UpdaterServiceName` | Native service names; preserve them on repair |
| `-Start` | Start and health-check the native installation |
| `-RepairSupervision` | Repair already replaced files without copying binaries |
| `-MigrateScheduledTask`, `-TaskName` | Explicit transition from a stopped old task |
| `-NoEnable` | Manual rather than automatic native service startup |
| `-NoBuild`, `-Configuration`, `-Runtime` | Source/publish installation options |

The installer does not silently convert an existing named service account into a
virtual account. Complete that account migration explicitly first.

### The Scheduled Task

A migration task is disabled before native activation and retained. After successful
migration it stays disabled; remove it only once native operation and recovery have
been checked. Failure restores the prior application and enabled state.

### Shutdown Quasar and other power actions

**Shutdown Quasar** preserves servers and exits successfully; native failure
recovery leaves the main service stopped. **Restart Quasar** uses the updater.
Explicit server stop actions still stop their selected servers.

The installer grants access to the configured application and data directories.
Grant the main virtual account access to any external worlds, runtime folders,
backup paths or network resources before starting migrated servers. Existing task
credentials cannot be transferred to a service without supplying credentials;
this migration instead uses the selected passwordless virtual accounts.

## Where Quasar keeps its files

The installer records application and data paths in protected metadata. The small
`service-installation.json` in the application folder locates that record. Existing
configuration, worlds, credentials and Data Protection keys are retained together.
`Updates/operation.json` is the durable update status. The complete previous
application is under `Updates/LauncherTransaction` in the application directory.

## Network ports

The default UI/API port is 8080. Existing game and cluster ports are unchanged.
Internal update health requires the installation token and a local connection.
The installer does not add public firewall rules.

## Uninstall

```powershell
.\uninstall.ps1
```

This stops/removes the native pair and any retained migration task. Data remains
unless `-Purge` is supplied. Stop game servers explicitly before destructive purge.

## Upgrading

Use **Update Quasar** or `Quasar.exe update --version <release>`. Inspect
`update --status` and use `update --retry` after correcting a failure. The updater
independently checks trusted release provenance and confirms the exact service PID
and version before recording success. See [Updates](Updates.md).

### Move the install root

Stop both services, preserve the complete application/data and keyring together,
copy to the new locations and rerun installer repair with those paths and names.
Verify native actions and health before deleting the old copy.

## Cluster machines

Host is bundled under `Host/` in the complete platform package. Cluster deployment
and Host update ownership remain separate; application updates do not deploy remote
changes. See [Cluster hosts](ClusterHosts.md).

## Common problems

Read the persistent capability and operation messages on Updates. Missing service
permissions, wrong accounts/actions, a stopped updater, checksum failures and merge
conflicts prevent activation. The old application remains available. Linux fixtures
do not validate Windows file locking; native Windows CI provides that coverage.
See [deployment and migration](https://github.com/CometWorks/quasar/blob/main/Docs/QuasarUpdates.md).

## Sources
- `README.md`
- `Docs/QuickStart.md`
- `Docs/WindowsDeploymentAndUpdates.md`
- `Docs/Configuration.md`
- `scripts/install.ps1`
- `scripts/uninstall.ps1`
- `scripts/readme-install-windows.md`
- `scripts/package-windows-release.ps1`
- `.github/workflows/release.yml`
- `Quasar/appsettings.json`
- `Quasar.Bootstrap/Program.cs`
- `Quasar.Bootstrap/Quasar.Bootstrap.csproj`
- `Quasar/Program.cs`
- `Quasar/Services/WebServiceOptions.cs`
- `Quasar/Services/BrowserLauncher.cs`
- `Quasar/Services/QuasarLoggingConfigurator.cs`
- `Quasar/Services/QuasarShutdownService.cs`
- `Quasar/Services/DedicatedServerRuntimePreparer.cs`
- `Quasar/Services/ServerManagementActions.cs`
- `Quasar/Components/Layout/MainLayout.razor`
- `Quasar/Components/Layout/QuasarControlDialog.razor`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
- `Quasar/Services/ClusterCommandService.cs`
- `Quasar/Services/ClusterReconciler.cs`
- `Quasar/Components/Shared/QuasarOperationLoader.razor`

- `Docs/QuasarUpdates.md`

- `Shared/Updates/QuasarServiceInstallation.cs`

- `Quasar.Updater/ConfigureInstallation.cs`

- `Shared/Updates/GitHubUpdateTokenReader.cs`

- `Shared/ServiceHosting/NativeServiceFailures.cs`

- `Shared/Updates/NativeQuasarServices.cs`

- `Docs/Installation.md`

- `scripts/install-gui.ps1`

- `scripts/Install.cmd`

- `Shared/Updates/QuasarUpgradePath.cs`

- `Docs/ReleaseNotes1.5.md`
