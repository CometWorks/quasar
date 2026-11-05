## Install and run on Windows (x64)

Extract **`quasar-win-x64.zip`**. It contains the complete Quasar application,
UI assets, Host, installer and a small updater under `Updater\`.

Install the current complete package directly; no intermediate Quasar release is required. Preserve existing paths, accounts, settings, credentials and Data Protection keys when migrating to native services.

### Guided install

Open **Install.cmd**, choose **Install**, and approve Windows UAC.
Choose **Manual install** for terminal commands and custom settings. The updater
is installed from this same package. See [installation](INSTALL.md) for defaults,
desktop requirements and existing-installation migration.

### Run in the foreground

```powershell
.\Quasar.exe serve --foreground
```

The UI is bundled and available at <http://localhost:8080>. A separate UI download
is unnecessary. Foreground updates stage safely and need explicit manual restart.
Use **Shutdown Quasar** to preserve running game servers before manual activation.

### Install native services

From an elevated PowerShell:

```powershell
.\install.ps1 -Start
Get-Service Quasar, QuasarUpdater
```

The installer uses dedicated `NT SERVICE\Quasar` and `NT SERVICE\QuasarUpdater`
virtual accounts, with no password. It grants data permissions and narrowly scoped
main-service control to the updater. `-InstallDir`, `-DataDir`, `-ServiceName` and
`-UpdaterServiceName` support custom installations.

For an old Scheduled Task installation, save worlds and stop the old task and
servers first. Then run the new package's installer with `-MigrateScheduledTask
-TaskName <old name> -Start`. A complete application backup is retained; migration
failure restores it and the old task's enabled state. Do not run old/new supervision
together. See [migration](Docs/QuasarUpdates.md#migrating-existing-installations).

### Updates and uninstall

Use an elevated shell or an account with installation/data access:

```powershell
.\Quasar.exe update --version <release>
.\Quasar.exe update --status
.\Quasar.exe update --retry
.\uninstall.ps1
```

`-RepairSupervision` repairs services after files have already been replaced while
stopped. The updater does not replace its own files during an application update.
Uninstall preserves data unless `-Purge` is supplied. See
[Windows deployment](Docs/WindowsDeploymentAndUpdates.md).

## Post-install access setup

After starting Quasar, open `/setup` at its configured web address. Choose
**Self-deploy** to open Security, or **Use hosted panel** to enroll with Nebula.
Completed setup is saved; pending, cancelled or failed enrollment stays resumable.
Reopen it from **Security → Manage access setup**. See
[post-install instructions](INSTALL.md).
