# Install on Windows

How to download, run and install Quasar on Windows: release assets, the .NET prerequisite, foreground runs, the `install.ps1` Scheduled Task, where Quasar keeps its files, uninstalling and upgrading. For Linux see [Install on Linux](InstallLinux.md).

## Release assets

Every Quasar GitHub release (`https://github.com/CometWorks/quasar/releases`) carries the Windows and Linux archives together with one combined `SHA256SUMS` file.

| Asset | Contents | Who needs it |
| --- | --- | --- |
| `quasar-installer-windows.zip` | One top-level `Quasar\` folder: the `Quasar.exe` Bootstrap launcher, `install.ps1`, `uninstall.ps1`, the default `appsettings.json` and a `README.md` | Everyone. This is the file you download. |
| `quasar-web-win-x64.zip` | The web UI worker (`Quasar.exe`), static web assets, `Agent\Quasar.Agent.dll`, `Agent\Magnetar.Protocol.dll`, `Quasar.Plugin.Abstractions.dll` | Bootstrap downloads it on first start and on UI updates. |
| `quasar-host-win-x64.zip` | The standalone Quasar.Host for cluster machines | Only cluster machines. See [Cluster hosts](ClusterHosts.md). |
| `quasar-symbols-win-x64.zip` | Debug symbols matching the release | Developers analyzing crashes. |
| `SHA256SUMS` | SHA-256 of every archive | Quasar verifies every download against it. |

The installer zip does not contain the web UI. On first start Bootstrap downloads the latest `quasar-web-win-x64.zip` from GitHub, checks it against `SHA256SUMS`, extracts it to `<install-dir>\ManagedRuntime\WebService\<version>\` and writes `<install-dir>\Updates\active-release.json`. The first start therefore needs outbound HTTPS access to GitHub.

Both `Quasar.exe` files are self-contained single-file x64 executables (`win-x64`).

## Prerequisites

- Windows x64.
- The .NET 10 runtime. `install.ps1` runs `dotnet --list-runtimes` and requires a `Microsoft.NETCore.App 10.x` entry before it copies anything. It does not install .NET for you. Download it from `https://dotnet.microsoft.com/download/dotnet/10.0`.
- An elevated PowerShell (Run as Administrator) for `install.ps1` and `uninstall.ps1`.
- A .NET 10 SDK only if an administrator installs a source-built QuasarHub UI plugin. Quasar then uses a .NET 10 SDK on `PATH`, or asks whether to download a pinned private SDK into `<install-dir>\ManagedRuntime\Tools\DotNetSdk\`. You can also install the SDK with WinGet instead. See [UI plugins](UiPlugins.md).
- Git is not required.

## Run in the foreground

```powershell
Expand-Archive quasar-installer-windows.zip -DestinationPath C:\quasar
cd C:\quasar\Quasar
.\Quasar.exe serve
```

- Bootstrap starts the web worker and prints `Quasar worker launching on http://127.0.0.1:8080`. Open that URL in a browser. A `serve` run does not open a browser by itself.
- Logs go to the console and to `Logs\quasar.log` in the extracted folder.
- `Ctrl+C` stops the launcher.
- State lives in the extracted folder.
- If another Quasar is already healthy on the same folder, `Quasar.exe serve` exits immediately with code 0 and no output.

`.\Quasar.exe serve --headless` runs the API-only mode (no Blazor UI, UI plugins, branding or static files). The other launcher commands and flags are the same as on Linux; see [Install on Linux](InstallLinux.md#launcher-commands-and-flags).

## Install as a Scheduled Task

Quasar on Windows runs as a Scheduled Task, not as a Windows Service. From an elevated PowerShell:

```powershell
Expand-Archive quasar-installer-windows.zip -DestinationPath C:\quasar
cd C:\quasar\Quasar
.\install.ps1 -Start
```

With an extracted release zip, `install.ps1` installs in place: the folder that holds the script becomes the install directory, and Quasar keeps all its state there. At the end it prints the task name, install directory, run-as account and the web UI URL (`http://localhost:<Quasar:Port>`, default `8080`).

### install.ps1 parameters

| Parameter | Default | Effect |
| --- | --- | --- |
| `-Start` | off | Starts the task right after registering it. Without it, run `Start-ScheduledTask -TaskName Quasar` or reboot. |
| `-InstallDir <dir>` | the script's folder for release zips; `%ProgramFiles%\Quasar` for source checkouts | Copies Quasar to `<dir>` and runs it from there. |
| `-User <account>` | the current user | Runs the task as this account instead. |
| `-TaskName <name>` | `Quasar` | Scheduled Task name. |
| `-NoEnable` | off | Registers the task disabled. |
| `-NoBuild` | automatic for release zips | Source checkouts: install an existing publish output instead of running `dotnet publish`. |
| `-Configuration <name>`, `-Runtime <rid>` | `Release`, `win-x64` | Source checkouts only: publish settings. |

`install.ps1` replaces an existing task with the same name. It does not create firewall rules.

### The Scheduled Task

| Setting | Value |
| --- | --- |
| Name | `Quasar` (or `-TaskName`) |
| Action | `<install-dir>\Quasar.exe serve --quiet --service`, working directory `<install-dir>`. Bootstrap is the task's process directly, without `cmd.exe`. |
| Trigger | At system startup |
| Account | Default: the installing user (`DOMAIN\user`), logon type S4U, run level Highest. With `-User`: that account, logon type ServiceAccount, run level Highest. |
| Restart on failure | Up to 10 times, every 5 minutes |
| Time limit | None |
| Other | Start when available, run on batteries, ignore a new instance while one runs. The task is saved with Windows 10 compatibility. |

The task sets no environment variables. Bootstrap uses its own folder as the install root (`QUASAR_INSTALL_DIR`) and `--service` puts it in service mode, which also disables the browser auto-open.

Manage the task:

```powershell
Get-ScheduledTask   -TaskName Quasar
Start-ScheduledTask -TaskName Quasar
Stop-ScheduledTask  -TaskName Quasar
```

Stopping the task terminates the Bootstrap process. To restart Quasar after editing `appsettings.json`, run `Stop-ScheduledTask -TaskName Quasar; Start-ScheduledTask -TaskName Quasar`.

### Shutdown Quasar and other power actions

The power button in the top bar opens **Quasar power** (admin role):

- **Shutdown Quasar** saves the managed server state, then stops the web worker and Bootstrap with exit code 0. The task counts this as success and does not restart it. Quasar stops within about 10 seconds, also while a browser tab still shows the UI. Servers keep running without supervision. Run `Start-ScheduledTask -TaskName Quasar` to bring the UI back. In a foreground run the launcher exits to the terminal.
- **Restart Quasar** restarts only the web worker under Bootstrap. Servers keep running.
- **Save and stop all servers** stops every server and leaves Quasar running.

If the web worker crashes, Bootstrap restarts it. If Bootstrap itself fails, the task restarts it (up to 10 times, 5 minutes apart).

## Where Quasar keeps its files

Everything lives under the install root: the extracted `Quasar\` folder for an in-place install, or `-InstallDir`. The layout is the same as on Linux; the full table is in [Install on Linux](InstallLinux.md#where-quasar-keeps-its-files). The most asked-for paths:

| Path under the install root | Contents |
| --- | --- |
| `appsettings.json` | Settings. Pages such as Security and Updates save their changes here. |
| `rbac.json` | Role mappings. See [Access control](AccessControl.md). |
| `Logs\quasar.log`, `Logs\discord.log` | Application logs. Under the Scheduled Task there is no visible console, so read these files. |
| `Magnetars\<server>\` | Per-server definition, DS app data and saves, Magnetar config, history and analytics |
| `ManagedRuntime\Tools\` | Downloaded Magnetar, SteamCMD, Space Engineers Dedicated Server and private .NET SDK |
| `ManagedRuntime\WebService\<version>\` | Installed web UI worker releases |
| `Updates\` | `active-release.json`, `appsettings.base.json`, staged updates |
| `Backups\` | Configuration backup ZIPs |
| `Diagnostics\` | Diagnostic capture and queues. See [Diagnostics](Diagnostics.md). |

Back up the whole install root as one unit.

## Network ports

The web UI listens on TCP `8080` on all interfaces by default (`Quasar:Host` `0.0.0.0`, `Quasar:Port` `8080` in `appsettings.json`). Each game server gets its own UDP game port, starting at `27016`, plus a Steam port (game port + 1000) and a Remote API port (game port + 2000). Neither the scripts nor Quasar add Windows Firewall rules. See [Install on Linux](InstallLinux.md#network-ports) and [Servers](Servers.md).

## Uninstall

From an elevated PowerShell in the install directory:

```powershell
.\uninstall.ps1          # stop and remove the Scheduled Task, keep all files
.\uninstall.ps1 -Purge   # also delete the install directory
```

| Parameter | Default | Effect |
| --- | --- | --- |
| `-TaskName <name>` | `Quasar` | Task to remove |
| `-InstallDir <dir>` | the script's folder | Folder `-Purge` deletes |
| `-Purge` | off | Delete the install directory, including all servers, saves, backups and logs inside it |

The script stops the task if it is running, then unregisters it. If the task does not exist it prints `Scheduled task 'Quasar' not found; nothing to remove.` and continues. `-Purge` refuses the system drive root, `%SystemRoot%`, `%ProgramFiles%`, `%ProgramFiles(x86)%` and `%ProgramData%`. Managed game servers are detached processes, so use **Save and stop all servers** first if you want them stopped.

## Upgrading

- Quasar updates itself from the Updates page. See [Updates](Updates.md). Your `appsettings.json` is kept.
- When Bootstrap replaces itself on Windows, it starts the new launcher as a detached `Quasar.exe serve --quiet` process (with `--service` and `--headless` if the old one had them) and exits with code 0.
- Running `install.ps1 -InstallDir <existing-root>` from a newly extracted zip copies every file of that zip into the install root, including the default `appsettings.json`. Save your `appsettings.json` first and put it back afterwards.
- Rerun `install.ps1` to change task options; it replaces the task.

### Move the install root

Stop the Scheduled Task and the managed servers, copy the whole install root, then run `install.ps1 -InstallDir <new-root>` so the task starts Quasar from the new place. Blank or relative server paths follow the new root. Absolute overrides stay where they point. If an old `server.json` still contains absolute default paths, open the server and use **Edit Server -> Paths -> Use Managed Defaults**.

## Cluster machines

Cluster nodes run Quasar.Host from `quasar-host-win-x64.zip` in its own folder with its own configuration. See [Cluster hosts](ClusterHosts.md).

## Common problems

- `.NET 10 runtime is required before installing Quasar.`: install the .NET 10 runtime, open a new PowerShell and rerun. The message lists the runtimes it found.
- `Run from an elevated PowerShell (Administrator) to register the scheduled task.`: start PowerShell with Run as Administrator. `uninstall.ps1` has the same check.
- `install.ps1 supports Windows only.`: use `install.sh` on Linux.
- `Existing publish output not found: <dir>`: `-NoBuild` was used in a source checkout without a publish output.
- Rerunning `install.ps1` in place inside a populated install root copies the whole folder, including downloaded runtimes and saves, through a temporary folder under `%TEMP%` and back. Make sure the temp drive has enough free space, or stop the task and run the script from a freshly extracted zip with `-InstallDir`.
- `Error: port 8080 is already bound by another process and is not a healthy Quasar server.`: another program uses the port. Stop it or change `Quasar:Port` in `appsettings.json`.
- The UI never comes up after a fresh install: Bootstrap could not download the web worker from GitHub, or `QUASAR_UPDATES_ENABLED=false` blocks the first download. The Scheduled Task logs nothing about it, because Bootstrap runs with `--quiet`. Stop the task and run `Quasar.exe serve` in a terminal to see `Failed downloading initial Quasar web release.`.
- `Could not request Bootstrap shutdown. Check write access to the Quasar install directory.`: the task account cannot write to the install root. Give it write access.

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
