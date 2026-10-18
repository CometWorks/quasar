# Install on Linux

How to download, run and install Quasar on Linux: release assets, the .NET prerequisite, foreground runs, the `install.sh` systemd service, where Quasar keeps its files, uninstalling and upgrading. For the container image see [Docker](Docker.md); for Windows see [Install on Windows](InstallWindows.md).

## Release assets

Every Quasar GitHub release (`https://github.com/CometWorks/quasar/releases`) carries the Linux and Windows archives together with one combined `SHA256SUMS` file.

| Asset | Contents | Who needs it |
| --- | --- | --- |
| `quasar-installer-linux.tar.gz` | One top-level `Quasar/` folder: the `Quasar` Bootstrap launcher, `install.sh`, `uninstall.sh`, the default `appsettings.json`, `quasar-renice.c` and a `README.md` | Everyone. This is the file you download. |
| `quasar-web-linux-x64.tar.gz` | The web UI worker (`Quasar`), static web assets, `Agent/Quasar.Agent.dll`, `Agent/Magnetar.Protocol.dll`, `Quasar.Plugin.Abstractions.dll` and `Host/Quasar.Host` | Bootstrap downloads it on first start and on UI updates. You do not normally download it yourself. |
| `quasar-host-linux-x64.tar.gz` | The standalone Quasar.Host for cluster machines | Only cluster machines. See [Cluster hosts](ClusterHosts.md). |
| `quasar-symbols-linux-x64.zip` | Debug symbols matching the release | Developers analyzing crashes. |
| `SHA256SUMS` | SHA-256 of every archive | Quasar verifies every download against it. |

The installer archive does not contain the web UI. On first start Bootstrap downloads the latest `quasar-web-linux-x64.tar.gz` from GitHub, checks it against `SHA256SUMS`, extracts it to `<install-dir>/ManagedRuntime/WebService/<version>/` and writes `<install-dir>/Updates/active-release.json` pointing at it. The first start therefore needs outbound HTTPS access to GitHub. If the download fails, Bootstrap keeps retrying until it works; see [Common problems](#common-problems).

Both the launcher and the web worker are self-contained single-file x64 executables (`linux-x64`).

## Prerequisites

- Linux x64 with systemd (`systemctl`) for the service install. A foreground run does not need systemd.
- glibc 2.38 or newer, for example Debian 13 or Ubuntu 24.04. The native libraries LinuxCompat loads into the Dedicated Server need it. On Ubuntu 22.04 (glibc 2.35, .NET 10 from the `ppa:dotnet/backports` PPA) Quasar installs and downloads everything, but every server start aborts at `Havok: Physics.Init` with exit code 134, and Magnetar's crash report says `GLIBC_2.38' not found (required by .../LinuxCompat-*/Bin/libHavok.so)`. Debian 12 has glibc 2.36.
- .NET 10. `install.sh` checks it before it copies any file:
  - Installing from an extracted release archive needs the .NET 10 ASP.NET Core runtime. The script looks for both `Microsoft.NETCore.App 10.x` and `Microsoft.AspNetCore.App 10.x` in `dotnet --list-runtimes`.
  - Installing from a source checkout needs the .NET 10 SDK, because the script runs `dotnet publish`.
- A .NET 10 SDK is only needed later if an administrator installs a source-built QuasarHub UI plugin. Quasar then uses a .NET 10 SDK on `PATH`, or asks whether to download a pinned private SDK into `<install-dir>/ManagedRuntime/Tools/DotNetSdk/`. The private SDK needs no `sudo` and does not change `PATH`. See [UI plugins](UiPlugins.md).
- Git is not required.
- `ca-certificates`. Quasar downloads SteamCMD, Magnetar and plugins over HTTPS, and SteamCMD needs them to log in to Steam. Minimal Ubuntu installs lack them and `install.sh` doesn't add them there; on Debian 13 its .NET install commands do.
- 32-bit libraries for SteamCMD, which Quasar uses to download and update the Dedicated Server. `linux32/steamcmd` is a 32-bit program and needs the 32-bit loader `/lib/ld-linux.so.2`. On Debian and Ubuntu install `lib32gcc-s1`. It brings `libc6-i386` with the loader, so the i386 architecture doesn't have to be enabled. On a host with i386 enabled, `libc6:i386` works too. Without them only SteamCMD fails: a Dedicated Server supplied with `QUASAR_DS_INSTALL_DIR` still works, but Quasar can't download or update one. See [Servers](Servers.md#managed-downloads-steamcmd-dedicated-server-magnetar).
- Magnetar and the Dedicated Server need nothing else on Debian 13 and Ubuntu 24.04: Magnetar runs on the .NET 10 runtime, and the LinuxCompat native libraries load with the base system.

On Debian and Ubuntu, before the first start:

```bash
sudo apt update && sudo apt install ca-certificates lib32gcc-s1
```

Checked on minimal Debian 13 and Ubuntu 24.04 installs, up to a server that reaches `Game ready`. The self-contained launcher needs ICU, which the .NET runtime packages bring: on a host without it `./Quasar serve` stops with `Couldn't find a valid ICU package installed on the system`.

`install.sh` warns when `/etc/ssl/certs` is empty or `/lib/ld-linux.so.2` is missing and prints the `apt install` command, then installs as usual.

### What install.sh does when .NET is missing

1. It detects the first available package manager in this order: `apt-get`, `dnf`, `yum`, `pacman`, `zypper`.
2. It prints the exact commands it would run. Packages: `aspnetcore-runtime-10.0` (runtime) or `dotnet-sdk-10.0` (SDK); on `pacman` it uses `aspnet-runtime` and `dotnet-sdk aspnet-runtime`.
3. On Debian 13 the `apt` commands first add Microsoft's package feed (`packages-microsoft-prod.deb`).
4. The last command links an existing `dotnet` binary from `/usr/share/dotnet`, `/usr/lib64/dotnet` or `/opt/dotnet` to `/usr/local/bin/dotnet`, only if `dotnet` is not on `PATH` after the package install.
5. It asks `Run these commands now? [y/N]`. Any answer other than yes exits before files or services change.

Pass `--yes` (or `-y`) to run the commands without the prompt (it then uses `sudo -n`). Without `--yes` in a non-interactive shell the script refuses: `Non-interactive terminal; refusing automatic .NET installation.` If no supported package manager exists, install .NET 10 yourself from `https://dotnet.microsoft.com/download/dotnet/10.0` and rerun the script.

## Run in the foreground

```bash
tar -xzf quasar-installer-linux.tar.gz
cd Quasar
./Quasar serve
```

- Bootstrap starts the web worker and prints `Quasar worker launching on http://127.0.0.1:8080`. Open that URL in a browser. A `serve` run does not open a browser by itself.
- Logs go to the console and to `Logs/quasar.log` inside the extracted folder.
- `Ctrl+C` stops the launcher. So does `kill <pid>` (SIGTERM) on the launcher: either way Bootstrap stops the web worker first and exits with code 0.
- State lives in the extracted folder, because Bootstrap sets `QUASAR_INSTALL_DIR` to its own folder when the variable is not set.
- If another Quasar is already healthy on the same install folder, `./Quasar serve` exits immediately with code 0 and no output.

API-only mode skips the Blazor UI, UI plugins, branding and static files, and keeps the supervisor, HTTP APIs, background jobs and the `/ws/agent` socket:

```bash
./Quasar serve --headless
curl http://127.0.0.1:8080/api/ready
```

`QUASAR_HEADLESS=true` or `Quasar:Headless=true` in `appsettings.json` does the same. See [Configuration](Configuration.md) for the other settings.

### Launcher commands and flags

| Command or flag | Effect |
| --- | --- |
| `serve` | Run Bootstrap and supervise the web worker in this process. Used by the service. |
| (no command) | Same as `ensure-running`: reuse a healthy Quasar, otherwise start one. From a terminal it runs in the foreground; from a script it starts a detached launcher and waits up to 60 seconds for it to become healthy. |
| `cluster ...` | Cluster command line tools. See [Clusters](Clusters.md). |
| `help`, `--help`, `-h` | Print the commands and options and exit. |
| `--headless` | API-only mode. |
| `--quiet` | No console output from Bootstrap. The service unit uses it. |
| `--force` | With `ensure-running`: stop an already running Quasar worker first. |
| `--open-browser` | With `ensure-running`: open the UI in a browser when a graphical session exists. |

`ensure-running` exit codes: `2` unknown command, option or argument (nothing is started), `3` launcher entry point not found, `4` Quasar did not become healthy in time, `5` the port is taken by another program.

## Install as a systemd service

```bash
mkdir -p ~/.local/share/Quasar
tar -xzf quasar-installer-linux.tar.gz -C ~/.local/share/Quasar --strip-components=1
~/.local/share/Quasar/install.sh --start
```

With an extracted release archive, `install.sh` installs in place: the folder that holds the script becomes the install directory, and Quasar keeps all its state there. The command above installs a user service named `quasar.service` and starts it. The UI is then at `http://localhost:8080` (or the port in `appsettings.json`).

### install.sh options

| Option | Default | Effect |
| --- | --- | --- |
| (none) | user service | Writes `~/.config/systemd/user/quasar.service`, enables it, does not start it. |
| `--start` | off | Restarts or starts the service after installing. Without it, start it yourself. |
| `--system` | off | Machine-wide service in `/etc/systemd/system/<name>.service`. Must run as root (`sudo`). |
| `--user <user>` | the `sudo` caller, or `root` | Account the system service runs as. Only valid with `--system`. |
| `--user-service` | on | Explicitly select the user service. |
| `--install-dir <dir>` | the script's folder for release archives; `<run-user-home>/.local/share/Quasar` for source checkouts | Copies Quasar to `<dir>` and points the service there. |
| `--service-name <name>` | `quasar` | systemd unit name, `<name>.service`, and the clusters unit `<name>-clusters.service`. |
| `--no-enable` | off | Do not enable the unit at boot. |
| `--install-renice-helper` | off | Builds `quasar-renice.c` with `cc` and installs `/usr/local/bin/quasar-renice` as setuid root. |
| `--yes`, `-y` | off | Run .NET package install commands without asking. |
| `--no-build` | automatic for release archives | Source checkouts: install an existing publish output instead of running `dotnet publish`. |
| `--configuration <name>`, `--runtime <rid>` | `Release`, `linux-x64` | Source checkouts only: publish settings. |

The script refuses `/`, `/bin`, `/boot`, `/dev`, `/etc`, `/home`, `/lib`, `/lib64`, `/proc`, `/root`, `/run`, `/sbin`, `/sys`, `/tmp`, `/usr` and `/var` as install directories. It does not open firewall ports.

### User service or system service

- The user service runs as the user who ran `install.sh` and is managed with `systemctl --user`. To start it at boot before that user logs in, enable lingering once: `sudo loginctl enable-linger <user>`.
- The system service (`sudo ./install.sh --system`) runs as the `sudo` caller unless you pass `--user <user>`, and is managed with `sudo systemctl`. When run as root, `install.sh` makes the run user the owner of the install directory.

```bash
systemctl --user status  quasar.service
systemctl --user restart quasar.service
journalctl --user -u quasar.service

sudo systemctl status quasar.service      # system service
```

### The generated unit

Both modes write the same `[Service]` section (the system unit adds `User=` and `Group=`):

| Setting | Value | Why |
| --- | --- | --- |
| `ExecStart` | `<install-dir>/Quasar serve --quiet` | Bootstrap supervises the web worker. |
| `WorkingDirectory` | `<install-dir>` | |
| `Restart` / `RestartSec` | `on-failure` / `5` | Restarts after a crash or a Bootstrap self-update (exit code 75), not after a clean exit. |
| `KillSignal` | `SIGINT` | Graceful stop. |
| `KillMode` | `process` | `systemctl stop` signals only Bootstrap, so detached game servers keep running. |
| `TimeoutStopSec` | `1800` | Up to 30 minutes for a clean stop. |
| `SuccessExitStatus` | `130 143` | Exit after SIGINT or SIGTERM counts as success. |
| `Environment` | `QUASAR_MODE=Service`, `QUASAR_OPEN_BROWSER_ON_START=false`, `HOME=<run-user-home>`, `QUASAR_INSTALL_DIR=<install-dir>`, `QUASAR_HOST_STOP_UNIT=<path of the clusters unit>` | Service mode, no browser, fixed state root. The last one is for the local Host's warnings. |
| `WantedBy` | `default.target` (user) or `multi-user.target` (system) | |

The unit waits for `network-online.target`. Use `Restart=on-failure` in any custom unit too: `Restart=always` would bring Quasar back after the UI **Shutdown Quasar** action.

`install.sh` also writes and enables `<service-name>-clusters.service` in the same folder (a system unit with the same `User=`). It saves and stops the clusters on this PC at logout or shutdown, before Quasar's service stops. See [Cluster hosts](ClusterHosts.md#systemd-units). Installs made before it existed get it when you rerun `install.sh`.

### Stopping and the Shutdown Quasar action

- `systemctl stop` sends SIGINT. Bootstrap drains the web worker and exits. A SIGTERM to Bootstrap, from `kill` or a process manager, does the same. Managed servers keep running by default (`Quasar:PreserveManagedServersOnShutdown` is `true`) and Quasar adopts them again on the next start. The same goes for the local Host executor while it has running clusters; otherwise it stops with Quasar.
- The power button in the top bar opens **Quasar power** (admin role). **Shutdown Quasar** saves the managed server state, stops the web worker and Bootstrap with exit code 0. systemd leaves the service inactive. Quasar stops within about 10 seconds, also while a browser tab still shows the UI. Servers keep running without supervision. Start the service again to bring the UI back: `systemctl --user start quasar.service` or `sudo systemctl start quasar.service`.
- **Restart Quasar** in the same dialog restarts only the web worker under Bootstrap. Servers keep running.
- **Save and stop all servers** stops every server and leaves Quasar running.
- If Bootstrap exits unexpectedly, systemd restarts it after 5 seconds. If the web worker crashes, Bootstrap restarts it.

### Raising game server priority

Quasar applies the per-server process priority with `renice`. Without root, normal users can only lower priority, so High and Above normal fail and the server shows `Install /usr/local/bin/quasar-renice to use <priority> process priority on Linux.` Install the helper once (needs `cc` and `sudo`):

```bash
~/.local/share/Quasar/install.sh --install-renice-helper --no-build --no-enable
```

The helper accepts only nice values -10, -5, 0, 5 and 10, only for processes owned by the caller, and only for Magnetar executables (`MagnetarInterim.bin`, `MagnetarInterim`, `MagnetarInterim.exe`, `MagnetarLegacy.exe`). Nice is per thread on Linux, so it renices every thread of the server.

Quasar updates don't rebuild the helper. Helpers installed before Magnetar shipped `MagnetarInterim.bin` reject it, and the server shows that `/usr/local/bin/quasar-renice` may be outdated. The warning stays on the server card and in the List view until a priority applies. Older helpers also change only the server's main thread; Quasar then runs them once per thread and logs that the helper should be reinstalled. Run the same command again to rebuild it, then restart the server.

### System limit warnings

At startup the web worker checks four Linux limits and logs a warning to `quasar.log` for each one below its minimum. The open file limit applies to each process. The other three are shared by every process of the user, so a host running many servers under one user runs out of them first:

| Limit | Minimum |
| --- | --- |
| Open files (`ulimit -n`) | 65536 |
| Processes and threads (`ulimit -u`) | 16384 |
| `fs.inotify.max_user_instances` | 1024 |
| `fs.inotify.max_user_watches` | 524288 |

The warning includes the fix: a `limits.conf` line or a `Limit*=` setting in the unit for the first two, a `sysctl` command for the inotify ones. The generated unit sets no limits. Quasar starts anyway; a small setup may never reach them.

## Where Quasar keeps its files

Everything lives under the install root, `QUASAR_INSTALL_DIR`. The service unit sets it; a foreground run uses the launcher's folder. The default user-service install root is `~/.local/share/Quasar`.

| Path under the install root | Contents |
| --- | --- |
| `Quasar` | Bootstrap launcher |
| `appsettings.json` | Settings. Pages such as Security and Updates save their changes here. |
| `rbac.json` | Role mappings. See [Access control](AccessControl.md). |
| `Logs/quasar.log`, `Logs/discord.log` | Application logs. Default level `Warn`. Override the folder with `QUASAR_LOG_DIR` or `Quasar:Logging:Directory`. |
| `Magnetars/<server>/` | Per server: `server.json`, `DedicatedServer/` (DS app data, saves), `Magnetar/` (Magnetar profiles and config), `History/`, `analytics.jsonl` |
| `WorldTemplates/<id>/` | World templates |
| `Clusters/<name>/` | Cluster definitions |
| `LocalHost/` | The local Host executor's configuration, state (cluster deployments and saves) and log. See [Cluster hosts](ClusterHosts.md#the-local-host). |
| `Backups/` | Configuration backup ZIPs. Override with `QUASAR_BACKUP_DIR` or the Backups page. |
| `ManagedRuntime/Tools/` | Downloaded `Magnetar/`, `SteamCMD/`, `SteamCmdHome/` (private HOME for SteamCMD), `SpaceEngineersDedicatedServer/`, `DotNetSdk/` |
| `ManagedRuntime/WebService/<version>/` | Installed web UI worker releases |
| `ManagedRuntime/Cache/` | Download cache |
| `Updates/` | `active-release.json`, `appsettings.base.json`, `Staged/` update payloads |
| `Diagnostics/` | Diagnostic capture and queues, unless `QUASAR_DIAGNOSTICS_DIRECTORY` points elsewhere. See [Diagnostics](Diagnostics.md). |
| `DataProtection-Keys/` | Keys that encrypt stored secrets such as `github-updates.json` and `steam-workshop.json` |
| `supervisor-state.json`, `service-manifest.json` | Supervisor runtime state and the running worker's address |
| `discord.json`, `branding.json`, `Branding/`, `backup-settings.json`, `known-players.json`, `data-handling-consent.json` | Settings saved from the UI |

Back up the whole install root as one unit. With the service running, Quasar warnings and errors also appear in the systemd journal.

## Network ports

| Port | Default | Notes |
| --- | --- | --- |
| Web UI and API (TCP) | `8080` | `Quasar:Host` / `Quasar:Port` in `appsettings.json`. The shipped `Host` is `0.0.0.0` (all interfaces). Set `127.0.0.1` to allow only local access. Change the port in `appsettings.json`, not with `QUASAR_WEB_PORT`, because Bootstrap reads only the JSON file. |
| Game server port (UDP) | first free port from `27016` | Set per server. |
| DS Steam port | game port + 1000 | Quasar writes it into the DS config. |
| DS Remote API port | game port + 2000 | Quasar writes it into the DS config. |

Neither `install.sh` nor Quasar changes firewall rules. Keep the web port behind a firewall or an HTTPS reverse proxy if the machine is reachable from untrusted networks; see [Access control](AccessControl.md). Port details per server are in [Servers](Servers.md).

## Uninstall

```bash
~/.local/share/Quasar/uninstall.sh            # stop, disable and remove the user service
~/.local/share/Quasar/uninstall.sh --purge    # also delete the install directory
sudo ./uninstall.sh --system --install-dir /path/to/quasar --purge   # system service
```

| Option | Default | Effect |
| --- | --- | --- |
| `--service-name <name>` | `quasar` | Unit to remove |
| `--install-dir <dir>` | the script's folder; `/opt/quasar` when `--system` is given without `--install-dir` | Folder `--purge` deletes |
| `--user-service` | on | Remove the user unit from `~/.config/systemd/user/` |
| `--system` | off | Remove `/etc/systemd/system/<name>.service`. Needs root. |
| `--purge` | off | Delete the install directory, including all servers, saves, backups and logs inside it |

The script stops the service, disables it, deletes the unit file, runs `daemon-reload` and `reset-failed`. Without `--purge` all data stays. Managed game servers are detached and can keep running after the service stops, so use **Save and stop all servers** first if you want them stopped. With `--system`, always pass `--install-dir` together with `--purge`, otherwise the script targets `/opt/quasar`.

## Upgrading

- Quasar updates itself from the Updates page: the web worker is staged and activated there, and Bootstrap replaces its own launcher when the Updates page asks it to. Your `appsettings.json` is kept. See [Updates](Updates.md).
- On Linux Bootstrap exits with code 75 after replacing itself, and systemd starts the new launcher. In a foreground run nothing restarts it: run `./Quasar serve` again.
- Running `install.sh --install-dir <existing-root>` from a newly extracted archive copies every file of that archive into the install root, including the default `appsettings.json`. Save your `appsettings.json` first and put it back afterwards. Extracting a new installer archive over the install root with `tar` also replaces `appsettings.json`. Rerunning `install.sh` from inside an existing install root (in place) leaves `appsettings.json` unchanged.
- Rerun `install.sh` to change service options; it rewrites the unit and reloads systemd.

### Move the install root

Stop Quasar and its servers, copy the whole install root to the new place, then run `install.sh --install-dir <new-root>` so the unit gets the new `QUASAR_INSTALL_DIR`. Blank or relative server paths follow the new root. Absolute path overrides stay where they point. If an old `server.json` still contains absolute default paths, open the server and use **Edit Server -> Paths -> Use Managed Defaults**.

## Cluster machines

Cluster nodes run Quasar.Host from `quasar-host-linux-x64.tar.gz` in its own folder with its own configuration. See [Cluster hosts](ClusterHosts.md) and [Cluster setup](ClusterSetup.md).

## Common problems

- `User service install is the default; run install.sh without sudo.` You ran `sudo ./install.sh` without `--system`. Run it as the normal user, or add `--system`.
- `System service install needs root. Run: sudo ./install.sh --system`: `--system` needs `sudo`.
- `--user is only supported with --system installs.`: drop `--user` or add `--system`.
- `systemctl not found; systemd install cannot continue.`: the machine has no systemd. Run `./Quasar serve` in the foreground instead, or use [Docker](Docker.md).
- `.NET 10 ASP.NET Core runtime is still missing after installation attempt.`: the package install did not provide .NET 10. The script lists the SDKs and runtimes it found. Install .NET 10 manually and rerun.
- `Refusing unsafe install directory: <dir>`: pick a dedicated folder.
- The SteamCMD row of the Managed Runtime panel fails with `ExitCode=127` and `linux32/steamcmd: cannot execute: required file not found`: the host lacks SteamCMD's 32-bit libraries. Install them (see [Prerequisites](#prerequisites)) and click **Retry** on the SteamCMD row.
- Quasar logs `The remote certificate is invalid because of errors in the certificate chain: PartialChain` for every download, or SteamCMD fails with `unable to load trusted SSL root certificates` and exit code 254: the host lacks `ca-certificates`. Install it (see [Prerequisites](#prerequisites)) and restart Quasar.
- `./Quasar serve` stops with `Couldn't find a valid ICU package installed on the system`: install the .NET 10 runtime (see [Prerequisites](#prerequisites)), whose packages bring ICU.
- The service does not start at boot: enable lingering for the user service (`sudo loginctl enable-linger <user>`), or check that `--no-enable` was not used.
- `Error: port 8080 is already bound by another process and is not a healthy Quasar server.`: another program uses the port. Stop it or change `Quasar:Port` in `appsettings.json`.
- The UI doesn't come up after a fresh install: Bootstrap hasn't been able to download the web worker from GitHub yet, or `QUASAR_UPDATES_ENABLED=false` blocks the first download. Bootstrap retries a failed download on its own, 30 seconds after the first failure, then doubling up to every 10 minutes, and writes each wait to the journal (`journalctl --user -u quasar.service`):
  - `Failed downloading the Quasar web UI; retrying at <time> (in <n> s). <error>`: GitHub can't be reached. Fix network access; there is no need to restart Quasar.
  - `GitHub's API rate limit is used up, so Quasar can't download its web UI yet. Waiting for GitHub to reset the limit; retrying at <time> ...`: GitHub allows 60 anonymous API requests an hour per public IP address, and every machine behind the same NAT, CGNAT or hosting provider shares them. Quasar waits until the reset time GitHub sends (at most an hour) and then continues on its own. Bootstrap doesn't read `GITHUB_TOKEN`, and the token on the [Updates](Updates.md) page can only be saved once the UI is up; after that, it keeps later update checks out of the anonymous limit.
  - Older launchers (1.3.1.27 and earlier) gave up after the first failure, printed `Failed downloading initial Quasar web release.` and `Quasar worker launching on ...` with no worker behind it, and needed a restart once GitHub could be reached again.

  With updates disabled and no worker, or when no `quasar-web-linux-x64.tar.gz` asset is found, a foreground run (`./Quasar serve`) prints `Quasar has no running worker, so the web UI is not available.` after the reason.
- `Could not request Bootstrap shutdown. Check write access to the Quasar install directory.`: the service user cannot write to the install root. Fix ownership (`chown -R <user> <install-dir>`).

## Sources
- `README.md`
- `Docs/QuickStart.md`
- `Docs/LinuxDeploymentAndUpdates.md`
- `Docs/Configuration.md`
- `install.sh`
- `uninstall.sh`
- `scripts/readme-install-linux.md`
- `scripts/package-linux-release.sh`
- `tools/quasar-renice.c`
- `Shared/Diagnostics/LinuxLimits.cs`
- `.github/workflows/release.yml`
- `Quasar/appsettings.json`
- `Quasar.Bootstrap/Program.cs`
- `Quasar.Bootstrap/Quasar.Bootstrap.csproj`
- `Quasar/Program.cs`
- `Quasar/Services/WebServiceOptions.cs`
- `Quasar/Services/BrowserLauncher.cs`
- `Quasar/Services/QuasarLoggingConfigurator.cs`
- `Quasar/Services/QuasarShutdownService.cs`
- `Quasar/Services/DedicatedServerSupervisor.cs`
- `Quasar/Services/DedicatedServerRuntimePreparer.cs`
- `Quasar/Services/ServerManagementActions.cs`
- `Quasar/Components/Layout/MainLayout.razor`
- `Quasar/Components/Layout/QuasarControlDialog.razor`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
