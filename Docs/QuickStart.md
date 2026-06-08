# Quick Start

## Download

Grab the latest release from GitHub. Each release contains platform archives:

- **Linux** — `quasar-linux-x64.tar.gz`
- **Windows** — `quasar-win-x64.zip`

## Run from the terminal (foreground)

**Linux**

```bash
tar -xzf quasar-linux-x64.tar.gz -C ~/quasar
cd ~/quasar
./Quasar serve
```

**Windows** (PowerShell or cmd)

```cmd
Expand-Archive quasar-win-x64.zip -DestinationPath C:\quasar
cd C:\quasar
Quasar.exe serve
```

Quasar starts, opens `http://localhost:8080` in your browser, and prints log
output to the console. Press `Ctrl+C` to stop. The web UI port is configurable
— see [Configuration](Configuration.md).

## Install as a background service

If .NET 10 is missing, the Linux installer detects the available package manager
(`apt`, `dnf`, `yum`, `pacman`, or `zypper`), prints the exact commands it would
run to install the required .NET 10 SDK/runtime, includes the conditional
`/usr/local/bin/dotnet` PATH-link command, and asks before running anything.
Declining the prompt exits before files or services are changed.

**Linux — systemd**

```bash
tar -xzf quasar-linux-x64.tar.gz -C /tmp/quasar
sudo /tmp/quasar/install.sh --start   # installs to /opt/quasar and starts quasar.service
```

Manage the service with the usual systemd commands:

```bash
sudo systemctl status  quasar.service
sudo systemctl stop    quasar.service
sudo systemctl restart quasar.service
```

To remove:

```bash
sudo /opt/quasar/uninstall.sh          # stop and remove the service
sudo /opt/quasar/uninstall.sh --purge  # also delete /opt/quasar
```

The uninstall script stops `quasar.service` before removing it.

For release assets, auto-update behaviour, and advanced configuration see
[Linux Deployment & Updates](LinuxDeploymentAndUpdates.md).

**Windows — Task Scheduler**

Run from an **elevated PowerShell**:

```powershell
Expand-Archive quasar-win-x64.zip -DestinationPath "$env:ProgramFiles\QuasarSetup"
cd "$env:ProgramFiles\QuasarSetup"
.\install.ps1 -Start   # installs to %ProgramFiles%\Quasar and starts the task
```

The task starts at boot, restarts on failure, and runs as `SYSTEM` by default.
Pass `-User <account>` to run as a specific service account instead.

To remove:

```powershell
cd "$env:ProgramFiles\Quasar"
.\uninstall.ps1         # stop and remove the task
.\uninstall.ps1 -Purge  # also delete the install directory
```

For release assets, auto-update behaviour, and advanced configuration see
[Windows Deployment & Updates](WindowsDeploymentAndUpdates.md).
