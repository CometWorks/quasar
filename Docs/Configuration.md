# Quasar Configuration

This document covers the runtime settings most operators need to change: the web
UI **listening host and port**, and the **browser auto-open** behavior on start.

Other settings (auth, updates, analytics, logging, managed runtime) live in the
same `appsettings.json` under the `Quasar` section; the update settings are
documented in [Windows](WindowsDeploymentAndUpdates.md) and
[Linux](LinuxDeploymentAndUpdates.md) deployment guides.

All .NET configuration keys can also use environment variables with `__` as the
section separator, such as `QUASAR__AUTH__ENABLED=false`. Quasar's shorter
`QUASAR_*` deployment overrides take precedence where documented. Docker users
should also see [Docker Deployment](Docker.md).

## Managed runtime startup check

When Quasar starts, it immediately checks the managed SteamCMD install and the
managed Space Engineers Dedicated Server install. Missing installs are downloaded
in the background and the Dashboard shows a Managed Runtime panel with live
status for both until both are ready. Managed Magnetar server launches are
blocked until those prerequisites are ready; on Linux this also prepares
SteamCMD's `linux64` native runtime directory so Quasar can pass it through
`LD_LIBRARY_PATH`. The Dedicated Server download is attempted up to three times
before it is marked failed; the Dashboard then shows a retry button on the
Dedicated Server row.

On Linux the managed SteamCMD runs with its own `HOME` under
`{Quasar data}/ManagedRuntime/Tools/SteamCmdHome` (override with
`QUASAR_STEAMCMD_HOME_DIR` or `Quasar:ManagedRuntime:SteamCmdHomeDirectory`), so it
never reads or rewrites the desktop Steam client's configuration under `~/.steam`.

## Magnetar data handling consent

Magnetar's anonymous plugin-usage statistics are opt-in. Quasar stores the
operator's decision in `data-handling-consent.json` under the Quasar data
directory and passes that decision to every managed Magnetar start:

- `YES` -> Quasar appends `-consent accept`
- `NO` -> Quasar appends `-consent deny`
- no stored decision -> Quasar appends `-consent deny`

Any `-consent`, `-noconsent`, or `-withdraw-consent` flag typed into a server's
launch arguments is removed before start; only the stored decision reaches
Magnetar.

Magnetar builds older than 2.3.3.0 only understand the bare `-consent` /
`-noconsent` flags, so Quasar sends those when it detects such a build (by the
launcher name on Linux, by the executable version on Windows). This fallback is
temporary and will be removed in the first Quasar release of 2027.

The same detection decides how the core compatibility plugins reach Magnetar.
Magnetar force-loads `dotnet-compat` (plus `linux-compat` on Linux) by id from
any configured source; they never appear in the profile. For 2.3.3.0 and later
Quasar always writes the MagnetarHub `RemoteHub` source into the server's
`sources.xml`, the same as a standalone Magnetar, because Pulsar keys per-file
`RemotePlugin` sources by repository and two hub manifests would collapse into
one. Older builds ask for the `se-` prefixed ids and still get per-file sources
pointing at the `*LegacyId.xml` manifests.

The Dashboard shows a top-of-page YES/NO consent prompt until a decision is
stored. The same decision can be changed later from **Settings -> Security**.
Changes apply to the next server start or restart; running servers keep their
current Magnetar consent state.

Magnetar sends only the enabled plugin IDs plus a random local instance ID when
consent is granted. It does not send a Steam ID, account, world, or server
content.

## Implicit Magnetar mod

Each server definition defaults **Disable implicit Magnetar mod load** to off.
With the default setting, Quasar omits Magnetar's `-noimplicitmod` launch flag
so Magnetar loads `MagnetarMod` normally.

Turn this on only from the server editor's **Runtime** section. The UI asks for
confirmation because enabling it passes `-noimplicitmod` on the next server
start, which disables `MagnetarMod` and breaks the mission screen popup used by
server-side plugins. Magnetar already does this automatically when cross-play is
enabled. Turning it back off removes the flag from future starts.

The server editor does not report `MagnetarMod` as missing from the selected
config profile while Magnetar's implicit mod loading is active. It still warns
when implicit loading is disabled explicitly or by cross-play.

## Server storage paths

Server path fields under **Edit Server -> Paths** use three forms:

- blank: Quasar's managed per-server location under the current install root
- relative: an override resolved from the current Quasar install root
- absolute: an external location that does not move with Quasar

Managed defaults stay blank in `server.json`; Quasar resolves them only when it
accesses the filesystem. Paths inside the Quasar root are stored with portable
forward-slash separators. Changing the DS app-data path immediately refreshes
the world-save list when the saves path is blank and therefore derived from the
DS location.

**Use Managed Defaults** clears the DS, Magnetar, saves, and rendered-config
overrides. It changes only the pending server definition; it does not copy or
delete files. Copy the authoritative server data into the current Quasar root
before resetting paths, especially when both the old and new roots still exist.

## Server and world names

The server editor's **Identity** section keeps Quasar's display name separate
from the names advertised by Space Engineers. **In-game server name** controls
the server-list title. **In-game world name** controls the world name shown in
the server browser. Blank values fall back to the Quasar display name and server
identifier respectively.

Quasar writes the selected world name into both the generated Dedicated Server
configuration and the selected save's `Sandbox_config.sbc` before each start.
The latter is required for existing saves because Space Engineers loads and
advertises that file's `SessionName`. Name changes therefore take effect on the
next server start or restart.

## Online mode defaults and Offline safety

New config profiles default **Online Mode** to **Public**. Profiles created from
a world template also use Public rather than importing the template world's
saved Online Mode.

Offline mode is unsafe on an ordinary Space Engineers dedicated server because
every connected user receives Owner-level permissions. Quasar shows a warning
whenever a profile is set to Offline. At launch it overrides that server's
rendered listen IP to `127.0.0.1`, and Quasar.Agent accepts only direct peers
whose reported address is loopback. Relay peers and network transports that do
not expose a verifiable loopback address are rejected. This restriction applies
only while Online Mode is Offline; Public, Friends, and Private retain the
server definition's configured Listen IP.

`0.0.0.0` means “listen on every local interface”; it is not a valid remote
client address and is not treated as localhost by the join guard.

## Steam Workshop mod dependencies

When a config profile with Workshop mods is opened, saved, or receives imported
mods, Quasar checks declared Steam Workshop child/dependency metadata, adds
missing dependency mods, and marks dependency rows in the profile JSON and the
world's generated `Sandbox_config.sbc`. It does not reorder the profile
automatically during this check; the Mods tab provides an **Auto Sort
Dependencies** action that applies a topological dependency order when the
operator wants it. If Quasar finds a dependency listed after its dependent, or
Steam reports a circular dependency chain that prevents a clean topological
order, the UI shows a warning. The Steam Workshop API key configured from the
Mods tab is required for this automatic dependency check. If the key is missing
or Steam cannot be reached, Quasar keeps the current mod list and shows a
warning instead of blocking the save.

Opening the Mods tab also refreshes each selected mod's display name from Steam
Workshop. This public lookup does not require the Workshop API key and preserves
Workshop IDs, load order, and dependency flags. Refreshed names remain pending
editor changes until the profile is saved; a failed lookup leaves existing names
unchanged.

Space Engineers Dedicated Server also has its own **Autodetect Dependencies**
setting. Quasar manages that setting in the profile instead of showing it as a
manual world option: it is disabled after a clean dependency check or a
successful auto-sort, so DS receives the exact generated `Sandbox_config.sbc`
mod list and load order. Quasar enables it as a fallback when the mod list is
manually changed, the Workshop API key is missing, dependency checks fail, or
dependency warnings remain after sorting. Auto-sort reorder notices do not keep
the fallback enabled when the final sorted order is otherwise valid.

After a dependency check or auto-sort, the Mods tab also shows a collapsed,
flattened dependency outline. Rows are tagged as root mods, dependency mods,
already-listed repeats, or circular references so operators can inspect why a
dependency warning was raised without changing the saved mod list.

## Dedicated Server log retention

Each server has a **Space Engineers DS logs to keep** setting in the server
editor's **Runtime** section. It defaults to `5`.

Quasar prunes `SpaceEngineersDedicated*.log` files from that server's Dedicated
Server app-data directory on server start and stop, keeping the newest files and
deleting older ones. Magnetar diagnostics are written in the server's Magnetar
app-data directory as timestamped `info_*.log` files, with `info.current`
pointing at the active file. PluginSdk stdout sink lines captured by
Quasar.Agent are also appended to that active Magnetar log as normal text log
lines for the specific instance.

Before a connected instance shuts down for a Quasar health-policy restart,
Quasar.Agent writes the restart reason through the same PluginSdk path. This
writes the reason to the active Magnetar `info_*.log` and queues it for Quasar's
Recent plugin logs. When the health failure itself prevents agent communication,
only Quasar's own log and persisted Dashboard restart status can record the
reason; Quasar emits an explicit warning for that delivery limitation.

The server console dialog can view **Most recent** or a specific older DS /
Magnetar log file. Auto-refresh and the Refresh button are active only for
**Most recent**; selecting an older file keeps that snapshot fixed for review.

## Agent startup timeouts

Each server has two agent-startup limits under **Edit Server -> Runtime**:

- **Agent startup log inactivity timeout** (`AgentStartupGraceSeconds`) defaults
  to `60`. While Quasar is waiting for Quasar.Agent to attach and send its first
  telemetry snapshot, a newer write to a `SpaceEngineersDedicated*.log` or
  Magnetar `info*.log` file restarts this soft inactivity timer.
- **Agent startup hard limit** (`AgentStartupHardLimitSeconds`) defaults to
  `600`. It is measured from process launch, or from adoption when a replacement
  Quasar worker discovers an existing process, and log activity never extends it.

Log files last written before the startup/adoption watch began do not count as
progress. Reaching either limit makes the server unhealthy and follows the
existing agent-attach retry and automatic-recovery policy.

Legacy server definitions that still contain the former default
`AgentStartupGraceSeconds = 180` and do not contain the hard-limit field are
normalized to `60` and `600` when loaded. This intentionally overrides that old
persisted default. Definitions that already contain a hard limit keep an
operator-configured `180`-second inactivity timeout.

## Where configuration is read from

Both the **Bootstrap launcher** (`Quasar`/`Quasar.exe`) and the replaceable **web
worker** read JSON config from the Quasar install root. Auto-updates preserve
`appsettings.json` during Bootstrap self-updates, and UI-worker activation
updates it from the staged, resolved `appsettings.json` so Bootstrap and the
managed worker keep the same base settings. Set `QUASAR_INSTALL_DIR`, or create
the ignored `.quasar-install-dir` file used by the development launch profile,
for direct worker/dev runs that need to target a deployed Quasar root.

The shipped defaults are defined in [`Quasar/appsettings.json`](../Quasar/appsettings.json).

During UI-worker staging, Quasar performs a three-way merge for `appsettings.json`:
the previous release base stored under the install root is the merge base, the
current install-root `appsettings.json` supplies local values, and the new
release file supplies new defaults. Clean local changes are carried into the
staged version automatically. If both the local file and the release changed the
same setting differently, the Updates page shows the current and incoming files
side-by-side with an editable final file below them. Use **Take current** or
**Take incoming** as a starting point, make any needed edits, then choose **Save
resolution** before activation. The take actions only copy into the final
editor; saving remains a separate action.

## Per-server launch diagnostics

To capture the exact Magnetar launch command and environment, edit the server in
the web UI, open **Runtime**, and enable **Log launch environment**. The setting is
saved on that server definition (`server.json`) and is applied on the next start
of that server only.

The diagnostic entry is written to the normal Quasar logs at warning level and
includes the executable path, arguments, working directory, and environment
variables such as `LD_LIBRARY_PATH`. Use it only while troubleshooting because
environment variables can contain secrets.

## Backup storage folder

Stored Quasar, server, and world backups are written to `Quasar:BackupDirectory`.
Change it from **Backup → Stored backups**, or edit `appsettings.json` directly.
Quasar config backups contain Quasar-managed configuration/catalog files only;
server backups contain one server definition plus non-cache Dedicated Server and
Magnetar app data; world backups contain world save files. Restored server
definitions are written with `Off` goal state so they do not auto-start before
matching world files are restored.
Leave it empty to use the default `Backups` folder under the Quasar data
directory. Set it to an absolute path to place backups on another disk or a
mounted network share:

```json
{
  "Quasar": {
    "BackupDirectory": "/mnt/quasar-backups"
  }
}
```

Relative paths are resolved under the Quasar install directory. If the folder is on
a network share, make sure it is mounted before Quasar starts and that the
Quasar service account can create, list, read, and delete files in it. Changes
from the Backup page apply to new stored-backup operations immediately; direct
file edits need a Quasar restart. Existing backup ZIPs are not moved
automatically; move them manually if they should appear in the new folder. When
`QUASAR_BACKUP_DIR` is set, it takes precedence and the Backup page shows the
active folder as read-only.

## Agent cluster mode

`Quasar.Agent` enters cluster mode when `CLUSTER_GATEWAY_REGISTRY` is non-empty,
the same activation condition used by the cluster release. Legacy `SE_CLUSTER_*`
variables are not supported. The host executor also supplies
`CLUSTER_ID`, `CLUSTER_NODE_ID`, and `CLUSTER_NODE_ROLE`; the agent includes
those values in hello and snapshot telemetry.

Cluster mode changes lifecycle safety, not the telemetry transport: the agent keeps
reconnecting when Quasar is unavailable, never performs the standalone offline
save-and-stop policy, does not register its standalone lifecycle chat commands, and
rejects save/stop commands received over the agent WebSocket. Cluster lifecycle
requests must use Magnetar's PluginSdk route to the Gateway, while OS/executor process
termination remains outside the agent.

## Agent profiler mode

Managed Space Engineers servers receive the profiler mode through
`QUASAR_AGENT_PROFILER_MODE`. For managed servers this comes from the server's
saved `AgentProfilerMode`; the global `Quasar:AgentProfilerMode` is only a
fallback for servers that do not have a per-server value yet.

Default:

```json
{
  "Quasar": {
    "AgentProfilerMode": "SafeContinuous"
  }
}
```

Supported values:

- `SafeContinuous` - default; shown as "Simple, low overhead" in Analytics.
  Continuous low-overhead Harmony timing for named high-level server paths,
  without deep IL call-site transpilers or broad entity update patching.
- `DeepContinuous` - shown as "Extensive, deep detail" in Analytics.
  Continuous profiler with Harmony IL call-site wrapping for session components,
  entity update dispatch, physics internals, replication/network paths, scripts,
  and game-loop timing. Detailed samples appear in the Profiler: Top Grids and
  Profiler: Entity Types panels when the deep patch groups produce data.
- `Off` - disables Quasar profiler patches and profiler snapshots.

The Analytics page exposes this per server/agent. Changing it there saves the
server definition and sends a live command to the connected agent when present.
Use `SafeContinuous` or `Off` if a Space Engineers update changes IL shapes and a
deep patch becomes suspect. Deep patch groups log failures and continue with the
remaining profiler surface; entity call-site misses fall back to high-level
timing only.

## Discord chat privacy and slash commands

Quasar keeps Space Engineers chat channels separate when relaying them to Discord:

- global game chat goes only to the server's **Chat relay channel ID**
- whispers go only to the server's **Admin whisper channel ID**
- faction chat is dropped unless that faction has a channel binding created with
  `/faction-channel`
- scripted, chatbot, broadcast-controller, and unknown chat types never fall
  through to the global relay

The admin whisper channel and generated faction channels must deny **View Channel**
to the guild's Everyone role. Quasar checks this before sending private traffic and
also rejects any non-administrator role or non-bot user overwrite that explicitly
allows viewing. A bad or public binding is therefore logged and the message is
dropped instead of leaked.

Invite the bot with both the `bot` and `applications.commands` OAuth scopes. It needs
View Channel, Send Messages, Read Message History, Attach Files, and Embed Links in
relay channels. It also needs Manage Channels to create faction channels.

Available guild slash commands:

- `/whisper server:<unique-name> user:<online name or Steam ID> message:<text>` sends
  an ephemeral-confirmed private message to an online game player. Run it from a
  command, global relay, admin, or faction channel bound to that server.
- `/faction-channel server:<unique-name> faction:<tag>` requires Discord
  Administrator permission. It creates a text channel in the invoking channel's
  category, denies View Channel to Everyone, explicitly grants the bot its relay
  permissions, and saves the faction/channel binding in `discord-options.json`.
  Discord administrators can see the channel because Discord's Administrator
  permission bypasses channel overwrites. Running the command again reapplies the
  private permission overwrites to the existing bound channel.

Messages posted by Discord administrators in a bound faction channel are delivered
to every online member of that in-game faction as server-authored private chat,
labeled `Discord [TAG]`. There is no global fallback. The dedicated server's normal
faction-send path requires its sender to be a faction member, so Quasar uses the
server's supported per-player private delivery rather than impersonating a player.

Slash commands are guild-scoped and refresh when the bot connects. Server values
use Quasar's stable unique names, not display names.

## Discord player and server notifications

The Discord page includes two independent per-server switches, both enabled by
default: **Enable player connection notifications** and **Enable server lifecycle
notifications**. Set **Status channel ID** to choose their destination; when empty,
Quasar uses **Chat relay channel ID**. Without either channel, no notifications are
sent. These switches work independently of **Enable chat relay**.

Messages include the server's unique name so several servers can share a channel:

- `[survival] 🚀 **artur** connected to the server!`
- `[survival] ☄️ **artur** disconnected from server!`
- `[survival] ✅ Server Started!`
- `[survival] 🔄 Server is going to Restart!`
- `[survival] ❌ Server Closed!`

Start notifications follow the supervisor's transition to Running, once the agent
reports the game running. Restart notifications follow each new pending supervisor
restart request, including manual, scheduled, policy, and in-game requests. Closure
notifications follow a transition to Stopped, Crashed, or Faulted. A restart can
produce a closure message if the supervisor observes the stopped/crashed process.
An agent transport disconnect alone does not mean the server closed.

Player changes are detected between consecutive connected, running agent snapshots.
The first snapshot after bot startup, reconnect, or server startup establishes a
baseline without announcing existing players. Agent reconnection and shutdown do
not generate a wave of player disconnect/join messages. Connections that begin and
end between snapshots cannot be reported. Notifications missed while the bot is
offline are not replayed. Player names are escaped and notifications cannot ping
Discord users or roles.

These settings are stored in `discord-options.json` as `statusChannelId`,
`enablePlayerNotifications`, and `enableServerNotifications`. Existing configurations
default to both notification types enabled, using their existing chat relay channel.

## Discord simspeed alerts

The Discord page stores per-server alert rules in `discord-options.json`.
Baseline rules are enabled for each server:

- sharp drop: previous simspeed at least `0.980`, current simspeed at most
  `0.800`, and drop delta at least `0.150`; cooldown `120` seconds
- sustained loss: average simspeed at most `0.900` over `60` seconds; cooldown
  `300` seconds

Each server can override the alert channel, enable/disable either rule, and tune
the thresholds, windows, and cooldowns. If the simspeed alert channel is empty,
Quasar uses that server's analytics channel.

## Web UI host and port

The browser connects to the web UI on the host and port configured here. Defaults:

```json
{
  "Quasar": {
    "Host": "0.0.0.0",
    "Port": 8080
  }
}
```

- `Host` — the interface Kestrel binds to. `0.0.0.0` (the default) listens on all
  interfaces so the UI is reachable from other machines on the network; use
  `127.0.0.1` to restrict it to the local machine only.
- `Port` — the TCP port the UI listens on. Default `8080`.

When `Host` is `0.0.0.0` (or `*`/`+`/`[::]`), the URL printed at startup and used
for health checks advertises `127.0.0.1` instead, since `0.0.0.0` is not a
connectable address.

### How to change the port

Edit `Quasar:Port` (and optionally `Quasar:Host`) in `appsettings.json`, then
restart Quasar:

```json
{
  "Quasar": {
    "Host": "0.0.0.0",
    "Port": 9000
  }
}
```

Restart the deployment:

- Windows (installed Scheduled Task): `Stop-ScheduledTask -TaskName Quasar; Start-ScheduledTask -TaskName Quasar`
- Linux (systemd): `sudo systemctl restart quasar.service`
- Foreground run: stop the process (Ctrl+C) and start it again.

**Edit `appsettings.json`, not an environment variable.** The launcher and worker
must agree on the port — the launcher starts the worker and then health-checks it
on the configured port. `appsettings.json` is read by both, so they stay in sync.
The `QUASAR_WEB_PORT` / `QUASAR_WEB_HOST` environment variables and `ASPNETCORE_URLS`
are honored only by the worker, not by the Bootstrap launcher, so using them in a
supervised install desynchronizes the two and the launcher will report the worker
as unhealthy. They are fine only when running the worker directly (e.g.
`dotnet run --project Quasar/Quasar.csproj`) without Bootstrap.

> **Port 8080 and Space Engineers:** `8080` is also the Space Engineers Dedicated
> Server **Remote API** default port. Quasar assigns each managed server a derived,
> non-default Remote API port (`ServerPort + 2000`), so managed servers do not
> collide with the UI by default. If you run other software on `8080`, or point a
> server's Remote API at `8080` manually, pick a different `Quasar:Port`.

## Outbound proxies

Bootstrap's local worker discovery, startup health checks, and graceful-drain
requests bypass HTTP proxies explicitly. Corporate proxy settings therefore
cannot redirect these control requests and cause Bootstrap to kill a healthy
worker when its 60-second startup health-check window expires.

Bootstrap's GitHub release queries, checksum downloads, and worker/bootstrap
archive downloads continue to use the configured outbound proxy. Disabling proxy
use for local control requests does not change proxy settings for downloads.

## Reverse proxy auth

Quasar can grant a trusted-network session to loopback or explicitly enabled
same-subnet clients. Same-subnet bypass is disabled by default.
When Quasar sits behind NGINX Proxy Manager, Caddy, Traefik, or another reverse
proxy, the TCP peer seen by Quasar is the proxy, not the browser. Without proxy
handling this can make every proxied browser look like a local or LAN client.

These settings can be changed in **Settings → Security**. The page includes a
public reverse-proxy preset and a step-by-step exposure checklist. It writes the
same data-directory `appsettings.json` values shown below.

Quasar now accepts `X-Forwarded-For`, `X-Forwarded-Proto`, and
`X-Forwarded-Host` only from trusted proxies:

- loopback proxies (`127.0.0.1` and `::1`) are trusted by default
- additional proxy IP addresses or CIDR ranges must be listed in
  `Quasar:Auth:TrustedNetworkBypass:TrustedProxies`
- if a request has forwarding headers but they were not accepted from a trusted
  proxy, trusted-network bypass is refused and the user must sign in

For NGINX Proxy Manager running on the same host, no proxy entry is usually
needed because loopback is trusted. For a Docker bridge or a separate reverse
proxy host, add the proxy container/host address or bridge CIDR:

```json
{
  "Quasar": {
    "Auth": {
      "TrustedNetworkBypass": {
        "AllowLoopback": true,
        "AllowSameSubnet": false,
        "TrustedProxies": [ "172.18.0.0/16" ],
        "Roles": [ "admin" ]
      }
    }
  }
}
```

Keep same-subnet bypass disabled whenever browser access should be tied to
Steam/RBAC identity:

```json
{
  "Quasar": {
    "Auth": {
      "TrustedNetworkBypass": {
        "AllowLoopback": true,
        "AllowSameSubnet": false,
        "TrustedProxies": [ "172.18.0.0/16" ]
      }
    }
  }
}
```

Keep Quasar's port private to the proxy when exposing it to the internet. Do not
trust broad networks unless every host in that range is under your control.

### RBAC enforcement

Steam users receive roles from the runtime `rbac.json` catalog. Quasar supports
three roles: `viewer`, `editor`, and `admin`. Viewer access is read-only: it can
open the dashboard, analytics, host status, and protected read APIs, but cannot
open configuration, world, plugin, player-control, chat-command, entity-control,
Discord, appearance, cluster-management, backup, update, or security editors.

Role changes take effect immediately. Quasar re-evaluates cookie roles on each
HTTP request, reloads active Blazor sessions when `rbac.json` changes, and checks
the current role again at sensitive dashboard and security actions. Runtime RBAC
saves that remove the last `admin` mapping are rejected to prevent accidental
lockout. Direct filesystem edits remain an operator-controlled recovery path.

On first startup, `QUASAR_ADMIN_STEAM_ID` can seed the initial Steam administrator.
Quasar accepts only a 17-digit SteamID64 and writes the mapping to `rbac.json`
only when that file is absent. Existing RBAC state is never replaced by the
environment value. This is primarily intended for container manifests and other
unattended provisioning.

Default policy grants are:

| Policy | Roles |
| --- | --- |
| `CanView` | viewer, editor, admin |
| `CanEditConfigs` | editor, admin |
| `CanEditServers` | editor, admin |
| `CanControlServers` | editor, admin |
| `CanManageDiscord` | editor, admin |
| `CanManageAppearance` | editor, admin |
| `CanManageSecurity` | admin |
| `CanShutdownQuasar` | admin |

Trusted-network access uses the roles configured under
`Quasar:Auth:TrustedNetworkBypass:Roles`; it defaults to `admin`. Treat every
trusted address as a full operator unless that role list is reduced explicitly.

### Development port

Running the worker directly with `dotnet run --project Quasar/Quasar.csproj` uses
[`Quasar/Properties/launchSettings.json`](../Quasar/Properties/launchSettings.json)
only for development environment variables. It does not set `applicationUrl`, so
the worker still reads `Quasar:Host` / `Quasar:Port` from normal configuration.
Set those values in `QUASAR_INSTALL_DIR/appsettings.json`, or in the root named
by `.quasar-install-dir`, when running the worker from Rider against an
installed Quasar root. Use `QUASAR_WEB_HOST` / `QUASAR_WEB_PORT` for a one-off
direct-worker override.

## Browser auto-open on start

Quasar **prints the UI URL on startup** so it can be clicked to open in a browser;
the deployed app does **not** open a browser automatically.

- The installed **Windows Scheduled Task** and **Linux systemd** services run in
  service mode with `QUASAR_OPEN_BROWSER_ON_START=false` — they never open a
  browser, and they run quietly in the background.
- A foreground launcher run (e.g. `Quasar ensure-running`) prints the URL and
  does not open a browser.

The URL is always printed regardless of the auto-open setting. To restrict the web
UI to the local machine, set `Quasar:Host` to `127.0.0.1` as described above.

### Auto-open setting (interactive use)

`Quasar:OpenBrowserOnStart` (env `QUASAR_OPEN_BROWSER_ON_START`, default `true`)
controls whether an **interactive console** worker auto-opens the browser. It has
no effect in service mode (services force it off). The developer launch profile
(`Quasar.Bootstrap/Properties/launchSettings.json`) still requests auto-open via
`ensure-running --open-browser` for convenience when running from an IDE.

## API-only headless mode

Use `Quasar serve --headless` for dark-factory and other unattended automation.
The launcher propagates the mode across worker launches and self-updates. The same
mode can be configured with `Quasar:Headless=true` or `QUASAR_HEADLESS=true`.

Headless mode runs the normal supervisor, catalogs, background jobs, HTTP APIs,
and `/ws/agent`, but does not load Razor components, UI plugins, branding assets,
or static web assets. It does not open a browser. Existing configuration profiles,
world templates, server definitions, and data paths are unchanged, so switching
between UI and headless operation does not convert or duplicate state.
Enabled UI-plugin package manifests are still read as data so their owned server
companions remain deployable; their UI assemblies are not loaded.

- `GET /api/health` is process liveness and reports `headless`.
- `GET /api/ready` confirms that the API worker, durable catalogs, and cluster
  operation store are ready.
- `GET /` returns a small JSON discovery document instead of the UI.

Authentication and authorization remain enabled exactly as configured; headless
mode is not an authentication bypass.

## Cluster query catalog

Quasar discovers clusters from `<quasar-root>/Clusters/<unique-name>/cluster.json`.
Set `Quasar:ClusterCatalogPath` to override that directory for development or an
external configuration deployment. A definition contains management metadata;
Gateway credentials stay outside the file and are read from the named environment
variable at query time.

```json
{
  "uniqueName": "production",
  "displayName": "Production cluster",
  "gatewayUrl": "https://cluster-gateway.internal:8443",
  "gatewayAdminTokenEnvironmentVariable": "PRODUCTION_GATEWAY_ADMIN_TOKEN",
  "hostCommandUrl": "http://127.0.0.1:28400",
  "hostCommandTokenEnvironmentVariable": "PRODUCTION_HOST_COMMAND_TOKEN",
  "configProfileId": "survival",
  "worldTemplateId": "main-world",
  "goalState": "Off",
  "shutdownGracePeriodSeconds": 60,
  "gateway": {
    "clusterId": "production",
    "goal": "On",
    "bundleManifestPath": "/srv/quasar/bundles/cluster/manifest.json",
    "bundleManifestSha256": "<64 lowercase hex characters>",
    "configRevision": "production-r1",
    "ports": [ 27016, 28016 ],
    "runRoot": "/srv/quasar/clusters/production/gateway"
  }
}
```

`gateway.goal` is stored as `On` because it is the launch template; the cluster
`goalState` is authoritative. The reconciler overrides the Host goal as it converges.

The Phase 4 API uses Gateway admin contract version 1 and is available in normal
and headless operation:

- `GET /api/v1/clusters`
- `GET /api/v1/clusters/{uniqueName}/health`
- `GET /api/v1/clusters/{uniqueName}/status`
- `GET /api/v1/clusters/{uniqueName}/plan`
- `GET /api/v1/clusters/{uniqueName}/recovery-readiness`
- `GET /api/v1/clusters/{uniqueName}/config`
- `PUT /api/v1/clusters/{uniqueName}/config`
- `GET /api/v1/clusters/{uniqueName}/lifecycle`
- `PUT /api/v1/clusters/{uniqueName}/goal`
- `PUT /api/v1/clusters/{uniqueName}/gateway-spec`
- `POST /api/v1/clusters/{uniqueName}/gateway/restart`
- `GET /api/v1/clusters/{uniqueName}/host`
- `PUT /api/v1/clusters/{uniqueName}/host/attachment`
- `PUT /api/v1/clusters/{uniqueName}/host/gateway`
- `GET /api/v1/clusters/{uniqueName}/operations/{operationId}`

Responses preserve the Gateway envelope, capture time, string enum values, and
stable error codes. When Quasar authentication is enabled, these routes require
a human `CanView` role or the scoped service-principal permission described below.
`/api/health` and `/api/ready` include
`configuredClusters`; they do not contact Gateway and remain usable if a Gateway
is down. The readiness payload also reports the durable operation store. Recovery
readiness is calculated by Gateway from its durable Registry,
Save Catalog, snapshot, WAL, and artifact-holder records; Quasar does not infer it
from node liveness.

Configured clusters appear beside standalone servers in both dashboard views. Their
detail page reports Gateway, World Authority, node capacity, and reconstructibility
from the query contracts above. The **Tools → Hosts** page shows Host executor
reachability and persisted attachments. Cluster rows keep the familiar start/stop
controls; stop changes the durable cluster goal and the reconciler performs Gateway
graceful shutdown, verifies `Down` plus the clean marker, then asks Host to stop the
exact Gateway process. Clean-Down evidence is persisted in `cluster.json` as
`shutdownProof` before teardown and bound to the exact lifecycle identity. It survives
Quasar restart and a lost Host response. Repeating the same goal preserves that identity;
changing the goal or Gateway spec clears the proof. Candidate package staging does not.
Do not hand-edit this evidence. An absent Gateway without matching evidence reports
`shutdown_unverified`; a mismatched or uncertain process reports
`gateway_recovery_required`. An Off goal never starts a Gateway for recovery.
The reconciler is a hosted service in normal and headless mode.

Every mutation requires an `Idempotency-Key` header. Quasar writes the operation
under `<quasar-root>/Operations/Clusters` before calling Gateway, returns `202` with
the durable operation ID, and exposes the completed result or structured failure at
the operation route. Replaying the same key and request returns the same operation;
reusing a key with different content returns `idempotency_key_conflict`.

### Packaged cluster CLI

The packaged `Quasar` launcher is also the thin dark-factory API client. It writes
one compact JSON envelope to stdout and diagnostics to stderr. `--url` overrides
`QUASAR_URL`; without either, the CLI uses the local Quasar discovery manifest.
Bearer tokens are read only from the environment (default `QUASAR_API_TOKEN`) so
they do not appear in process arguments or shell history.

```bash
export QUASAR_API_TOKEN='<service-principal token>'

./Quasar cluster list --url https://quasar.internal
./Quasar cluster lifecycle production --url https://quasar.internal
./Quasar cluster goal production off --url https://quasar.internal \
  --idempotency-key deploy-2026-08-09-stop --wait --wait-timeout 1800
./Quasar cluster operation production <operation-id> --url https://quasar.internal --wait
./Quasar cluster gateway-restart production --url https://quasar.internal \
  --idempotency-key deploy-2026-08-09-gateway --wait
```

Read commands are `list`, `health`, `status`, `lifecycle`, `plan`,
`recovery-readiness`, `config`, and `operation`. Mutations require a caller-owned
idempotency key. `--wait` polls the durable operation route and is safe to repeat
after either caller or Quasar restarts. Request timeout defaults to 30 seconds and
is controlled by `--timeout`; operation wait timeout defaults to 900 seconds.

Exit codes are stable: `0` success/accepted, `2` usage, `3` local configuration,
`4` connection or timeout, `5` authentication/authorization, `6` API rejection,
`7` completed operation failure, and `8` incompatible or invalid protocol JSON.

### Query-only service principals

Dark-factory callers can use a scoped bearer credential without a browser, cookie,
or device-login flow. Store only the environment-variable name in configuration:

```json
{
  "Quasar": {
    "Auth": {
      "ServicePrincipals": [
        {
          "Name": "factory-reader",
          "TokenEnvironmentVariable": "QUASAR_FACTORY_READER_TOKEN",
          "Scopes": [ "cluster.query" ],
          "Clusters": [ "production" ]
        }
      ]
    }
  }
}
```

Set `QUASAR_FACTORY_READER_TOKEN` to a random value of at least 32 characters in
the Quasar service environment. Use `Clusters: [ "*" ]` only when the principal
must query every configured cluster. The `cluster.query` scope can call only the
versioned cluster query and operation-status routes; it grants no server logs or
mutation permission. Use `cluster.manage` for an automation principal that may also
change cluster policy; both scopes remain limited by the principal's `Clusters`
allow-list. Invalid credentials and forbidden cluster access return
versioned JSON errors instead of redirects. Identical token values assigned to
multiple principals fail closed.

## Host executor attachments

Current Gateway node execution is gated by `executor_contract_unavailable` until
the public versioned reporting contract is available. The attachment and local
actualizer described here are retained for integration development.

`Quasar.Host` is packaged under the release's `Host` directory. Its persisted
attachment file contains stable executor/host IDs, Gateway URLs, and credential
environment-variable names; raw executor tokens remain in the host service
environment. One process may attach to multiple clusters:

```json
{
  "executorId": "executor-a",
  "hostId": "host-a",
  "pollIntervalSeconds": 2,
  "stateDirectory": "/var/lib/quasar-host",
  "command": {
    "url": "http://127.0.0.1:28400",
    "tokenEnvironmentVariable": "PRODUCTION_HOST_COMMAND_TOKEN"
  },
  "attachments": [
    {
      "clusterId": "production",
      "gatewayUrl": "https://gateway.internal:8443",
      "tokenEnvironmentVariable": "PRODUCTION_EXECUTOR_TOKEN",
      "bundleManifestPath": "/srv/quasar-clusters/production/manifest.json",
      "bundleManifestSha256": "0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef",
      "runRoot": "/var/lib/quasar-clusters/production"
    }
  ]
}
```

Run `Quasar.Host run --config host.json`; `--once` performs one deterministic
plan read for deployment checks and then reports `executor_contract_unavailable`.
No heartbeat or node actualization occurs, including for attachments without bundle
configuration.

The optional `command` listener is the Quasar-to-Host control seam. It accepts only an
HTTP loopback origin and bearer authentication from the named environment variable; the
raw token is never stored in either JSON file. Quasar uses it for Host status and durable,
idempotent attachment updates. Operators can inspect the same contract directly with
`Quasar.Host status --url URL --token-env ENV` or apply an attachment file with
`Quasar.Host attachment apply --url URL --token-env ENV --file FILE`. Apply the designated
Gateway's desired state with
`Quasar.Host gateway apply --url URL --token-env ENV --file gateway.json` or the durable
Quasar API route above. The versioned `GatewaySpec` binds the cluster and On/Off goal to an
immutable bundle-manifest hash, config revision, reserved ports, and provenance-marked run root.
Host persists the level-triggered spec before reconciling it, re-adopts an exact matching process
after restart, and refuses to stop or replace a process whose recorded identity does not match.
The Quasar API accepts `goal: Off` only after Gateway reports phase `Down` and a clean-shutdown
marker; emergency recovery uses a separate audited path rather than weakening this gate.
The marker must not predate the current shutdown start. Host also refuses an Off spec
that differs from its running deployment, an executable hash mismatch, or a launch
whose process identity was never committed. Direct Host actions through Quasar clear
previous shutdown proof. Calling Host directly is an operator action outside Quasar's
lifecycle gate and requires independently establishing shutdown safety.
Host must advertise `gatewayStopFencing: true` before Quasar initiates shutdown.
Quasar binds teardown to the PID and launch timestamp observed before checking Gateway
clean Down. Host echoes that identity as `completedStopFence` only after confirming
the process is gone. A replacement process is never stopped by the earlier request.
For direct Host CLI/API Off requests, include `stopFence` with `processId` and
`launchedAt` from Host status; a live process cannot be stopped without a matching
fence. Old Hosts are refused with `gateway_stop_fencing_unavailable`.

The retained, currently disabled node actualizer verifies the pinned manifest SHA-256 and every file hash,
then uses its per-slot immutable spawn specifications. Each specification declares the
slot, node ID, role, bundle-relative executable, arguments/environment, reserved ports,
and ready timeout. Host writes the launch intent before starting the process and records
PID, process start time, executable identity, attempt, and bundle revision afterward.
Those records let a restarted Host re-adopt an exact process without parenting it.

PID existence is not readiness. Host passes `QUASAR_CLUSTER_READY_PATH` and the stable
cluster/slot/attempt identity to the node. After Gateway registration and plugin readiness,
the planned integration requires the node to atomically write a versioned receipt at
that path with the same identity plus
its node ID, registry epoch, endpoint, and PID. Host reports `Spawning` until the receipt
matches. Automatic kill requires the exact launch record and, for a ready node, the
Gateway-requested node ID and epoch. The released node plugins do not yet write this
receipt. A mismatched process identity or occupied reserved
port is reported as `unmanaged_conflict`; Host leaves the process untouched.

## Current Gateway commands and operation recovery

`POST /api/v1/clusters/{name}/commands` accepts `{ "action": "save-all" }` and
requires cluster-manage access and an `Idempotency-Key`. GUI actions use the same
command service. Supported actions are `save-all`, `shutdown`, `gateway-restart`,
`config-set`, `node-close`, `node-kill`, `wa-move`, `kick`, `ban`, `unban`, `chat`
and `trigger`. `parameters` contains the current pinned Gateway request DTO;
`target` identifies a slot, player or maintenance task as appropriate.

For example, write `{ "action": "config-set", "parameters": { "expectedRevision": 4,
"slots": [] } }` to a JSON file, then invoke Bootstrap's `cluster command NAME FILE`
with the usual connection/auth options and `--idempotency-key KEY --wait`.
Slots are upserted; load current config before editing.
`cluster events NAME --cursor N --limit 100` and `cluster chat-history NAME` expose
cursor-based observation. JSON command input also accepts `-` for standard input.

Gateway operations remain Running on HTTP 202. Quasar persists the remote ID and a
stable forwarded key before sending, resumes after restart, and records the remote
terminal result. A transport outage leaves the outcome pending. Keep the original
Gateway URL while an operation is pending. Local `goal` success means the desired
state was saved; query `lifecycle` separately for actual convergence.
Reconciler-initiated shutdown uses this same journal. A new On goal waits with
`shutdown_pending` until an outstanding shutdown finishes. Per-cluster gates serialize
worker lifecycle effects, catalog goal/spec edits, command submissions and remote
operation polling. They do not replace future fenced execution or distributed leases;
avoid out-of-band Host actions and manual lifecycle-file edits during an operation.

The host's local process adoption and exact-incarnation execution remain available
for development tests. Automatic node actualization is disabled with
`executor_contract_unavailable`: cluster v1.0.3 publishes NodePlan but no public
versioned executor report contract. The legacy registry heartbeat is not used.
Packaged lifecycle integration and live executor acceptance remain pending.

## Cluster Agent identity and fleet observation

`GET /api/v1/clusters/{name}/fleet` (CLI: `cluster fleet NAME`) joins a single
Registry status snapshot with Agent telemetry. Matching requires the exact cluster ID,
slot, node ID and positive 64-bit epoch. Missing identity or multiple claimants yields
no matched Agent. Registry player/admission, node state and leases remain authoritative.
Disconnected telemetry never changes the cluster's desired state.

The Agent publishes `clusterSlot` and `clusterEpoch` in hello/snapshot messages. Cluster
processes use a random lifetime Agent ID, preventing PID reuse from reusing Agent state.
The retained development launcher supplies `QUASAR_CLUSTER_SLOT`,
`QUASAR_CLUSTER_ATTEMPT` and `QUASAR_CLUSTER_READY_PATH`. If the runtime writes the
existing readiness receipt, the Agent checks schema, cluster, slot, attempt and PID
before reading its Registry-issued node ID and epoch. Epoch zero means unknown; it is
never derived from entity IDs. The v1.0.3 package does not publish this receipt; runtime identity integration
remains pending. `CLUSTER_NODE_EPOCH` must not be used as the Registry incarnation. Agent observation alone is not a prerequisite
for cluster operation.

Metrics, profiler samples, plugin statistics and logs use a filesystem-safe hash of
cluster/slot/node/epoch. Unknown incarnations additionally include the process Agent ID.
Existing standalone history retains its original server key. Plugin configuration
snapshots and edits are bound to a connection; reconnecting clears prior snapshots,
and an editor from the old connection cannot apply to its replacement. The ordinary
Plugins page and log selector display cluster/slot/node/epoch labels.

The cluster detail page includes fleet process telemetry, plugin runtime state,
statistics/profiler snapshots, recent logs, Registry players with kick/ban controls,
and an event tail capped at 200 rows with truncation/reset indication. Drain and force
removal use the shared command service; force removal includes the displayed epoch.
Profile and world-template links reuse the existing catalogs. Applying those references,
generating boot images and converting worlds remain part of packaged provisioning.

## Cluster plugin configuration status

The one-server plugin-management requirement is specified in
[Cluster Plugins](ClusterPlugins.md). Common plugin artifacts and effective config
must be identical across the fleet, with centrally managed role-specific infrastructure.
The current Plugins editor still addresses individual Agents; it does not synchronize
cluster settings or confirm fleet-wide application. Do not treat a per-Agent edit as
a cluster-wide change. Replacing that path, persisting cluster revisions and enforcing
startup/config consistency are planned integration work, not shipped behavior.

## Cluster release staging and selection (Linux)

For an already registered cluster, discover the latest stable package and explicitly
stage its version and archive SHA-256 on the Quasar worker host:

```bash
./Quasar cluster package-release production --url https://quasar.internal
./Quasar cluster package-stage production 1.0.3 <sha256-from-release> \
  --url https://quasar.internal --idempotency-key stage-production-1.0.3 --timeout 900
```

Equivalent API: `GET /api/v1/clusters/{name}/package-release`, then
`PUT /api/v1/clusters/{name}/package` with `{ "version": "1.0.3", "sha256": "..." }`
and `Idempotency-Key`. Discovery needs cluster-query access; staging needs
cluster-manage access. Both enforce the principal's cluster allow-list. The existing
GitHub update credential must have read access to the private `CometWorks/cluster`
repository. Asset downloads use GitHub's authenticated release-asset API.

Staging waits for verification and returns a durable operation record, including the
installation receipt on success. Allow a longer HTTP timeout for cold downloads.
Retry an interrupted request with the same key and identical body; a completed
request replays its recorded outcome. Use a new key to retry a recorded failure or
revalidate staged files. Cancellation leaves the operation resumable by resubmission;
it is not automatically retried by the Gateway-operation background reconciler.

Packages are staged under `<quasar-root>/ManagedRuntime/Tools/Cluster/<version>/` using
Quasar's managed-runtime directory convention, with `installation.json` and the
original `cluster-<version>/` directory. The receipt records release/asset IDs,
archive SHA-256, source commit, package path and every extracted file hash. A repeat
stage with a new key revalidates the installation. Corrupt, changed or incomplete
versions fail closed and are never overwritten. Failed/cancelled extractions are
removed; a process crash may leave an unused `.stage-*` directory that can be removed
while staging is idle. Staging serializes within the worker without holding up unrelated admin operations; installations are
promoted by a same-filesystem rename without overwriting an existing destination.

After staging, select the package for future provisioning of this cluster:

```bash
./Quasar cluster package-selection production --url https://quasar.internal
./Quasar cluster package-select production 1.0.3 <sha256-from-release> 0 \
  --url https://quasar.internal --idempotency-key select-production-1.0.3
```

`GET /api/v1/clusters/{name}/package-selection` returns the current `revision`,
`selection`, `verified` and `errorCode`. With no selection, revision is `0`, selection
is null and verified is false. `PUT` on the same route accepts
`{ "version": "1.0.3", "sha256": "...", "expectedRevision": 0 }` and requires an
`Idempotency-Key`. Use the revision returned by GET, not a guessed revision. Reads
require cluster-query access; writes require cluster-manage access, with the same
cluster allow-list enforcement as staging.

Selection revalidates the local receipt, manifest identity and every package file
without contacting GitHub. Only verified staged packages can be selected. The catalog
persists version, archive SHA-256, source commit, selection revision and retry key in
`cluster.json`; no client-supplied filesystem path is accepted. Concurrent changes use
compare-and-swap. A stale revision produces a durable Failed operation with
`package_selection_conflict`; read the current selection and submit a new request/key.
Missing or invalid files also fail without changing the selection. Selection survives
worker restart, and a retry after an interrupted catalog write does not increment the
revision twice or overwrite a newer selection.

GET verifies the current files again and reports `cluster_package_missing` or
`cluster_package_invalid` when verification fails. Replaying a completed mutation
returns its historical result; it does not prove the files remain valid. Query selection
for current verification. Both selection and verification work offline after staging.

The selected package is a candidate for future provisioning, not an active deployment.
Selection preserves the current goal, Gateway specification and lifecycle request
identity. These operations do not activate a package, alter a running cluster,
provision a remote Host, or install the external DS/Magnetar/Direct Transport
requirements. Cluster plugins are included in v1.0.3. The release manifest is not
compatible with the retained executor bundle manifest described above. See the
[continuation plan](Phase4IntegrationPlan.md) for lifecycle and upstream requirements.

## Cluster dependency provisioning (Linux)

Quasar can now pin and copy the installed DS, its Content tree, Magnetar, a compiled
Direct Transport bundle and common plugins with their resolved native assets into a separate dependency installation. This prepares inputs
for a future deployment. It does not start the cluster or modify the shared runtime.
The input directories are configured on the worker; API callers supply hashes and
revisions, never arbitrary filesystem paths.

Install DS and Magnetar using the existing managed-runtime workflow first. The snapshot
uses the runtime resolver's installed DS64 path plus its sibling `Content` directory,
and the configured Magnetar install root. This path requires the current Linux layout
(`MagnetarInterim.bin` and `Libraries/MagnetarInterim/PluginSdk.dll`). Provisioning does
not invoke the standalone updater or silently download a newer dependency. Copying the
full DS/Content tree requires sufficient additional disk space.

Build Direct Transport once from an explicit source commit, using the repository helper:

```bash
bash scripts/package-cluster-direct-transport.sh /path/to/direct-transport \
  <exact-40-character-commit> /path/to/DedicatedServer64 /path/to/Magnetar \
  /path/to/artifacts/DirectTransport
```

The helper requires Git, the .NET 10 SDK, Python 3 and standard Linux tools. It builds
committed source in a temporary directory, disables post-build deployment and refuses
to overwrite an existing output. The artifact contains `DirectTransport.dll`, its
`LiteNetLib.dll` dependency, and both `DirectTransport.xml` and `DirectTransport.dll.xml`
with the exact source commit and `CoreCLR`/`Linux` runtime metadata for the net10.0 Linux
build. Current Magnetar reads the first manifest, with the second naming convention as
a fallback. Provisioning rejects unsupported runtime metadata such as `NETCoreApp`.
Rebuild and stage a new candidate for older bundles; do not edit an immutable snapshot.
No node builds this artifact independently.

Export compatibility and common plugins from already resolved Magnetar caches for the
selected runtime. Supply the exact, commit-pinned hub descriptor and its matching cache
directory (containing `manifest.xml`, `Bin/plugin.dll` and resolved assets):

```bash
python3 scripts/package-cluster-plugins.py \
  --plugin /path/to/pinned/DotNetCompat.xml /path/to/cache/DotNetCompat \
  --plugin /path/to/pinned/LinuxCompat.xml /path/to/cache/LinuxCompat \
  --output /path/to/artifacts/CommonPlugins
```

Repeat `--plugin` for every common plugin. The exporter performs no downloads, builds
or server starts. It copies the compiled DLLs and resolved assets, rewrites runtime asset
paths to stay inside each bundle, and retains the source/cache metadata for provenance.
It rejects unpinned descriptors and links; publication is atomic and never replaces an
existing output. The operator must supply a matching cache built for the selected game
and Magnetar; content hashes alone cannot establish binary compatibility or source provenance.
Do not use `NativeWrapperCache` as the native dependency input: generated wrappers are
not the downloaded runtime payload.

Configure both artifact directories in worker `appsettings.json`:

```json
{
  "Quasar": {
    "ClusterDependencies": {
      "DirectTransportDirectory": "/path/to/artifacts/DirectTransport",
      "CommonPluginsDirectory": "/path/to/artifacts/CommonPlugins"
    }
  }
}
```

After selecting a cluster package, inspect the dependency candidate and current selection:

```bash
./Quasar cluster dependency-candidate production --url https://quasar.internal --timeout 900
./Quasar cluster dependencies production --url https://quasar.internal --timeout 900
```

Candidate inspection hashes all input files and executable flags. It returns a
`manifestSha256`, `packageSelectionRevision`, Direct Transport commit, file count and
byte count. Create `dependencies.json` from those results; use the current dependency
hash from `dependencies` for `expectedDependencySha256`, or null for the first selection:

```json
{
  "manifestSha256": "<candidate hash>",
  "expectedPackageRevision": 1,
  "expectedDependencySha256": null
}
```

```bash
./Quasar cluster dependencies-stage production dependencies.json \
  --url https://quasar.internal --idempotency-key dependencies-production-1 --timeout 900
```

API equivalents are `GET /api/v1/clusters/{name}/dependency-candidate`, and GET/PUT
`/api/v1/clusters/{name}/dependencies`. Query/manage policies and cluster allow-lists
apply as for package selection. PUT returns a durable operation. It rechecks the approved
hash, copies and verifies the bytes, then checks both catalog selections before attaching
the result to the cluster. Changed inputs fail without replacing the previous selection.
Package/dependency selection conflicts are durable failed operations; read the current
state and use a new request/key. Replaying a completed operation returns historical results.

Installations live under `<quasar-root>/ManagedRuntime/Tools/ClusterDependencies/<hash>/`:
`manifest.json` binds the selected cluster package and every dependency file, and
`payload/` contains `DedicatedServer/{DedicatedServer64,Content}`, `Magnetar` and
`DirectTransport` and `CommonPlugins`. Schema 2 requires both compatibility plugins,
their native libraries, pinned source metadata, contained local asset paths and complete
common-plugin dependency IDs. Old schema-1 snapshots remain on disk but must be replaced
by a newly approved schema-2 candidate before use. Promotion is atomic. Copies share no writable files with the inputs.
Symbolic links and incomplete payloads are refused; inputs are limited to 200,000 files
and 100 GiB. Cancellation cleans up staging; a process crash can leave an unreferenced
`.stage-*` directory. Remove those only while provisioning is idle.

The cluster catalog stores `dependencyManifestSha256`. Changing package selection clears
that candidate dependency selection while retaining the old installation on disk. GET
`dependencies` revalidates the selected package, manifest and copied files offline; a
bad or missing installation reports `verified: false` and `cluster_dependencies_invalid`.
Existing snapshots can be reused after source directories change or disappear. Provisioning
preserves the cluster goal, Gateway spec and lifecycle request identity.

These content pins prove which bytes were prepared, not runtime compatibility. The released
v1.0.3 launcher still selects Direct Transport from source/hub and warms compatibility plugins.
An upstream source follow-up adds `DIRECT_TRANSPORT_BINARIES`, pointing at the selected
snapshot's `payload/DirectTransport` folder, with `DIRECT_TRANSPORT_SOURCE` unset. It copies
the compiled bundle into each node's local plugins and refuses invalid metadata or conflicting
transport inputs. Managed Host generation wires these paths into launches; v1.0.3 does not support them.
Consume a verified future package containing that CLI change; do not patch a staged package.
The follow-up also accepts `CLUSTER_COMMON_PLUGINS`: copies the frozen common bundles,
disables hub/dev/mod sources, leaves compatibility selection to Magnetar's core loader,
and uses `-noupdate` even for the warm-up node. Portable Host transfer and runtime
admission are implemented by the managed deployment flow below. Do not point a running cluster at the shared input directories or count this receipt as readiness.

## Host deployment preparation (Linux)

Export the selected verified package and schema-2 dependency snapshot through the
cluster-manage-authorized `GET /api/v1/clusters/{name}/deployment-inputs`, or:

```bash
./Quasar cluster deployment-inputs production --url https://quasar.internal --timeout 900 \
  | jq '.data' > deployment-inputs.json
sha256sum deployment-inputs.json
dotnet Quasar.Host/bin/Debug/net10.0/Quasar.Host.dll deployment prepare \
  --file deployment-inputs.json --sha256 <the-file-hash> --directory /path/to/host-deployments
```

This is a local preparation command, not a service start. The source directories must
be present at the exported absolute paths on the Host. It verifies the approved input
document, the complete file lists/hashes/executable flags, package/dependency identities
and plugin assets, then copies into `<directory>/<input-file-hash>/` atomically. It never
overwrites an existing deployment. Repeating the command verifies existing copies and
works even after the source directories disappear. Changed bytes or unexpected files
fail verification; interrupted copies are not promoted. Transfers between hosts are not
implemented by this command.

The result contains `Package/`, `Dependencies/`, `inputs.json` and
`launch-environment.json`. The environment binds DS/Magnetar, compiled transport,
common plugins and Gateway/tool/plugin paths to the prepared copies and clears source,
extra-plugin and hub overrides. This prepares deployment inputs; it does not change the
catalog's active revision, attach an executor, start any process or prove readiness.
Managed configuration/activation below supplies writable runtime directories, synchronized
plugin configuration and fenced admission.

Export and Host preparation require a package containing the verified
`cli/deployment-capabilities.json` marker (`schemaVersion: 1`, `frozenPluginBundles: true`).
The upstream build scripts now package that marker with the supporting CLI. Published
v1.0.3 lacks it and is refused for this deployment path; a future verified upstream
release is required. Existing release artifacts are never patched in place.

### Managed cluster preparation and activation

`Quasar.Host deployment configure --installation DIR --file SPEC --sha256 SHA256
--host HOST --world SEED --directory CONFIG` invokes the verified package's
nonlaunching preparation command. All Hosts use the same approved specification and
world-file hashes; each gets its own manifest and writable run root. The package's
`docs/ManagedDeployment.md` describes the specification. Conversion uses its shipped
`cli/managed_deployment.py --installation DIR convert --source WORLD --destination NEW`.
Existing worlds are never automatically wiped or refreshed.

Activate a prepared local manifest with `Quasar.Host deployment activate --url URL
--token-env ENV --file ACTIVATION.json`. The authenticated Host endpoint is
`PUT /host/v1/deployments/{clusterId}`. Activation requires every recorded local process
to be stopped, checks the expected prior manifest hash, verifies files and journals the
attachment/Gateway transition for restart recovery. It does not start a process.

Quasar's `GET/PUT /api/v1/clusters/{uniqueName}/deployment` inspect/activate a revision
across Hosts. CLI equivalents are `cluster deployment NAME` and
`cluster deployment-activate NAME REQUEST.json --idempotency-key KEY`.
Activation requires goal Off and clean-Down proof for an existing deployment. The
request includes `expectedRevision`, `revision`, and `hosts`; each host names `hostId`,
`commandUrl`, `tokenEnvironmentVariable` and its Host `activation` request. Each local
activation names `clusterId`, `expectedBundleManifestSha256`, `bundleManifestPath`,
`bundleManifestSha256`, `gatewayUrl`, and `executorTokenEnvironmentVariable`.
Exactly one returned Host manifest must own the Gateway. Quasar commits active identity
after all Hosts acknowledge; retries reuse their durable activation receipts.

Executor credentials are separate from Query/Manage credentials. The scoped Gateway
token file gives each credential scope `Executor` and name equal to the Host ID.
Host polls every 1–15 seconds, within the 60-second executor lease. Gateway credentials
are enforced on loopback when configured. Hosts resolve secret environment references
only at launch; generated manifests never contain credential values.

These contracts require coordinated upstream releases. Portable transfer, cluster config
editing and backup/update workflows are implemented; full live acceptance remains a
release gate. Verified preparation alone does not establish a working cluster.

Cluster deployment commands now include `cluster create REQUEST.json`,
`cluster deployment-prepare NAME REQUEST.json`, `cluster deployment-activate NAME REQUEST.json`,
`cluster gateway-recover NAME`, `cluster recover NAME REQUEST.json`, `cluster backups NAME`, `cluster backup NAME REQUEST.json`
and `cluster restore NAME REQUEST.json`. Mutations except catalog creation require an
idempotency key. Inspect the operation and lifecycle status separately from command acceptance.

For cross-host preparation, `Quasar.Host deployment export --installation DIR --file PACKAGE.tar`
produces a portable verified installation. Transfer it with the normal authenticated host
transport, then run `Quasar.Host deployment import --file PACKAGE.tar --sha256 INPUTS_SHA256 --directory DIR`.
The hash identifies `inputs.json`, including every expected payload hash, rather than the tar
container. Source absolute paths remain provenance; imported launch paths point to the new
installation. Transfer world seeds separately; packaged preparation verifies them against the
common specification's `worldFiles` checksums before producing an execution bundle.

Cluster backups use `Quasar:BackupDirectory` under `Clusters`. Automatic-marked backups use
existing server-backup retention settings. Manual backups are retained. Snapshot and restore
require stopped managed processes on every Host. A restore requires rotated node, admin and
executor credentials using new environment references; old accepted-token hashes, including
previous-token slots, must not remain in the candidate scoped-token file. Host command
credentials can remain unchanged. The previous runtime directory remains beside the restored
one for explicit recovery and is not removed by backup retention.

## Managed cluster workflows

The cluster deployment panel persists one preparation specification for all Hosts.
PluginSdk configuration schemas reuse the ordinary editor. Saving changes prepares
immutable candidate configuration on every Host; use a full-downtime update to apply it.
The individual Agent editor cannot change cluster-owned configuration.

`cluster update NAME REQUEST.json --wait` submits a durable workflow and waits for its
exact ID to reach Complete. A request contains `id` (UUID) and `deployment` (the result
of preparation). For rollback use `{"id":"<new UUID>","rollback":true}`. Inspect with
`cluster update-status NAME`. API equivalents are POST/GET `/api/v1/clusters/{name}/update`.
Preflight checks every Host before goal Off. Stopping, Activating and Starting are
persistent checkpoints. Errors remain visible/retryable; completion requires matching
Gateway revision, managed readiness and Host attachments. Previous installations remain
available. Binary/config rollback preserves current world and plugin state. Direct Host
attachment/Gateway mutation endpoints reject managed clusters; use deployment, goal or
explicit recovery operations to preserve the recorded revision.

Host activation and restore compare the complete nonempty `storageFormats` map
(`world`, `registry`, `plugins`). Different formats require explicit migration. The
prepared package's exact PluginSdk hash must match the frozen Magnetar dependency.

Use `cluster command NAME REQUEST.json` for `world-export` (bounded TTL), scoped
`trigger`, `handover-config-set`, or `artifact-release`; inspect with `diagnostics`,
`handover-config`, `artifacts`, and `artifact`. `cluster artifact-backup NAME ID`
retrieves and verifies a vanilla world export into cluster backup storage, then releases
the Gateway artifact. Checks include manifest, exact inventory, file hashes and size.
For multiple Hosts, `exportDataRoots` must name paths readable by the Gateway Host.
Native stopped snapshots capture every Host independently without shared storage.

Automatic native cluster backups reuse the existing server schedule/retention only
while a cluster is already cleanly stopped. They do not schedule downtime. Manual
native backups and archived vanilla exports are retained. Snapshot capture IDs bind
the stopped lifecycle. Restore rotates runtime credentials, retains the prior runtime,
changes plugin store identity and fences old operations before startup is allowed.

### Explicit recovery after unclean cluster shutdown

Use `POST /api/v1/clusters/{uniqueName}/recover` with `{"generation":"NEW-UUID"}` and
an `Idempotency-Key`, or `cluster recover NAME REQUEST.json --idempotency-key KEY`.
The deployment panel exposes the same operation. Requires Manage authorization, goal
Off, a complete active deployment and no incomplete deployment/restore/update workflow.
Stop every node first; this operation refuses running or unknown node process states.

Quasar checks every Host's attachment identity and stopped-node state, fences/stops the
recorded Gateway process, then rechecks every Host under its execution gate. Only then
it commits the new generation with `Recover=true` and goal On. Host passes
`CLUSTER_START_RECOVERY=true`; Registry accepts only the unchanged revision/inventory,
fences stale authority and recovers retained saves and plugin records. Unsaved changes
can be lost. No world wipe, plugin-store reset or clean-shutdown proof is manufactured.

A lost acknowledgement can replay the same generation without undoing a later drain.
A terminal failed operation is immutable; after correcting the cause, retry with a new
idempotency key. The original generation may be reused if it was never committed.
Normal starts clear the recovery flag. `gateway-recover` remains the separate action
for resuming the recorded generation to finish a shutdown; ordinary Gateway restarts
also reuse their recorded generation.

Persistence failure returns 503 and pauses Gateway relay while the process stays alive.
Monitor control health as well as process state, correct the storage fault and restart.
Do not turn that failure into an automatic fresh start or data reset.

Release build and nonlaunching Host preparation check actual PluginSdk assembly metadata
for Magnetar 2.4.2.1 plus managed configuration/plugin services. The package's exact SDK
hash must still match the frozen dependency snapshot. This applies to unmanaged launches
as well; the cluster release must wait for the compatible Magnetar release.

### Guided standalone/cluster conversion

Use **Convert to cluster** on a standalone server or **Convert to standalone server**
on cluster details. Both workflows preserve sources/backups and create a stopped
destination. [Cluster Conversion](ClusterConversion.md) covers prerequisites, placement,
plugin settings, UUID-based resume and matching API/CLI routes. Reverse conversion
uses native snapshots from all Hosts and needs no shared filesystem. Canonical plugin
settings are exported for review/reapplication; private/shared plugin storage remains
in the backup.
