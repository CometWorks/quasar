# Install Quasar

Download the complete package for your platform from
[Quasar releases](https://github.com/CometWorks/quasar/releases), verify its
`SHA256SUMS` entry, and extract the archive. The application, updater and Host are
in the same package. There is no separate updater download.

## Guided installation

Open the extracted `Quasar` folder:

- **Windows:** double-click `Install.cmd`, select **Install**, and approve Windows
  UAC. New installations default to `%ProgramFiles%\Quasar` for application files
  and `%ProgramData%\Quasar` for data. You can change both before installation.
  The main and updater services use passwordless virtual accounts. Existing
  default-service installation paths and names are read after elevation and kept.
- **Linux desktop:** open `Install.desktop`, select **Install**, and approve the
  polkit authentication prompt. Some file managers first require **Allow
  Launching**, **Trust**, or an executable-file confirmation. The graphical entry
  point needs `kdialog` or `zenity`, `pkexec`, and a desktop polkit authentication
  agent. New installs use system services running as your desktop user, with files
  and data under `~/.local/share/Quasar`. Required .NET, CA certificate, SteamCMD
  32-bit and polkit packages are installed through the detected package manager.
  Package availability depends on your distribution and enabled repositories.

Linux needs glibc 2.38 or newer for the game runtime, for example Debian 13 or
Ubuntu 24.04. An existing Linux installation retains its service name, execution
account, path and user/system scope. Reading existing metadata in the graphical
front end requires `python3`; the manual installer does not. A different execution
account or a custom Windows service installation uses manual installation.

The guided front ends call the same `install.sh` / `install.ps1` backends as manual
installation. Declining elevation leaves the installation untouched. Failures
report an error; Linux also keeps a private log under
`${XDG_STATE_HOME:-~/.local/state}/Quasar/install-*.log`. Windows shows the backend
console and an error dialog; rerun the manual command to retain terminal output.
No graphical installation is needed for a server without a desktop.

After a successful guided installation, the installer opens the configured Quasar
address in your desktop's default browser once Quasar is ready. Custom ports are
preserved. If the browser cannot open, the success dialog gives the address for
manual access. This runs from your desktop session; ordinary service starts and
restarts do not open browser windows. Automatic opening on Linux also needs
`python3` to read the installed address and check readiness; a new installation
still succeeds without it and shows manual access guidance.

## Manual installation

Choose **Manual install** in either guided installer, or use a terminal directly.

Linux user services, in the extracted package:

```sh
./install.sh --start
```

Linux system services with an explicit account and path:

```sh
sudo ./install.sh --system --user <account> --install-dir /path/to/Quasar --install-dependencies --yes --start
```

`--install-dependencies` opts into system package installation. Omit it to manage
CA certificates, SteamCMD's 32-bit libraries and polkit yourself. Without `--yes`,
the installer asks before installing packages. Use `./install.sh --help` for custom
service names, user/system scope and repair options.

Windows, from an elevated PowerShell in the extracted package:

```powershell
.\install.ps1 -InstallDir 'C:\Program Files\Quasar' -DataDir 'C:\ProgramData\Quasar' -Start
```

`-ServiceName`, `-UpdaterServiceName`, `-RepairSupervision` and
`-MigrateScheduledTask -TaskName <name>` cover custom and existing installations.
Without explicit paths the manual Windows installer retains its install-in-place
behavior. Existing named service accounts require an explicit account migration.

For a portable session without service registration, run `Quasar serve
--foreground` (`Quasar.exe` on Windows). Application updates then need a manual
stop and restart.

**Required upgrade order:** complete the 1.4.1 migration and start it successfully
before 1.5 or later. A staged legacy worker does not satisfy the requirement. See
[the migration prerequisite](QuasarUpdates.md#required-141-migration-before-15).

## Existing installations and updater upgrades

Keep application, settings, credentials and Data Protection keys backed up
together. Stop both native services before replacing their package, especially
when upgrading the updater itself. The updater is shipped in the same release;
the installer replaces it while stopped. Application updates never rewrite the
running updater's own files.

Legacy Bootstrap or Scheduled Task installations require the explicit
[migration procedure](https://github.com/CometWorks/quasar/blob/main/Docs/QuasarUpdates.md#migrating-existing-installations).
Save worlds and stop the old installation first. Preserve paths, account, service
names and Linux scope. The guided installer does not guess a legacy task's account
credentials or silently migrate another service account.
