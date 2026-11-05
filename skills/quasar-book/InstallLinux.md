# Install on Linux

How to download, run and install Quasar on Linux: the release package, the guided installer, prerequisites, foreground runs, the `install.sh` systemd services, where Quasar keeps its files, uninstalling and upgrading. For the container image see [Docker](Docker.md); for Windows see [Install on Windows](InstallWindows.md).

Quasar is one application: the web UI and the server supervisor run in the same `Quasar` process. A small separate updater, `Updater/Quasar.Updater`, replaces the application during updates. The installer registers both as systemd services. Older versions had a Bootstrap launcher that downloaded a separate web worker; current packages don't use it.

**Required upgrade order.** Quasar 1.5 and later start only after a completed 1.4.1 migration. Install the complete 1.4.1 package with your existing paths, account, service name and user or system scope, and start it successfully. That writes `Updates/migration-1.4.1.json` in the data directory. A 1.4.1 web worker staged by an old Bootstrap install does not count. Without a valid receipt, the 1.5 installer, the updater and Quasar itself refuse with `Quasar 1.5 and later require a completed Quasar 1.4.1 migration first. ...` (exit code 3). This applies to new installations too: a new installation that will run 1.5 starts with 1.4.1. Keep the receipt with the data directory. Don't write one by hand.

## Release assets

Every GitHub release (`https://github.com/CometWorks/quasar/releases`) carries:

| Asset | Contents |
| --- | --- |
| `quasar-linux-x64.tar.gz` | One top-level `Quasar/` folder: the `Quasar` application with its UI assets (`wwwroot/`), `Agent/`, `Host/Quasar.Host`, `Updater/Quasar.Updater`, `install.sh`, `uninstall.sh`, `install-gui.sh`, `Install.desktop`, `quasar-renice.c`, the default `appsettings.json`, `release.json`, `launcher-files.txt`, `README.md` and `INSTALL.md` |
| `quasar-win-x64.zip` | The Windows package, see [Install on Windows](InstallWindows.md) |
| `SHA256SUMS` | SHA-256 of the archives |

Verify the archive against `SHA256SUMS` before you extract it. The package needs no download on first start: the UI is bundled. SteamCMD, the Dedicated Server, Magnetar and plugins still download from the network. The legacy `quasar-web-*` worker archives exist only on the 1.4.1 migration release, so old Bootstrap installs pick that release. Debug symbols are CI artifacts, not release assets.

`Quasar`, `Quasar.Updater` and `Quasar.Host` are self-contained single-file x64 programs.

## Guided installation

Open **Install.desktop** in the extracted `Quasar` folder and choose **Install**. Some file managers ask you to **Allow Launching** or **Trust** the file first. The front end needs `kdialog` or `zenity`, `pkexec` and a desktop polkit authentication agent. It refuses to run as root; start it as your desktop user.

For a new install it shows the plan and asks for polkit approval:

- Application and data: `~/.local/share/Quasar` (the package is copied there).
- Execution account: your desktop user.
- Scope: system services (`quasar.service` and `quasar-updater.service` in `/etc/systemd/system`).
- It runs `install.sh --system --user <you> --install-dependencies --yes --start`, so CA certificates, SteamCMD's 32-bit libraries, polkit and the .NET 10 runtime are installed through the package manager when missing.

If `~/.local/share/Quasar` (or the package folder) already holds an installation, the guided installer reads its `service-installation.json` and keeps its path, account, service name and user or system scope. Reading it needs `python3`. Automatic browser opening also needs `python3`; a new installation still succeeds without it and shows manual access guidance. An installation that runs as another account needs the manual install.

On success it waits up to 90 seconds for Quasar to answer its health check, then opens the configured address, including a custom port, in your desktop browser through `xdg-open` or `gio`. The success dialog shows the address and log path. If Quasar is not ready or the browser cannot open, it gives manual access guidance; the installation remains installed. Ordinary service restarts do not open browsers. Every run writes a private log to `${XDG_STATE_HOME:-~/.local/state}/Quasar/install-*.log`. Canceling the polkit prompt leaves the system unchanged. **Manual install** shows the terminal commands instead. A server without a desktop uses the manual commands below.

## Prerequisites

- Linux x64 with systemd (`systemctl`) for the service install. A foreground run does not need systemd.
- glibc 2.38 or newer, for example Debian 13 or Ubuntu 24.04. The native libraries LinuxCompat loads into the Dedicated Server need it. On Ubuntu 22.04 (glibc 2.35, .NET 10 from the `ppa:dotnet/backports` PPA) Quasar installs and downloads everything, but every server start aborts at `Havok: Physics.Init` with exit code 134, and Magnetar's crash report says `GLIBC_2.38' not found (required by .../LinuxCompat-*/Bin/libHavok.so)`. Debian 12 has glibc 2.36.
- The .NET 10 ASP.NET Core runtime. `install.sh` checks for both `Microsoft.NETCore.App 10.x` and `Microsoft.AspNetCore.App 10.x` in `dotnet --list-runtimes` before it copies any file. Magnetar runs on it, and its packages bring ICU, which `Quasar` needs too. A source checkout needs the .NET 10 SDK instead, because the script runs `dotnet publish`.
- `ca-certificates`. Quasar downloads SteamCMD, Magnetar and plugins over HTTPS, and SteamCMD needs them to log in to Steam. Minimal Ubuntu installs can lack them.
- 32-bit libraries for SteamCMD, which Quasar uses to download and update the Dedicated Server. `linux32/steamcmd` is a 32-bit program and needs the 32-bit loader `/lib/ld-linux.so.2`. On Debian and Ubuntu install `lib32gcc-s1`. It brings `libc6-i386` with the loader, so the i386 architecture doesn't have to be enabled. On a host with i386 enabled, `libc6:i386` works too. Without them only SteamCMD fails: a Dedicated Server supplied with `QUASAR_DS_INSTALL_DIR` still works, but Quasar can't download or update one. See [Servers](Servers.md#managed-downloads-steamcmd-dedicated-server-magnetar).
- polkit, for a system service install. The system updater controls the main unit through a polkit rule; without `/etc/polkit-1/rules.d` the install stops with `System updater control needs polkit. Install polkit, then rerun repair-supervision.`
- A .NET 10 SDK only if an administrator installs a source-built QuasarHub UI plugin. Quasar then uses a .NET 10 SDK on `PATH`, or asks whether to download a pinned private SDK into `<install-dir>/ManagedRuntime/Tools/DotNetSdk/`. The private SDK needs no `sudo` and does not change `PATH`. See [UI plugins](UiPlugins.md).
- Git is not required.

On Debian and Ubuntu, before the first start:

```bash
sudo apt update && sudo apt install ca-certificates lib32gcc-s1
```

Checked on minimal Debian 13 and Ubuntu 24.04 installs, up to a server that reaches `Game ready`. On a host without ICU, `./Quasar serve` stops with `Couldn't find a valid ICU package installed on the system`; the .NET runtime packages bring it.

`--install-dependencies` installs CA certificates, the SteamCMD 32-bit libraries and polkit with the detected package manager (`apt-get`, `dnf`, `yum`, `pacman` or `zypper`). It prints the commands and asks first unless `--yes` is given. Without the option, `install.sh` warns when `/etc/ssl/certs` is empty or `/lib/ld-linux.so.2` is missing, prints the `apt install` command, then installs as usual.

### What install.sh does when .NET is missing

1. It detects the first available package manager in this order: `apt-get`, `dnf`, `yum`, `pacman`, `zypper`.
2. It prints the exact commands it would run. Packages: `aspnetcore-runtime-10.0` (runtime) or `dotnet-sdk-10.0` (SDK); on `pacman` it uses `aspnet-runtime` and `dotnet-sdk aspnet-runtime`.
3. On Debian 13 the `apt` commands first add Microsoft's package feed (`packages-microsoft-prod.deb`).
4. The last command links an existing `dotnet` binary from `/usr/share/dotnet`, `/usr/lib64/dotnet` or `/opt/dotnet` to `/usr/local/bin/dotnet`, only if `dotnet` is not on `PATH` after the package install.
5. It asks `Run these commands now? [y/N]`. Any answer other than yes exits before files or services change.

Pass `--yes` (or `-y`) to run the commands without the prompt (it then uses `sudo -n`). Without `--yes` in a non-interactive shell the script refuses: `Non-interactive terminal; refusing automatic .NET installation.` If no supported package manager exists, install .NET 10 yourself from `https://dotnet.microsoft.com/download/dotnet/10.0` and rerun the script.

## Run in the foreground

```bash
tar -xzf quasar-linux-x64.tar.gz
cd Quasar
./Quasar serve --foreground
```

Quasar prints `Quasar` and its URL (`http://127.0.0.1:8080` with the shipped settings) and opens it in a browser when a desktop session is available (`DISPLAY` or `WAYLAND_DISPLAY` set). State lives in the extracted folder. Logs go to `Logs/quasar.log` there.

- Ctrl+C or SIGTERM stops Quasar. Managed servers keep running if `Quasar:PreserveManagedServersOnShutdown` is on (the default).
- **Shutdown Quasar** in the UI saves the server state and stops Quasar; the servers keep running.
- **Restart Quasar** is not available in a foreground run, because no service brings Quasar back.
- An update started from the UI stages the release but can't swap the running files. Stop Quasar, run `./Quasar update --retry`, then start it again. See [Updates](Updates.md).

A foreground run of a 1.4.1 package records the migration receipt after it starts, the same as a service install.

### Launcher commands and flags

`./Quasar --help` prints:

```
Quasar [serve] [--headless] [--foreground]
Quasar update --version <release> | --status | --retry
Quasar cluster --help
Quasar activate-release --restore (internal recovery; Quasar must be stopped)
```

| Command or flag | Effect |
| --- | --- |
| `serve` (or no command) | Runs Quasar until stopped |
| `--foreground`, `--console` | Console mode with browser auto-open |
| `--headless` | API-only mode, see [Configuration](Configuration.md#headless-mode) |
| `--service` | Used by the service unit. Service settings come from the installation metadata, see [The generated unit](#the-generated-unit). |
| `--version` | Prints the full version, for example `1.5.0.0`, and exits |
| `update --version <release>`, `update --status`, `update --retry` | Application updates from the terminal, see [Upgrading](#upgrading) |
| `rollback` or `--rollback` | Queues a return to the retained previous application for the updater service |
| `activate-release --restore` | Runs `Updater/Quasar.Updater recover` to restore the retained previous application. Stop Quasar and its updater first. |
| `cluster <verb> ...` | Cluster API client, see [Configuration](Configuration.md#cluster-cli) |

The full command list and exit codes are in [Configuration](Configuration.md#command-line).

## Install as a systemd service

From the extracted package, as your normal user:

```bash
./install.sh --start
systemctl --user status quasar.service quasar-updater.service
```

With an extracted release archive, `install.sh` installs in place: the folder that holds the script becomes the install directory, and Quasar keeps all its state there. To install somewhere else, pass `--install-dir`, for example the guided installer's default:

```bash
./install.sh --install-dir ~/.local/share/Quasar --start
```

The script then copies the package there. Before it copies anything it checks .NET, the 1.4.1 migration requirement (`Updater/Quasar.Updater check-upgrade-path`) and that no Quasar of this installation is running (`check-stopped`, which fails with `A Quasar process from this installation is still running. Stop it before replacing files.`). An existing `appsettings.json` in the install directory is kept.

The UI is then at `http://localhost:8080` (or the port in `appsettings.json`). The script ends with a summary and the restart command.

### install.sh options

| Option | Default | Effect |
| --- | --- | --- |
| (none) | user services | Writes the units to `~/.config/systemd/user/`, enables them, does not start them. |
| `--start` | off | Restarts or starts the updater and the main service after installing. |
| `--system` | off | Machine-wide services in `/etc/systemd/system/`. Must run as root (`sudo`). |
| `--user <user>` | the `sudo` caller, or `root` | Account the system services run as. Only valid with `--system`. |
| `--user-service` | on | Explicitly select user services. |
| `--install-dir <dir>` | the script's folder for release archives; `<run-user-home>/.local/share/Quasar` for source checkouts | Copies Quasar to `<dir>` and points the services there. |
| `--service-name <name>` | `quasar` | Unit names `<name>.service`, `<name>-updater.service` and `<name>-clusters.service`. |
| `--no-enable` | off | Do not enable the units at boot. |
| `--repair-supervision` | off | Rewrites the units and installation metadata of an already copied current package without copying files. Needs the same path, name and scope; a system service also needs its existing `--user`. |
| `--install-dependencies` | off | Install CA certificates, SteamCMD 32-bit libraries and polkit through the package manager. |
| `--install-renice-helper` | off | Builds `quasar-renice.c` with `cc` and installs `/usr/local/bin/quasar-renice` as setuid root. |
| `--yes`, `-y` | off | Run package install commands without asking. |
| `--no-build` | automatic for release archives | Source checkouts: install an existing publish output instead of running `dotnet publish`. |
| `--configuration <name>`, `--runtime <rid>` | `Release`, `linux-x64` | Source checkouts only: publish settings. |

The script refuses `/`, `/bin`, `/boot`, `/dev`, `/etc`, `/home`, `/lib`, `/lib64`, `/proc`, `/root`, `/run`, `/sbin`, `/sys`, `/tmp`, `/usr` and `/var` as install directories. It does not open firewall ports. `./install.sh --help` lists the options.

### User service or system service

- User services run as the user who ran `install.sh` and are managed with `systemctl --user`. To start them at boot before that user logs in, enable lingering once: `sudo loginctl enable-linger <user>`.
- System services (`sudo ./install.sh --system`) run as the `sudo` caller unless you pass `--user <user>`, and are managed with `sudo systemctl`. When run as root, `install.sh` makes the run user the owner of the install directory.

```bash
systemctl --user status  quasar.service quasar-updater.service
systemctl --user restart quasar.service
journalctl --user -u quasar.service

sudo systemctl status quasar.service quasar-updater.service    # system services
```

### The generated unit

`install.sh` writes three units next to each other:

| Unit | What it runs |
| --- | --- |
| `<name>.service` | `Quasar serve --service` from the install directory. `Restart=on-failure`, `RestartSec=5`, `KillMode=process`, `TimeoutStopSec=120`. Sets `QUASAR_INSTALL_DIR`, `QUASAR_HOST_STOP_UNIT` and `QUASAR_CONSOLE_LOGGING=true`; stdout and stderr go to the journal. `Wants=` the updater unit. |
| `<name>-updater.service` | `Updater/Quasar.Updater --installation <metadata>`. `Restart=on-failure`, `RestartSec=5`. Runs as the same account as Quasar. |
| `<name>-clusters.service` | A oneshot unit whose stop action runs `LocalHost/stop-clusters`, so the clusters on this PC are saved and stopped at logout or shutdown. See [Cluster hosts](ClusterHosts.md#the-local-host). |

New and repaired main units copy application messages to the journal at the configured minimum level and format, alongside `Logs/quasar.log`. File and Discord logging remain enabled. Existing units need `install.sh --repair-supervision` with their original paths, service name, account and scope, then a main-service restart.

`KillMode=process` makes systemd stop only the Quasar process, so detached game servers keep running when Quasar stops or updates.

The installer also writes the installation metadata: install and data directory (the same folder on Linux), service names, accounts, the release repository and package name the updater trusts, and the health URL and token the updater uses to confirm a new version. For user services the record is `<install-dir>/service-installation.json`. For system services it is `/etc/quasar/<name>.json` (mode 0600, owned by the run user), and `service-installation.json` in the install directory only points to it. A system install also writes `/etc/polkit-1/rules.d/60-quasar-<name>.rules`, which lets the run user start, stop and restart `<name>.service` and nothing else, so the updater doesn't need root.

Quasar checks this wiring on start and shows the result on Updates. The main PID must belong to the configured unit and run the installed executable as the recorded account, and the unit needs `Restart=on-failure` and `KillMode=process` without treating exit code 75 as success. When something doesn't match, Quasar reports that it can't restart itself; the fix is the current installer with `--repair-supervision`.

### Stopping and the Shutdown Quasar action

**Shutdown Quasar** (in the **Quasar power** dialog, admin role) saves the managed server state and stops Quasar with exit code 0. `Restart=on-failure` treats that as a clean stop, so the main unit stays inactive. Servers keep running without supervision until their agent's offline policy takes over (see [Configuration](Configuration.md#shutdown-and-agent-behavior)). Start the service again to bring Quasar back.

**Restart Quasar** hands the restart to the updater service; servers keep running. Restart is enabled only after native service verification succeeds for the current Quasar process. If verification is missing, stale, unreadable or denied, the button stays disabled and shows repair guidance. An unsupported restart request leaves Quasar running. Wait for the next verification on Updates or repair supervision with the current installer, preserving paths, service names, account and scope. A pending update must finish before a manual restart can be queued. **Save and stop all servers** saves and stops standalone servers and managed clusters and leaves Quasar running.

`systemctl stop` follows `Quasar:PreserveManagedServersOnShutdown`. Updates always keep servers running and save their state for the next Quasar to adopt.

### Raising game server priority

Quasar applies the per-server process priority with `renice`. Without root, normal users can only lower priority, so High and Above normal fail and the server shows `Install /usr/local/bin/quasar-renice to use <priority> process priority on Linux.` Install the helper once (needs `cc` and `sudo`):

```bash
~/.local/share/Quasar/install.sh --install-renice-helper --no-build --no-enable
```

The helper accepts only nice values -10, -5, 0, 5 and 10, only for processes owned by the caller, and only for Magnetar executables (`MagnetarInterim.bin`, `MagnetarInterim`, `MagnetarInterim.exe`, `MagnetarLegacy.exe`). Nice is per thread on Linux, so it renices every thread of the server.

Quasar updates don't rebuild the helper. Helpers installed before Magnetar shipped `MagnetarInterim.bin` reject it, and the server shows that `/usr/local/bin/quasar-renice` may be outdated. The warning stays on the server card and in the List view until a priority applies. Older helpers also change only the server's main thread; Quasar then runs them once per thread and logs that the helper should be reinstalled. Run the same command again to rebuild it, then restart the server.

### System limit warnings

At startup Quasar checks four Linux limits and logs a warning to `quasar.log` for each one below its minimum. The open file limit applies to each process. The other three are shared by every process of the user, so a host running many servers under one user runs out of them first:

| Limit | Minimum |
| --- | --- |
| Open files (`ulimit -n`) | 65536 |
| Processes and threads (`ulimit -u`) | 16384 |
| `fs.inotify.max_user_instances` | 1024 |
| `fs.inotify.max_user_watches` | 524288 |

The warning includes the fix: a `limits.conf` line or a `Limit*=` setting in the unit for the first two, a `sysctl` command for the inotify ones. The generated unit sets no limits. Quasar starts anyway; a small setup may never reach them.

## Where Quasar keeps its files

On Linux the install directory is also the data directory, `QUASAR_INSTALL_DIR`. The installation metadata sets it for the services; a foreground run uses the folder of `Quasar`. The guided installer uses `~/.local/share/Quasar`.

| Path under the install root | Contents |
| --- | --- |
| `Quasar`, `wwwroot/`, `Agent/` | The application, its UI assets and the deployable Quasar.Agent |
| `Updater/Quasar.Updater` | The separate updater. Application updates don't replace it; the installer does. |
| `Host/Quasar.Host` | The Host executor for clusters |
| `release.json`, `launcher-files.txt` | Package version and the list of application files an update replaces |
| `service-installation.json` | The installation metadata (user services), or a pointer to `/etc/quasar/<name>.json` (system services) |
| `appsettings.json` | Settings. Pages such as Security and Updates save their changes here. See [Configuration](Configuration.md). |
| `rbac.json` | Role mappings. See [Access control](AccessControl.md). |
| `Logs/quasar.log`, `Logs/discord.log` | Application logs. Default level `Warn`. Override the folder with `QUASAR_LOG_DIR` or `Quasar:Logging:Directory`. |
| `Magnetars/<server>/` | Per server: `server.json`, `DedicatedServer/` (DS app data, saves), `Magnetar/` (Magnetar profiles and config), `History/`, `analytics.jsonl` |
| `WorldTemplates/<id>/` | World templates |
| `Clusters/<name>/` | Cluster definitions |
| `LocalHost/` | The local Host executor's configuration, state (cluster deployments and saves) and log. See [Cluster hosts](ClusterHosts.md#the-local-host). |
| `Backups/` | Configuration backup ZIPs. Override with `QUASAR_BACKUP_DIR` or the Backups page. |
| `ManagedRuntime/Tools/` | Downloaded `Magnetar/`, `SteamCMD/`, `SteamCmdHome/` (private HOME for SteamCMD), `SpaceEngineersDedicatedServer/`, `DotNetSdk/` |
| `ManagedRuntime/WebService/<version>/` | Web worker releases left from a Bootstrap install |
| `ManagedRuntime/Cache/` | Download cache |
| `Updates/` | `operation.json` (update status), `migration-1.4.1.json` (migration receipt), staged packages and `LauncherTransaction/` (the complete previous application) |
| `.update-cache/` | The updater's private copy of a downloaded package |
| `Diagnostics/` | Diagnostic capture and queues, unless `QUASAR_DIAGNOSTICS_DIRECTORY` points elsewhere. See [Diagnostics](Diagnostics.md). |
| `DataProtection-Keys/` | Keys that encrypt stored secrets such as `github-updates.json` and `steam-workshop.json` |
| `supervisor-state.json`, `service-manifest.json` | Supervisor runtime state and the running Quasar's address and process |
| `discord.json`, `branding.json`, `Branding/`, `backup-settings.json`, `known-players.json`, `data-handling-consent.json` | Settings saved from the UI |

Back up the whole install root as one unit, with the keys. The service does not send Quasar's log to the journal; `journalctl` shows only what Quasar prints before logging starts, such as a refused start. Read `Logs/quasar.log`.

## Network ports

| Port | Default | Notes |
| --- | --- | --- |
| Web UI and API (TCP) | `8080` | `Quasar:Host` / `Quasar:Port` in `appsettings.json`. The shipped `Host` is `0.0.0.0` (all interfaces). Set `127.0.0.1` to allow only local access. After changing the port or host, rerun the installer with `--repair-supervision` so the updater's health check uses the same address. |
| Game server port (UDP) | first free port from `27016` | Set per server. |
| DS Steam port | game port + 1000 | Quasar writes it into the DS config. |
| DS Remote API port | game port + 2000 | Quasar writes it into the DS config. |

The updater checks Quasar's health on a local address with a token from the installation metadata. Neither `install.sh` nor Quasar changes firewall rules. Keep the web port behind a firewall or an HTTPS reverse proxy if the machine is reachable from untrusted networks; see [Access control](AccessControl.md). Port details per server are in [Servers](Servers.md).

## Uninstall

```bash
~/.local/share/Quasar/uninstall.sh            # save the clusters on this PC, stop and remove the user services
~/.local/share/Quasar/uninstall.sh --purge    # also delete the install directory
sudo ./uninstall.sh --system --install-dir /path/to/quasar --purge   # system services
```

| Option | Default | Effect |
| --- | --- | --- |
| `--service-name <name>` | `quasar` | Units to remove: `<name>.service`, `<name>-updater.service` and `<name>-clusters.service` |
| `--install-dir <dir>` | the script's folder; `/opt/quasar` when `--system` is given without `--install-dir` | Folder `--purge` deletes |
| `--user-service` | on | Remove the user units from `~/.config/systemd/user/` |
| `--system` | off | Remove the units from `/etc/systemd/system/`, plus `/etc/quasar/<name>.json` and the polkit rule. Needs root. |
| `--purge` | off | Delete the install directory, including all servers, saves, backups and logs inside it |

The script first disables and stops the updater unit. Then it stops `<name>-clusters.service`, which saves and stops the clusters on this PC while the local Host still runs. Without that unit (an older install) it runs `LocalHost/stop-clusters` itself when the Host runs. Then it stops and disables the main service, stops the local Host process recorded in `LocalHost/host.pid`, deletes the unit files, and runs `daemon-reload` and `reset-failed`. Without `--purge` all data stays, `LocalHost/` included.

A local Host that an older Quasar version installed in `~/.local/share/Quasar/Hosts/<id>` is left alone, because another install may use it. The script prints the commands to remove it. Managed game servers are detached and can keep running after the service stops, so use **Save and stop all servers** first if you want them stopped. With `--system`, always pass `--install-dir` together with `--purge`, otherwise the script targets `/opt/quasar`.

## Upgrading

Use **Update Quasar** on the Updates page, or from the install directory as the service account:

```bash
./Quasar update --version <release>   # the full version shown on the release page
./Quasar update --status
./Quasar update --retry               # after fixing a reported failure
```

Quasar downloads the complete package and checks it against `SHA256SUMS`. The updater service checks its own copy of the archive, stops only the main service, keeps the complete previous application in `Updates/LauncherTransaction`, replaces the files, starts Quasar and confirms the exact version and process through the health endpoint. A failed start restores the previous application. Status survives restarts and reconnects. Game servers keep running. Details, exit codes and recovery are on [Updates](Updates.md).

The updater does not replace its own files. To upgrade the updater, stop both services and run the new package's `install.sh` against the existing installation:

```bash
systemctl --user stop quasar.service quasar-updater.service
./install.sh --no-build --install-dir /path/to/Quasar --service-name quasar --start
```

For system services use `sudo`, `--system` and the existing `--user <account>`.

### Migrating a Bootstrap install

A Bootstrap-era install moves to the native services through the complete 1.4.1 package. Save and stop the worlds, back up `appsettings.json`, credentials and `DataProtection-Keys/` together, and keep a copy of the old installation. Extract the 1.4.1 package to a temporary folder and run its `install.sh` with the existing install directory, service name and scope as above. The installer keeps the existing `appsettings.json` and data and abandons any pending Bootstrap activation (`Updates/bootstrap-update-request.json` is renamed to `.migrated`). When the files were already replaced with Quasar stopped, `--repair-supervision` with the same path, name and scope rewrites only the units and metadata. Then start 1.4.1 and check **Updates** and `./Quasar update --status` before moving on to 1.5.

### Move the install root

Stop the main and updater services and the managed servers. Copy the complete install root, keys included, to the new location, then run the installer from the copy with `--install-dir <new-root>` and the existing service name, account and scope so the units and metadata point to the new place. Check the Updates page and `update --status` before you remove the old copy.

Blank managed server paths and relative overrides follow the new root automatically. Absolute overrides stay at their configured external locations. Older `server.json` files may hold absolute defaults; opening and saving the server rewrites them under the current root. If both the old and new server data trees exist, copy the authoritative one first, then use **Edit Server -> Paths -> Use Managed Defaults**.

## Cluster machines

The package contains `Host/Quasar.Host`. On the Quasar machine, Quasar runs it as its local Host executor. A separate cluster machine gets its Host through enrollment from the UI, and its updates are separate from Quasar's own updates. See [Cluster hosts](ClusterHosts.md).

## Common problems

- `Quasar 1.5 and later require a completed Quasar 1.4.1 migration first. ...`: install and start the complete 1.4.1 package first (see the top of this page).
- `User service install is the default; run install.sh without sudo.` You ran `sudo ./install.sh` without `--system`. Run it as the normal user, or add `--system`.
- `System service install needs root. Run: sudo ./install.sh --system`: `--system` needs `sudo`.
- `--user is only supported with --system installs.`: drop `--user` or add `--system`.
- `Repairing a system service requires --user <account> to preserve its execution account.`: pass the account the service runs as.
- `Replace the complete current package before repairing native services.`: `--repair-supervision` found no `Quasar`, `Updater/Quasar.Updater` or `launcher-files.txt` in the install directory. Copy the complete package first.
- `A Quasar process from this installation is still running. Stop it before replacing files.`: stop the service or the foreground run, then rerun the installer.
- `systemctl not found; systemd install cannot continue.`: the machine has no systemd. Run `./Quasar serve --foreground` instead, or use [Docker](Docker.md).
- `.NET 10 ASP.NET Core runtime is still missing after installation attempt.`: the package install did not provide .NET 10. The script lists the SDKs and runtimes it found. Install .NET 10 manually and rerun.
- `Refusing unsafe install directory: <dir>`: pick a dedicated folder.
- `Cannot read this installation: ... Use an elevated Windows shell or the configured Linux service account; repair installation metadata if needed.` (exit code 3): `Quasar` was started by a user that cannot read `service-installation.json` or the record it points to. Run commands as the service account.
- The Updates page says Quasar can't restart itself and names a unit problem: the units don't match the installation metadata. Rerun the current `install.sh` with `--repair-supervision` and the existing path, name and scope.
- The guided installer says `Graphical installation needs kdialog or zenity.` or `polkit (pkexec) is unavailable.`: install them, or use the manual commands.
- The SteamCMD row of the Managed Runtime panel fails with `ExitCode=127` and `linux32/steamcmd: cannot execute: required file not found`: the host lacks SteamCMD's 32-bit libraries. Install them (see [Prerequisites](#prerequisites)) and click **Retry** on the SteamCMD row.
- Quasar logs `The remote certificate is invalid because of errors in the certificate chain: PartialChain` for every download, or SteamCMD fails with `unable to load trusted SSL root certificates` and exit code 254: the host lacks `ca-certificates`. Install it (see [Prerequisites](#prerequisites)) and restart Quasar.
- `./Quasar serve` stops with `Couldn't find a valid ICU package installed on the system`: install the .NET 10 runtime (see [Prerequisites](#prerequisites)), whose packages bring ICU.
- The services don't start at boot: enable lingering for user services (`sudo loginctl enable-linger <user>`), or check that `--no-enable` was not used.
- The UI doesn't come up after a port change: another program may use the port, or the port was changed only through an environment variable. Check `Logs/quasar.log`, set `Quasar:Port` in `appsettings.json`, and rerun the installer with `--repair-supervision`.

## Sources
- `README.md`
- `Docs/Installation.md`
- `Docs/QuickStart.md`
- `Docs/LinuxDeploymentAndUpdates.md`
- `Docs/QuasarUpdates.md`
- `Docs/ReleaseNotes1.5.md`
- `Docs/Configuration.md`
- `install.sh`
- `uninstall.sh`
- `scripts/install-gui.sh`
- `scripts/Install.desktop`
- `scripts/readme-install-linux.md`
- `scripts/package-linux-release.sh`
- `tools/quasar-renice.c`
- `Shared/Diagnostics/LinuxLimits.cs`
- `Shared/Diagnostics/SystemLimits.cs`
- `.github/workflows/release.yml`
- `Quasar/appsettings.json`
- `Quasar/Program.cs`
- `Quasar/Services/QuasarApplicationEntry.cs`
- `Quasar.Bootstrap/UpdateCli.cs`
- `Quasar/Services/WebServiceOptions.cs`
- `Quasar/Services/WebServiceManifestHostedService.cs`
- `Quasar/Services/BrowserLauncher.cs`
- `Quasar/Services/QuasarLoggingConfigurator.cs`
- `Quasar/Services/QuasarShutdownService.cs`
- `Quasar/Services/DedicatedServerSupervisor.cs`
- `Quasar/Services/DedicatedServerRuntimePreparer.cs`
- `Quasar/Services/ServerManagementActions.cs`
- `Quasar/Services/ClusterLocalHost.cs`
- `Quasar/Components/Layout/MainLayout.razor`
- `Quasar/Components/Layout/QuasarControlDialog.razor`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
- `Quasar/Services/ClusterCommandService.cs`
- `Quasar/Services/ClusterReconciler.cs`
- `Quasar/Components/Shared/QuasarOperationLoader.razor`
- `Shared/Updates/QuasarServiceInstallation.cs`
- `Shared/Updates/NativeQuasarServices.cs`
- `Shared/Updates/QuasarUpgradePath.cs`
- `Shared/Updates/LauncherReplacement.cs`
- `Shared/ServiceHosting/NativeServiceFailures.cs`
- `Quasar.Updater/Program.cs`
- `Quasar.Updater/ConfigureInstallation.cs`
- `Quasar.Updater/InstallationProcesses.cs`
- `Quasar.Updater/TrustedUpdatePackage.cs`
- `Tests/Updates/service_unit_config.py`
- `Quasar.Tests/QuasarShutdownTests.cs`
- `Quasar.Tests/QuasarLoggingConfiguratorTests.cs`
