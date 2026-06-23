# Quick Start

## Download

Grab the latest release from GitHub. Each release contains platform archives:

- **Linux** — `quasar-installer-linux.tar.gz`
- **Windows** — `quasar-installer-windows.zip`

## Run from the terminal (foreground)

**Linux**

```bash
tar -xzf quasar-installer-linux.tar.gz
cd Quasar
./Quasar serve
```

**Windows** (PowerShell or cmd)

```cmd
Expand-Archive quasar-installer-windows.zip -DestinationPath C:\quasar
cd C:\quasar\Quasar
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
On Debian 13, the prompt also includes the Microsoft package feed bootstrap
commands needed before installing the .NET packages.

**Linux — systemd**

```bash
mkdir -p ~/.local/share/Quasar
tar -xzf quasar-installer-linux.tar.gz -C ~/.local/share/Quasar --strip-components=1
~/.local/share/Quasar/install.sh --start        # installs in place and starts quasar.service
```

The Linux installer defaults to a user systemd service, uses the extracted
folder as the install and data root, and writes that path to the unit as
`QUASAR_DATA_DIR`. Pass `--system` with `sudo` for a machine-wide service,
`--install-dir <dir>` to copy Quasar elsewhere, or `--data-dir <dir>` to store
Quasar state elsewhere.
When Quasar is running from the installed user service, the UI **Shutdown
Quasar** action requests `systemctl --user stop quasar.service` and leaves
managed servers detached by default.

Manage the service with the usual systemd commands:

```bash
systemctl --user status  quasar.service
systemctl --user stop    quasar.service
systemctl --user restart quasar.service
```

To remove:

```bash
~/.local/share/Quasar/uninstall.sh          # stop and remove the user service
~/.local/share/Quasar/uninstall.sh --purge  # also delete the install folder
```

The uninstall script stops `quasar.service` before removing it.

For release assets, auto-update behaviour, and advanced configuration see
[Linux Deployment & Updates](LinuxDeploymentAndUpdates.md).

**Windows — Task Scheduler**

Run from an **elevated PowerShell**:

```powershell
Expand-Archive quasar-installer-windows.zip -DestinationPath C:\quasar
cd C:\quasar\Quasar
.\install.ps1 -Start   # installs in place and starts the task
```

The task starts at boot, restarts on failure, and runs as the installing user by
default. Quasar state is stored in the same folder by default. Pass
`-InstallDir <dir>` to copy Quasar elsewhere, or `-User <account>` to run as a
specific service account instead.

To remove:

```powershell
.\uninstall.ps1         # stop and remove the task
.\uninstall.ps1 -Purge  # also delete the install directory
```

For release assets, auto-update behaviour, and advanced configuration see
[Windows Deployment & Updates](WindowsDeploymentAndUpdates.md).
