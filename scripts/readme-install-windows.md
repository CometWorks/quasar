## Install and run on Windows (x64)

You downloaded **`quasar-win-x64.zip`**. It contains the Quasar launcher
(`Quasar.exe`), the `install.ps1` / `uninstall.ps1` scripts, and a default
`appsettings.json`. The steps below assume you have extracted the zip, for
example:

```powershell
Expand-Archive quasar-win-x64.zip -DestinationPath C:\quasar
cd C:\quasar
```

### Run in the foreground

```powershell
.\Quasar.exe serve
```

Quasar starts, opens <http://localhost:8080> in your browser, and prints log
output to the console. Press `Ctrl+C` to stop. On first start the launcher
downloads the Quasar web UI from GitHub and caches it locally. The listening
port is configurable — see [Configuration](Docs/Configuration.md).

### Install as a background service (Scheduled Task)

Run from an **elevated PowerShell** (Administrator):

```powershell
.\install.ps1 -Start
```

This installs Quasar to `%ProgramFiles%\Quasar` and registers a **Scheduled
Task** named `Quasar` that starts the launcher at boot and restarts it on
failure. The web UI is then served at <http://localhost:8080>. Pass
`-User <account>` to run as a specific service account instead of the current
user.

Manage the task:

```powershell
Get-ScheduledTask -TaskName Quasar
Start-ScheduledTask -TaskName Quasar
Stop-ScheduledTask  -TaskName Quasar
```

### Uninstall

```powershell
cd "$env:ProgramFiles\Quasar"
.\uninstall.ps1         # stop and remove the Scheduled Task
.\uninstall.ps1 -Purge  # also delete the install directory
```

For release assets, the auto-updater flow, and advanced configuration see the
[Windows Deployment & Updates](Docs/WindowsDeploymentAndUpdates.md) guide.
