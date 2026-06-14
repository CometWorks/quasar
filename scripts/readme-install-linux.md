## Install and run on Linux (x64)

You downloaded **`quasar-linux-x64.tar.gz`**. It contains the Quasar launcher
(`Quasar`), the `install.sh` / `uninstall.sh` scripts, and a default
`appsettings.json`.

### Run in the foreground

```bash
mkdir -p ~/quasar
tar -xzf quasar-linux-x64.tar.gz -C ~/quasar
cd ~/quasar
./Quasar serve
```

Quasar starts, opens `http://localhost:8080` in your browser, and prints log
output to the console. Press `Ctrl+C` to stop. On first start the launcher
downloads the Quasar web UI from GitHub and caches it locally. The listening
port is configurable — see [Configuration](Docs/Configuration.md).

### Install as a background service (systemd)

Install the **.NET 10 runtime** before running `install.sh`.

```bash
mkdir -p /tmp/quasar
tar -xzf quasar-linux-x64.tar.gz -C /tmp/quasar
/tmp/quasar/install.sh --start
```

This installs Quasar to `~/.local/share/Quasar`, creates the user's
`~/.config/Quasar` data directory, and starts the user `quasar.service`. Pass
`--system` with `sudo` for a machine-wide service, or `--data-dir <dir>` to
store Quasar state elsewhere. The web UI is then served at
`http://localhost:8080`. Manage the service with:

```bash
systemctl --user status  quasar.service
systemctl --user stop    quasar.service
systemctl --user restart quasar.service
```

### Uninstall

```bash
~/.local/share/Quasar/uninstall.sh          # stop and remove the user service
~/.local/share/Quasar/uninstall.sh --purge  # also delete ~/.local/share/Quasar
```

The uninstall script stops `quasar.service` before removing it.

For release assets, the auto-updater flow, and advanced configuration see the
[Linux Deployment & Updates](Docs/LinuxDeploymentAndUpdates.md) guide.
