# Servers

This page covers standalone managed servers in Quasar: what a server is made of, creating, editing, cloning and deleting servers, start/stop/restart/kill, crash and health recovery, ports, the .NET runtime choice, managed downloads, Steam Workshop mods, process priority and what happens to servers when Quasar itself restarts. Config profiles and world saves have their own page: [Config profiles](ConfigProfiles.md).

## What a managed server is

A managed server is one Space Engineers 1 Dedicated Server (DS) process that Quasar launches and supervises. It has three parts:

- Magnetar, the server launcher and plugin loader. Quasar starts the Magnetar launcher, which starts the DS with plugins.
- The Space Engineers Dedicated Server itself (Steam app `298740`).
- Quasar.Agent, a Magnetar plugin that Quasar copies into the server's Magnetar `Local` folder on every start. It connects back to Quasar over a WebSocket (`/ws/agent` on the Quasar web port), sends telemetry every 2 seconds and executes commands (save, stop, chat, kick, ban and others).

All managed servers run headless. Quasar writes the DS config and `LastSession.sbl` before every start, so the DS never shows its own configurator.

Each server has a stable **Server ID** (internally `UniqueName`). It is also the name of the server's folder under `<quasar-root>/Magnetars/`.

## Where server files live

`<quasar-root>` is the Quasar install root (see [Linux install](InstallLinux.md), [Windows install](InstallWindows.md); in Docker it is `/data`, see [Docker](Docker.md)).

| Path | Contents |
| --- | --- |
| `Magnetars/<Server ID>/server.json` | The server definition (all editor settings, goal state). |
| `Magnetars/<Server ID>/History/` | A timestamped copy of `server.json` on every save, plus `<timestamp>-deleted.json` when the server is deleted. |
| `Magnetars/<Server ID>/DedicatedServer/` | DS app-data (`-path`): `SpaceEngineers-Dedicated.cfg`, `SpaceEngineersDedicated*.log`, Steam Workshop content. |
| `Magnetars/<Server ID>/DedicatedServer/Saves/` | World saves. `LastSession.sbl` is written here before every start. |
| `Magnetars/<Server ID>/Magnetar/` | Magnetar app-data (`-config`): `Sources/sources.xml`, `Profiles/Current.xml`, `Local/` (Quasar.Agent.dll), `info_*.log`. |
| `Magnetars/<Server ID>/analytics.jsonl` | Analytics samples for the server. |
| `supervisor-state.json` | Runtime state of all servers (process IDs, restart counters, last restart record). |
| `ManagedRuntime/Tools/SteamCMD/` | Managed SteamCMD. |
| `ManagedRuntime/Tools/SteamCmdHome/` | Private `HOME` for SteamCMD on Linux. |
| `ManagedRuntime/Tools/SpaceEngineersDedicatedServer/` | Managed DS install (shared by all servers). |
| `ManagedRuntime/Tools/Magnetar/` | Managed Magnetar install (shared by all servers). |

The server editor's **Paths** section can override the DS app-data path, Magnetar app-data path, saves path and rendered DS config path. Blank means the managed default above. Relative values resolve from `<quasar-root>`. Absolute values stay where they are when the Quasar root moves. **Use Managed Defaults** clears the overrides in the editor only; it does not copy or delete files.

## Creating a server

On the **Dashboard**, click **New server**. This opens the **Create Server** dialog. Other entry points: **Import server** on the Dashboard imports an existing vanilla DS or Torch install (see [First setup](FirstSetup.md)), and **Clone server** in a server's menu copies an existing server.

Required fields:

- **Server ID**: letters, numbers, underscore and hyphen only. Must be unique. Error: "Only letters, numbers, underscore, and hyphen are allowed." or "A server named '...' already exists."
- **Configuration profile**: error "Configuration profile required." The **+** button next to it creates a profile without leaving the dialog.
- **World save**: a folder name inside the saves path that contains `Sandbox.sbc`. **Create From Template** copies a world template into a new save folder (see [Config profiles](ConfigProfiles.md#creating-a-world-save-from-a-template)). Errors: "World save required.", "World save must be a folder name, not a path.", "Selected world save not found. Create it from a template first.", "Selected world save is missing Sandbox.sbc."

Other identity fields:

- **Server name**: shown in Quasar lists. New dialogs fill an editable generated suggestion.
- **In-game server name**: the server-list title in Space Engineers. Blank uses Server name.
- **In-game world name**: the world name in the server browser. Blank uses Server ID. Quasar writes it into `SpaceEngineers-Dedicated.cfg` and into the save's `Sandbox_config.sbc` (`SessionName`) on every start.
- **Public server port** and **Listen IP**: see [Ports](#ports).
- **.NET runtime**: see [.NET runtime: .NET 10 or .NET Framework 4.8](#net-runtime-net-10-or-net-framework-48).

If the selected world save has mods that the selected profile lacks, the editor warns "Selected world has N mod(s) not in the selected configuration profile." Quasar writes the profile's mod list into the world on every start, which drops those mods. **Merge Into <profile>** adds them to the profile.

New servers start with the goal **Off**. Nothing launches until you click **Start**.

## Editing, renaming, cloning and deleting

- **Edit server** (server menu, three dots) opens the same editor. Saving writes `server.json` and a history copy. Most changes apply on the next start or restart.
- Renaming the **Server ID** requires a stopped server ("Stop the server before renaming it."). Quasar moves `Magnetars/<old ID>` to `Magnetars/<new ID>`. It fails if the target folder already exists.
- **Clone server** creates `<ID>-copy` (or `-copy-2` and so on), picks the next free port from 27016 upward, clears all path overrides and the world save, and sets the goal Off. After the editor, Quasar asks "Clone world state?": **Copy World** copies the source world into the clone; **Keep Save** uses the save chosen in the editor. If the source is running, Quasar copies the newest Space Engineers `Backup` snapshot of the world instead of the live folder, and fails if none exists.
- **Delete server** requires a stopped server ("Stop the server before deleting its definition."). It archives `server.json` into `History/` as `<timestamp>-deleted.json` and removes the server from Quasar. Quasar does **not** delete `Magnetars/<Server ID>/`; world saves, config and logs stay on disk. Delete that folder yourself to free the space. A new server with the same Server ID reuses whatever is left in that folder.
- **Create world template** (server menu) needs a stopped server with goal Off. See [Config profiles](ConfigProfiles.md#world-templates).
- **Convert to cluster** is described in [Clusters](Clusters.md).

Editing `server.json` by hand works too: Quasar watches the `Magnetars` folder and reloads changed definitions.

## Dashboard and server controls

The **Dashboard** shows every server as a card (**Cards** view) or as a row in a table (**List** view). Both show the state, health message, last restart record, port, config profile and the newest error line from the logs (click it to open the console at the error).

| Button | Shown when | What it does |
| --- | --- | --- |
| **Start** | Stopped, Crashed, Faulted | Sets the goal On and starts the server. Disabled while the managed runtime is still preparing or no world save is selected. |
| **Stop** | Running | Asks "Stop server?", sets the goal Off, then stops gracefully (see below). |
| **Restart** | Running | Graceful stop, then start. Keeps the goal On. |
| **Kill** | Starting, Restarting | Asks "Kill starting server?", sets the goal Off and kills the process tree immediately. Use it to cancel a wrong or stuck launch. |
| **Show console output** | Always | Opens the log console. |
| **Save** (expanded live data) | Agent connected | Asks the agent to save the world now. |

These buttons need the server control permission; editing and deleting need the server edit permission (see [Access control](AccessControl.md)).

### Goal state, process state and health

Quasar tracks three separate values per server:

- **Goal state** (`On` or `Off`) is what you want. It is saved in `server.json`. Start sets it On, Stop and Kill set it Off.
- **Process state** is what the process is doing: `Stopped`, `Starting`, `Running`, `Stopping`, `Restarting`, `Crashed`, `Faulted`.
- **Health** is `Unknown`, `Healthy`, `Warning` or `Unhealthy`, computed from agent telemetry.

A reconcile loop runs every 2 seconds and moves each server toward its goal: goal On and Stopped means start it, goal Off and running means stop it. A server only counts as **Running** once Quasar.Agent has connected and sent a telemetry snapshot showing the game is running. Until then it is **Starting**.

Because the goal is saved, Quasar starts every server whose goal is On when Quasar itself starts, for example after a host reboot.

`Crashed` and `Faulted` are not retried by the reconcile loop. Click **Start** after fixing the cause, or use **Clear Error Status** on the Dashboard problem banner. Clearing sets the goal Off, resets the restart counters and mod-download failure messages, and returns the server to `Stopped`.

### Graceful stop and save

When Quasar stops a server (Stop, Restart, health or scheduled restart) and Quasar.Agent is connected, Quasar:

1. Sends a save command and waits up to 15 seconds for the result.
2. Sends a stop command. The agent shuts the server down.
3. Waits up to 30 seconds for the process to exit, then kills the whole process tree.

If no agent is connected, steps 1 and 2 are skipped: Quasar waits 30 seconds and kills the process without saving.

### In-game commands from Quasar.Agent

On standalone servers Quasar.Agent registers these chat commands:

| Command | Effect |
| --- | --- |
| `!stop` | Saves the world and shuts the server down. Quasar sets the goal Off, so the server stays stopped. |
| `!quit` | Exits immediately without saving. Quasar sets the goal Off. |
| `!restart [seconds]` | Announces a countdown in chat (default 10 seconds, maximum 3600), saves and exits. Quasar keeps the goal On and starts the server again. A second `!restart` while one is pending fails with "Restart already scheduled." |

Discord `start` and `stop` commands set the same goal state as the UI; see [Monitoring](Monitoring.md).

## Crash detection and automatic restart

These settings are in the server editor's **Restart Policy** section:

| Setting | Default | Meaning |
| --- | --- | --- |
| **Restart when process crashes** (`RestartOnCrash`) | on | Restart the server when the process exits without Quasar asking for it while the goal is On. |
| **Restart delay (seconds)** (`RestartDelaySeconds`) | 5 | Wait before relaunching after an exit. |
| **Max consecutive restart attempts** (`MaxRestartAttempts`) | 3 | Crash restarts allowed in a row. |
| **Restart schedule (HH:mm, server local time)** (`DailyRestartTimeLocal`) | blank | Planned daily restarts. Several times allowed, separated by spaces, commas or new lines. Error: "Use HH:mm values from 00:00 to 23:59." |
| **Maximum uptime (HH:mm)** (`MaximumUptime`) | `08:00` | Restart once the process has been up this long. Blank disables it. Error: "Use HH:mm duration, for example 06:00." |

How it works:

- An unexpected exit with `RestartOnCrash` on and attempts left moves the server to `Restarting` with the message "Process exited with code N. Restarting." and relaunches after the delay. The restart record says "Crash recovery".
- The attempt counter resets when the server ran at least 1 minute before exiting. So the limit catches a server that crashes again within a minute of each start.
- When attempts run out, the server becomes `Faulted` with "Process exited with code N. Restart attempt limit (3) reached." Quasar does not retry on its own after that.
- With `RestartOnCrash` off, a non-zero exit or a failed start (below) leaves the server `Crashed`.
- A server that exits before it gets to `Running` failed to start, whatever its exit code. That's how the DS ends a world it can't load ("Session can not start" in its log, for example after a Workshop mod download failed). The message is "Start failed: process exited with code N before the world was ready", and it's retried and faulted like a crash, so the Dashboard counts it as an error. The goal stays On: Quasar.Agent doesn't report a stop before the world is ready as an admin stop.
- After a crash or fault, Quasar captures the newest error line from the DS and Magnetar logs and shows it on the Dashboard. After a failed start it takes the line from the DS log when it has one, since that's where the DS writes why the world didn't load.
- A scheduled restart fires within 5 minutes after the scheduled time, and only if the process has been up for at least 5 minutes.
- When **Avoid simultaneous scheduled restarts** is on (global, default on, `Quasar:AvoidSimultaneousScheduledRestarts` or `QUASAR_AVOID_SIMULTANEOUS_SCHEDULED_RESTARTS`), scheduled and maximum-uptime restarts wait while another server is starting, stopping or restarting. The card then says "Scheduled restart delayed; another server is stopping or restarting."

Every restart records its cause (Health policy, Agent attach recovery, Scheduled, Maximum uptime, Manual, In-game, Crash recovery), the reason, times and outcome (pending, recovered, failed). The latest record stays on the Dashboard across Quasar restarts.

### Launch failures that fault a server

Quasar sets `Faulted` without retrying when preparation fails. The Dashboard shows the message. Common ones:

| Message | Fix |
| --- | --- |
| `World save required.` | Select a world save in the editor. |
| `World directory not found: <path>` / `World directory '<path>' does not contain Sandbox.sbc.` | The save folder is missing or broken. Pick another save or restore it from a backup. |
| `Server '<ID>' has no config profile assigned. Assign a profile from the Configs page before starting the server.` | Select a configuration profile in the editor. |
| `Unknown Quasar config profile '<id>' for server '<ID>'.` | The profile was deleted or renamed on disk. Select an existing profile. |
| `Executable not found: <path>` / `Working directory not found: <path>` | A **Paths** override points to a missing file. Clear it to use the managed Magnetar. |
| `Launch arguments cannot include -ignorelastsession for Quasar-managed servers.` | Remove `-ignorelastsession` from **Launch arguments**. |
| `Startup blocked: oxygen is disabled but the save contains oxygen-capable blocks or definitions (...). Enable Oxygen or remove the oxygen blocks before starting.` | Turn on **Enable Oxygen** in the config profile, or remove the blocks. Quasar blocks this because Space Engineers crashes on load in that combination. |
| `Quasar.Agent did not attach after N attempt(s). ...` | See [Health checks](#health-checks-and-the-watchdog). |
| `DedicatedServer64 path not found. Set QUASAR_DS64_PATH, ...` | The managed DS install is missing and could not be downloaded. Check the Managed Runtime panel. |

While the managed runtime is not ready, Start is refused with "Managed SteamCMD, Magnetar, and Dedicated Server runtime are still preparing." and the server stays `Stopped`.

## Health checks and the watchdog

Settings are in the editor's **Health Policy** section:

| Setting | Default | Meaning |
| --- | --- | --- |
| **Enable health monitoring** | on | Off makes health `Unknown` ("Health monitoring disabled."). |
| **Auto-restart when unhealthy** | on | Restart (or retry the launch) when health becomes `Unhealthy`. |
| **Agent startup log inactivity timeout (seconds)** | 60 | During startup, give up if neither the DS log nor the Magnetar `info*.log` changed for this long before the first agent snapshot. |
| **Agent startup hard limit (seconds)** | 600 | Absolute wait for the agent from process launch, even while logs keep changing. |
| **Agent attach retry attempts** | 3 | Launch retries when the agent never attaches. |
| **Agent attach retry delay (seconds)** | 5 | Wait between those retries. |
| **Agent heartbeat timeout (seconds)** | 20 | A connected agent that sends nothing for this long makes the server `Unhealthy`. |
| **Simulation frame window (seconds)** | 30 | Window for the simulation progress check. |
| **Minimum average simulation rate** | 0.05 for servers created in the UI | Frames advanced / (seconds x 60). 1.00 is full sim speed. Below this the server is `Unhealthy`. |
| **Warn after uptime (hours)** | 12 | Show a Dashboard warning after this uptime. 0 disables it. |

What makes a server `Unhealthy`:

- The goal is On but the process is not running ("Goal state is ON but the process is not running.").
- During startup, the agent did not attach or send its first snapshot within the inactivity timeout or the hard limit.
- A running server's agent disconnected ("Quasar.Agent disconnected.").
- The heartbeat is older than the heartbeat timeout.
- The simulation rate over the window is below the minimum. A save in progress resets the window instead of counting as a stall.

What happens then, with **Auto-restart when unhealthy** on and the goal On:

- During `Starting`, Quasar kills the process, waits the retry delay and launches again ("Quasar.Agent attach timed out (1/3). Retrying in 5s."). After the last attempt the server becomes `Faulted`.
- During `Running`, Quasar does a graceful restart with cause "Health policy". It does this at most once per 30 seconds (or per restart delay, if longer). When the agent is still connected, the restart reason is also written to the server's Magnetar `info_*.log`.

To turn health monitoring off for the whole Quasar instance, set `Quasar:DisableServerHealthMonitoring` to `true` or `QUASAR_DISABLE_SERVER_HEALTH_MONITORING=true`. New servers then also start with both health checkboxes off.

When Quasar's bundled Quasar.Agent differs from the one deployed in a running server, the server card says "Bundled Quasar.Agent differs from the deployed Magnetar local DLL; restart this server manually to load the bundled agent." Quasar does not restart it for you.

## The server console

**Show console output** opens a log viewer with two tabs:

- **Server log**: the DS `SpaceEngineersDedicated*.log` files from the DS app-data folder.
- **Magnetar info log**: Magnetar's `info_*.log` files from the Magnetar app-data folder. Plugin log lines captured by Quasar.Agent are also written here.

Each tab has a file selector (**Most recent** or an older file), **Errors** (searches the log for error lines), **Refresh** and a download button. Refresh and auto-refresh work only for **Most recent**.

The console is read-only. To talk to players, use the **Chat** page or the chat box in a server's expanded live data on the Dashboard (see [Monitoring](Monitoring.md)).

**Space Engineers DS logs to keep** (Runtime section, default 5, range 1 to 1000) controls how many `SpaceEngineersDedicated*.log` files Quasar keeps. It deletes older ones on every start and stop.

## Ports

| Port | Default | Where it comes from |
| --- | --- | --- |
| Game port (UDP) | 27016 | **Public server port** in the editor (`ServerPort`). |
| Steam port | game port + 1000 (28016) | Quasar writes `SteamPort` into `SpaceEngineers-Dedicated.cfg` on every start. |
| DS Remote API port | game port + 2000 (29016) | Quasar writes `RemoteApiPort` on every start. |
| Quasar.Agent connection | Quasar web port (8080) | The agent connects to `QUASAR_BASE_URL` + `/ws/agent`. There is no separate agent port. |

Quasar derives the Steam and Remote API ports from the game port so several servers on one host do not collide on the Space Engineers defaults (8766 and 8080). You cannot set them separately.

New servers get the first free game port from 27016 upward. The editor checks that the game port is between 1 and 65535 ("Port must be between 1 and 65535.") and not used by another server ("Port already used by server '<ID>'."). It does not check the derived Steam and Remote API ports. Do not give a server a game port that equals another server's game port + 1000 or + 2000.

**Listen IP** (`ServerIP`) defaults to `0.0.0.0` (all interfaces). If the config profile's Online Mode is **Offline**, Quasar writes `127.0.0.1` instead and Quasar.Agent rejects any client that does not connect from loopback (see [Config profiles](ConfigProfiles.md#online-mode-and-offline-safety)).

Players connect to the host address and the game port. The port on each server card copies the direct connect address to the clipboard.

## .NET runtime: .NET 10 or .NET Framework 4.8

The editor's **.NET runtime** field picks which Magnetar build launches the server:

| Option | Magnetar build | Platforms |
| --- | --- | --- |
| **.NET 10** (default) | `MagnetarInterim` (`MagnetarInterim.exe` on Windows, `MagnetarInterim.bin` on Linux) | Windows and Linux |
| **.NET Framework 4.8** | `MagnetarLegacy.exe` | Windows only |

On Linux the field is disabled (".NET 10 is the only runtime available on this host machine."). A `server.json` copied from Windows with .NET Framework 4.8 selected runs on .NET 10 on Linux without an error. On Windows, use .NET Framework 4.8 only when a plugin requires it. Both builds install together into the managed Magnetar folder.

## Managed downloads: SteamCMD, Dedicated Server, Magnetar

When Quasar starts, it checks and downloads the managed runtime in the background. The Dashboard shows a **Managed Runtime** panel with progress until everything is ready. Server launches are blocked until then.

- **SteamCMD** comes from Valve's CDN (`steamcmd_linux.tar.gz` or `steamcmd.zip`). On Linux Quasar runs it with its own `HOME` (`ManagedRuntime/Tools/SteamCmdHome`) so it never touches a desktop Steam client's `~/.steam`, and prepares SteamCMD's `linux64` runtime, which it passes to servers through `LD_LIBRARY_PATH`.
- **Dedicated Server** is installed with SteamCMD (`+login anonymous +app_update 298740`). Quasar tries up to 3 times. If it still fails, the Managed Runtime panel shows **Retry** on the Dedicated Server row. A valid install has `DedicatedServer64` with the `SpaceEngineersDedicated` executable, `VRage.dll`, `Sandbox.Game.dll` and `SpaceEngineers.Game.dll`.
- **Magnetar** comes from the latest full GitHub release of `CometWorks/magnetar` (`MagnetarForLinux-*.7z` or `MagnetarForWindows-*.7z`). On each server start Quasar checks the latest release (at most once per 5 minutes) and installs it if it is newer. If GitHub cannot be reached, Quasar logs a warning and launches with the installed Magnetar.

What happens to the installed DS on each server start is set on the **Updates** page under **Dedicated Server update on launch**: **Skip** (use as is), **Update** (default, downloads only when Steam has a newer build) or **Validate** (re-checks every file, adds minutes). The setting is also `Quasar:ManagedRuntime:DedicatedServerLaunchUpdateMode` or `QUASAR_DS_LAUNCH_UPDATE_MODE`. See [Updates](Updates.md).

Override settings (section `Quasar:ManagedRuntime` in `appsettings.json`, or the environment variable):

| Setting | Environment variable | Purpose |
| --- | --- | --- |
| `DedicatedServerInstallDirectory` | `QUASAR_DS_INSTALL_DIR` | Where the managed DS is installed. |
| `DedicatedServer64OverridePath` | `QUASAR_DS64_PATH` (or `DS64`) | Use an existing `DedicatedServer64` folder. |
| `SteamCmdInstallDirectory` | `QUASAR_STEAMCMD_INSTALL_DIR` | Managed SteamCMD folder. |
| `SteamCmdPath` | `QUASAR_STEAMCMD_PATH` | Use an existing SteamCMD. |
| `SteamCmdHomeDirectory` | `QUASAR_STEAMCMD_HOME_DIR` | SteamCMD's private `HOME` on Linux. |
| `MagnetarInstallDirectory` | `QUASAR_MAGNETAR_INSTALL_DIR` | Managed Magnetar folder. |
| `MagnetarArchiveUrl` | `QUASAR_MAGNETAR_ARCHIVE_URL` | Install Magnetar from this archive URL (or `file://` path) instead of the GitHub release. |
| `PreferManagedDedicatedServerInstall` | `QUASAR_PREFER_MANAGED_DS` | Default `true`. |

A server's **Launcher executable path** (Paths section) can point at a custom Magnetar launcher. Blank uses the managed Magnetar.

### Launch arguments

Quasar always adds `-noconsole`, `-daemon`, `-path <DS app-data>`, `-config <Magnetar app-data>`, `-ds64 <DedicatedServer64>` and `-consent accept` or `-consent deny` (from the stored data handling consent), plus `-noimplicitmod` when **Disable implicit Magnetar mod load** is on. It removes these flags, `-console`, `-nosplash` and `-github-token` from what you type in **Launch arguments**. Tokens you can use: `{uniqueName}`, `{configPath}`, `{quasarBaseUrl}`, `{hostId}`, `{dsAppDataPath}`, `{magnetarAppDataPath}`, `{ds64Path}`, `{worldPath}`.

For a Magnetar build older than 2.3.3.0, Quasar sends the old `-consent`/`-noconsent` flags instead. See [Configuration](Configuration.md) for consent and the implicit Magnetar mod.

**Log launch environment** (Runtime section) writes the full launch command and environment to the Quasar log on the next start. The GitHub token is redacted, but other environment values may contain secrets.

## Steam Workshop mods

The config profile holds the mod list (see [Config profiles](ConfigProfiles.md#mods)). On every start Quasar writes that list, in order, into the world's `Sandbox_config.sbc`. The Dedicated Server then downloads the mods itself through Steam, into `content/244850/<item id>` under the server's DS app-data folder. Each server has its own Workshop content folder.

Quasar watches the server's console output for mod download failures. When it sees one, the server message says "Mod download failure reported by server output." and the expanded live data on the Dashboard lists up to 20 distinct failure lines. They clear on the next start or with Clear Error Status.

### Workshop cache repair

Before every start Quasar checks the server's Steam Workshop manifest `appworkshop_244850.acf` in the DS app-data folder:

- If the manifest lists items as installed but their content folder is missing or empty, Quasar removes only those entries so Steam downloads just the missing items. The Quasar log says "Repaired inconsistent Steam Workshop cache for server ...".
- If the manifest cannot be parsed, Quasar moves it and the `downloads` folder into `WorkshopCacheQuarantine/<timestamp>-<id>/` in the DS app-data folder, and Steam rebuilds the item state on launch. The log says "Quarantined invalid Steam Workshop manifest for server ...".

### Steam Web API key and SteamCMD account

On the **Configs** page, Mods tab, **Steam Workshop** panel:

- **Add API Key** / **Update API Key** opens **Steam Workshop API Key**. Get a key at `https://steamcommunity.com/dev/apikey`. Quasar needs it for Workshop search, **Popular Mods**, **Auto Sort Dependencies** and the automatic dependency check. Looking up mod names works without it.
- **Add SteamCMD account** / **Update SteamCMD account** opens **SteamCMD account** (Steam account name and password; **Remove account** clears it). Quasar uses this account only when its own SteamCMD downloads Workshop items and an item cannot be downloaded anonymously. Steam Guard may require an interactive SteamCMD login first. Standalone servers download mods through the DS and do not use this account.

Quasar stores both in `<quasar-root>/steam-workshop.json`, encrypted with the Data Protection keyring in `<quasar-root>/DataProtection-Keys`. On Linux the file is restricted to owner read/write (0600).

## Process priority and CPU affinity

In the editor's **Runtime** section:

- **Startup process priority** applies right after the process starts. **Ready process priority** applies once the agent reports the server is open. Values: Low, Below normal, Normal, Above normal, High. Default Normal for both.
- **CPU affinity** pins the server to logical cores, for example `0-7` or `0-7,16-23`. Empty means all cores. At least 2 cores. Quasar applies it at start and again when the server is ready, and picks up changes without a restart.

On Windows Quasar sets the process priority class (Low is Idle) and the processor affinity mask. On Linux it uses nice levels (Low 10, Below normal 5, Normal 0, Above normal -5, High -10) and `taskset` for affinity.

Raising priority above Normal on Linux needs privileges. Install the setuid helper `/usr/local/bin/quasar-renice`:

```bash
./install.sh --install-renice-helper --no-build --no-enable
```

Without it, Quasar calls `renice`, which fails for negative values as a normal user, and the card says "Install /usr/local/bin/quasar-renice to use High process priority on Linux." The helper only accepts Quasar's nice values and only renices Magnetar launcher processes owned by the caller.

## Servers survive Quasar restarts

Quasar launches Magnetar with `-daemon`, which detaches it from Quasar's session. Quasar saves each server's process ID in `supervisor-state.json`.

- **Preserve servers on shutdown** is on by default (`Quasar:PreserveManagedServersOnShutdown`, or `QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN`). When Quasar stops (service stop, `SIGTERM`, power menu, self-update), running servers keep running.
- When Quasar starts again, it adopts each still-running process by its saved ID ("Process adopted after Quasar worker turnover.") and gives the agent a fresh startup window to reconnect. A server whose process is gone becomes `Stopped` ("Previously running process not found during supervisor restore.") and starts again if its goal is On.
- With preserve set to `false`, Quasar stops every server gracefully (save, then stop, no kill timeout) before it exits. The Docker image sets `QUASAR_PRESERVE_SERVERS_ON_SHUTDOWN=false`.

While Quasar is away, Quasar.Agent keeps the server running and retries the connection every 10 seconds (plus up to 3 seconds of jitter). If Quasar stays unreachable for 3600 seconds after the agent had connected at least once, the agent saves the world and stops the server. Change this with `Quasar:AgentOfflineShutdownSeconds` or `QUASAR_AGENT_OFFLINE_SHUTDOWN_SECONDS` (0 or negative stops the server as soon as Quasar is gone). Quasar passes the value to each server at launch, so a change applies after the server's next start.

## Profiler mode

Quasar.Agent runs a built-in profiler in each server. Its mode is chosen per server on the **Analytics** page: **Simple, low overhead** (`SafeContinuous`, default), **Extensive, deep detail** (`DeepContinuous`) or **Off**. A change is saved in `server.json` (`AgentProfilerMode`) and applied live to a connected agent without a restart. Use Simple or Off if a game update breaks deep profiling. The global fallback is `Quasar:AgentProfilerMode` or `QUASAR_AGENT_PROFILER_MODE`. See [Monitoring](Monitoring.md).

## Common problems

- **Start button disabled or "still preparing"**: the managed runtime (SteamCMD, DS, Magnetar) is still downloading or failed. Check the Managed Runtime panel on the Dashboard and click **Retry** on a failed Dedicated Server row.
- **Server stuck in Starting, then restarts**: the agent did not connect in time. Check the Server log and Magnetar info log tabs. Big modded worlds may need a larger **Agent startup hard limit**. Check that the server can reach Quasar's web port at `QUASAR_BASE_URL`.
- **Server Faulted right after Start**: read the message on the card and match it to the table in [Launch failures](#launch-failures-that-fault-a-server).
- **Server restarts every 8 hours**: **Maximum uptime** defaults to `08:00`. Clear it in Restart Policy to disable.
- **Server keeps restarting with "Health policy"**: look at the health message. A low simulation rate or a stale heartbeat triggers it. Lower **Minimum average simulation rate**, or turn off **Auto-restart when unhealthy** while investigating.
- **Second server not reachable**: check that no server's game port equals another server's game port + 1000 or + 2000. Each server also uses those two derived ports.
- **Mods missing after start**: the world's mod list comes from the config profile on every start. Add the mods to the profile, or use **Merge Into <profile>** in the server editor.
- **High priority does nothing on Linux**: install `quasar-renice` (see above).
- **Server deleted but disk still full**: delete `<quasar-root>/Magnetars/<Server ID>/` yourself.

## Sources
- `Docs/StateMachines/DedicatedServerLifecycle.md`
- `Docs/StateMachines/ManagedRuntimeProvisioning.md`
- `Docs/StateMachines/AgentConnection.md`
- `Docs/StateMachines/ProfilerMode.md`
- `Docs/QuasarArchitecture.md`
- `Docs/Configuration.md`
- `Docs/LinuxDeploymentAndUpdates.md`
- `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
- `Quasar/Models/DedicatedServerDefinition.cs`
- `Quasar/Models/DedicatedServerPathResolver.cs`
- `Quasar/Models/DedicatedServerProcessPriority.cs`
- `Quasar/Models/DedicatedServerRestartInfo.cs`
- `Quasar/Models/ManagedServerRuntime.cs`
- `Quasar/Models/CpuAffinitySpec.cs`
- `Quasar/Services/DedicatedServerSupervisor.cs`
- `Quasar/Services/DedicatedServerRuntimePreparer.cs`
- `Quasar/Services/DedicatedServerCatalog.cs`
- `Quasar/Services/ManagedDedicatedServerRuntimeResolver.cs`
- `Quasar/Services/ManagedRuntimeOptions.cs`
- `Quasar/Services/ManagedRuntimeWarmupService.cs`
- `Quasar/Services/MagnetarLegacyLaunchCompatibility.cs`
- `Quasar/Services/ServerManagementActions.cs`
- `Quasar/Services/SteamWorkshopCacheRepairer.cs`
- `Quasar/Services/SteamWorkshopCredentialsCatalog.cs`
- `Quasar/Services/ClusterWorkshopModStager.cs`
- `Quasar/Services/WorldStartupCompatibilityGuard.cs`
- `Quasar/Services/WorldSandboxConfigEditor.cs`
- `Quasar/Services/WebServiceOptions.cs`
- `Quasar/Services/QuasarShutdownService.cs`
- `Quasar/Program.cs`
- `Quasar/Components/Pages/Home.razor`
- `Quasar/Components/Pages/Servers.razor`
- `Quasar/Components/Pages/ServerEditorDialog.razor`
- `Quasar/Components/Pages/ServerDeleteDialog.razor`
- `Quasar/Components/Pages/ServerConsoleDialog.razor`
- `Quasar/Components/Pages/Configs.razor`
- `Quasar/Components/Pages/Updates.razor`
- `Quasar/Components/Pages/Analytics.razor`
- `Quasar/Components/Pages/SteamWorkshopAccountDialog.razor`
- `Quasar/Components/Pages/SteamWorkshopApiKeyDialog.razor`
- `Quasar/Components/Dashboard/ServerCard.razor`
- `Quasar/Components/Dashboard/ServerDetailPanel.razor`
- `Quasar.Agent/StopCommand.cs`
- `Quasar.Agent/AgentConnection.cs`
- `Quasar.Agent/AgentOptions.cs`
- `Quasar.Agent/OfflineModeNetworkGuard.cs`
- `Dockerfile`
- `install.sh`
- `tools/quasar-renice.c`
