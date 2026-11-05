## Install and run on Linux (x64)

Extract **`quasar-linux-x64.tar.gz`**. It contains the complete Quasar application,
UI assets, Host and a separate updater under `Updater/`.

Install the current complete package directly; no intermediate Quasar release is required. Preserve existing paths, accounts, settings, credentials and Data Protection keys when migrating to native services.

### Guided install

Open **Install.desktop**, choose **Install**, and approve polkit.
Choose **Manual install** for terminal commands and custom settings. The updater
is installed from this same package. See [installation](INSTALL.md) for defaults,
desktop requirements and existing-installation migration.

### Before you start

Use Linux x64 with glibc 2.38 or newer and the .NET 10 runtime. Game provisioning
also needs CA certificates and SteamCMD's 32-bit libraries; on Debian/Ubuntu:

```bash
sudo apt install ca-certificates lib32gcc-s1
```

A .NET 10 SDK is needed for source-built UI plugins; Quasar can offer its pinned
private SDK when no suitable SDK is installed.

### Run in the foreground

```bash
./Quasar serve --foreground
```

The UI is bundled and opens at <http://localhost:8080>. Application staging works
without services; activation then requires stopping Quasar, `update --retry` and
manual restart. **Shutdown Quasar** preserves running servers. Ctrl+C follows the
configured shutdown policy.

### Install as a background service (systemd)

```bash
./install.sh --start
systemctl --user status quasar.service quasar-updater.service
```

The default is a user service pair in the extracted directory. For system units
use `sudo ./install.sh --system --user <account> --start`. Preserve your existing
service name, account, path and scope during migration. System updater control uses
a scoped polkit rule; it does not need root at runtime. The clusters shutdown unit
continues to save/stop local clusters at logout or machine shutdown.

### Updates and uninstall

```bash
./Quasar update --version <release>
./Quasar update --status
./Quasar update --retry
./uninstall.sh
```

Read [updates and migration](Docs/QuasarUpdates.md) before replacing a legacy
Bootstrap installation. `--repair-supervision` repairs already copied files.
Uninstall retains data unless `--purge` is supplied.

## Post-install access setup

After starting Quasar, open `/setup` at its configured web address. Choose
**Self-deploy** to open Security, or **Use hosted panel** to enroll with Nebula.
Completed setup is saved; pending, cancelled or failed enrollment stays resumable.
Reopen it from **Security → Manage access setup**. See
[post-install instructions](INSTALL.md).
