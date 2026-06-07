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

```bash
mkdir -p /tmp/quasar
tar -xzf quasar-linux-x64.tar.gz -C /tmp/quasar
sudo /tmp/quasar/install.sh --start
```

This installs Quasar to `/opt/quasar` and starts `quasar.service`. The web UI is
then served at `http://localhost:8080`. Manage the service with the usual
systemd commands:

```bash
sudo systemctl status  quasar.service
sudo systemctl stop    quasar.service
sudo systemctl restart quasar.service
```

### Uninstall

```bash
sudo /opt/quasar/uninstall.sh          # stop and remove the service
sudo /opt/quasar/uninstall.sh --purge  # also delete /opt/quasar
```

For release assets, the auto-updater flow, and advanced configuration see the
[Linux Deployment & Updates](Docs/LinuxDeploymentAndUpdates.md) guide.
