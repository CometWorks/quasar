## Install and run on Windows (x64)

You downloaded **`quasar-installer-windows.zip`**. It contains one
`Quasar\` folder with the Quasar launcher (`Quasar.exe`), the
`install.ps1` / `uninstall.ps1` scripts, and a default `appsettings.json`. The
steps below assume you have extracted the zip, for example:

```powershell
Expand-Archive quasar-installer-windows.zip -DestinationPath C:\quasar
cd C:\quasar\Quasar
```

### Run in the foreground

```powershell
.\Quasar.exe serve
```

Quasar starts, opens <http://localhost:8080> in your browser, and prints log
output to the console. Press `Ctrl+C` to stop the launcher. The UI **Shutdown
Quasar** action drains the web worker and leaves the foreground launcher idle;
press `Ctrl+C`, then run `.\Quasar.exe serve` again when you want the UI back.
On first start the launcher downloads the Quasar web UI from GitHub and caches
it locally. The listening port is configurable — see [Configuration](Docs/Configuration.md).

### Install as a background service (Scheduled Task)

Install the **.NET 10 runtime** before running `install.ps1`.
Packaged Quasar can run with the runtime alone. When an administrator installs a
source-built QuasarHub UI plugin, Quasar uses a suitable .NET 10 SDK on `PATH`
or offers a confirmed download of its pinned private SDK. The administrator can
cancel and install the SDK with WinGet or another package manager instead.

Run from an **elevated PowerShell** (Administrator):

```powershell
.\install.ps1 -Start
```

This installs Quasar in the extracted folder, keeps Quasar state in the same
folder by default, and registers a **Scheduled Task** named `Quasar` that starts
the launcher at boot and restarts it on failure. The web UI is then served at
<http://localhost:8080>. Pass `-InstallDir <dir>` to copy it elsewhere, or
`-User <account>` to run as a specific service account instead of the current user.
The UI **Shutdown Quasar** action drains the web worker and leaves the task
running idle. Stop and start the task to bring the UI and supervisor back.

Manage the task:

```powershell
Get-ScheduledTask -TaskName Quasar
Start-ScheduledTask -TaskName Quasar
Stop-ScheduledTask  -TaskName Quasar
```

### Uninstall

```powershell
.\uninstall.ps1         # stop and remove the Scheduled Task
.\uninstall.ps1 -Purge  # also delete the install directory
```

For release assets, the auto-updater flow, and advanced configuration see the
[Windows Deployment & Updates](Docs/WindowsDeploymentAndUpdates.md) guide.
