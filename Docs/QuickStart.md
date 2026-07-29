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
output to the console. Press `Ctrl+C` to stop the launcher. The UI **Shutdown
Quasar** action drains the web worker and leaves the foreground launcher idle;
press `Ctrl+C`, then run `./Quasar serve` or `Quasar.exe serve` again when you
want the UI back. The web UI port is configurable — see
[Configuration](Configuration.md).

## First server setup

The dashboard setup wizard starts from a world shipped with Space Engineers
Dedicated Server:

1. Choose a predefined Dedicated Server world. Quasar copies it into managed
   world-template storage. The chooser stays disabled until the managed
   Dedicated Server download is installed and validated.
2. Create a matching config profile from that template's
   `Sandbox_config.sbc`. This carries the world's session settings and mods
   forward instead of applying an unrelated profile on first start.
3. Create the server. The wizard preselects the matching world template and
   config profile; create the server's save from that template in the editor.
4. Start the server and wait for Quasar.Agent to connect.

Both template and matching profile are required. If a profile was already
created from the selected template, the wizard recognizes that relationship
and skips the profile-creation step.

## Install as a background service

If .NET 10 is missing, the Linux installer detects the available package manager
(`apt`, `dnf`, `yum`, `pacman`, or `zypper`), prints the exact commands it would
run to install the required .NET 10 SDK/runtime, includes the conditional
`/usr/local/bin/dotnet` PATH-link command, and asks before running anything.
Declining the prompt exits before files or services are changed.
On Debian 13, the prompt also includes the Microsoft package feed bootstrap
commands needed before installing the .NET packages.

Packaged installs need only the runtime to run Quasar. QuasarHub UI plugin
install/update compiles source with `dotnet build`, so install the .NET 10 SDK
too by accepting the optional prompt or passing `--install-ui-plugin-sdk` when
you want source-built UI plugins from QuasarHub. On Linux, the UI Plugins page
can also run the install script's SDK-only path when the SDK is missing.

**Linux — systemd**

```bash
mkdir -p ~/.local/share/Quasar
tar -xzf quasar-installer-linux.tar.gz -C ~/.local/share/Quasar --strip-components=1
~/.local/share/Quasar/install.sh --start        # installs in place and starts quasar.service
# Optional SDK for QuasarHub source-built UI plugin installs:
# ~/.local/share/Quasar/install.sh --start --install-ui-plugin-sdk
```

The Linux installer defaults to a user systemd service, uses the extracted
folder as the install root, and writes that path to the unit as
`QUASAR_INSTALL_DIR`. Pass `--system` with `sudo` for a machine-wide service or
`--install-dir <dir>` to install Quasar elsewhere.
When Quasar is running from the installed user service, the UI **Shutdown
Quasar** action drains the web worker and leaves `quasar.service` running idle
without respawning it. Managed servers stay detached by default. Restart the
service to bring the UI and supervisor back.

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
The UI **Shutdown Quasar** action drains the web worker and leaves the Scheduled
Task running idle. Stop and start the task to bring the UI and supervisor back.

To remove:

```powershell
.\uninstall.ps1         # stop and remove the task
.\uninstall.ps1 -Purge  # also delete the install directory
```

For release assets, auto-update behaviour, and advanced configuration see
[Windows Deployment & Updates](WindowsDeploymentAndUpdates.md).
