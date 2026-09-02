## Install and run on Linux (x64)

You downloaded **`quasar-installer-linux.tar.gz`**. It contains one
`Quasar/` folder with the Quasar launcher (`Quasar`), the
`install.sh` / `uninstall.sh` scripts, and a default `appsettings.json`.

### Run in the foreground

```bash
tar -xzf quasar-installer-linux.tar.gz
cd Quasar
./Quasar serve
```

Quasar starts, opens `http://localhost:8080` in your browser, and prints log
output to the console. Press `Ctrl+C` to stop the launcher. The UI **Shutdown
Quasar** action drains the web worker and leaves the foreground launcher idle;
press `Ctrl+C`, then run `./Quasar serve` again when you want the UI back. On
first start the launcher downloads the Quasar web UI from GitHub and caches it
locally. The listening port is configurable — see [Configuration](Docs/Configuration.md).

### Install as a background service (systemd)

Install the **.NET 10 runtime** before running `install.sh`. Packaged Quasar can
run with the runtime alone, but QuasarHub UI plugin install/update compiles
source with `dotnet build` and needs the **.NET 10 SDK**. When an administrator
installs one of those plugins, Quasar first uses a suitable SDK on `PATH`. If no
system SDK is available, the UI can download a pinned private SDK into Quasar's
managed data directory after confirmation, or the administrator can cancel and
install the SDK through the system package manager.

```bash
mkdir -p ~/.local/share/Quasar
tar -xzf quasar-installer-linux.tar.gz -C ~/.local/share/Quasar --strip-components=1
~/.local/share/Quasar/install.sh --start
```

This installs Quasar in the extracted folder and starts the user
`quasar.service`. Pass `--system` with `sudo` for a machine-wide service or
`--install-dir <dir>` to install Quasar elsewhere. The web UI is then served at
`http://localhost:8080`. In the installed user service, the UI **Shutdown
Quasar** action drains the web worker and leaves `quasar.service` running without
respawning it. Restart the service to bring the UI and supervisor back:

```bash
systemctl --user status  quasar.service
systemctl --user stop    quasar.service
systemctl --user restart quasar.service
```

### Uninstall

```bash
~/.local/share/Quasar/uninstall.sh          # stop and remove the user service
~/.local/share/Quasar/uninstall.sh --purge  # also delete the install folder
```

The uninstall script stops `quasar.service` before removing it.

For release assets, the auto-updater flow, and advanced configuration see the
[Linux Deployment & Updates](Docs/LinuxDeploymentAndUpdates.md) guide.
