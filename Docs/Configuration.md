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

`Quasar.Agent` enters cluster mode when `SE_CLUSTER_GATEWAY_REGISTRY` is non-empty,
the same activation condition used by ClusterRuntime. The host executor also supplies
`SE_CLUSTER_ID`, `SE_CLUSTER_NODE_ID`, and `SE_CLUSTER_NODE_ROLE`; the agent includes
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
- `GET /api/ready` confirms that the API worker and its durable server catalog are
  ready for queries.
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
  "configProfileId": "survival",
  "worldTemplateId": "main-world"
}
```

The first Phase 4 API slice is read-only and uses Gateway admin contract version
1. It is available in normal and headless operation:

- `GET /api/v1/clusters`
- `GET /api/v1/clusters/{uniqueName}/health`
- `GET /api/v1/clusters/{uniqueName}/status`
- `GET /api/v1/clusters/{uniqueName}/plan`
- `GET /api/v1/clusters/{uniqueName}/recovery-readiness`

Responses preserve the Gateway envelope, capture time, string enum values, and
stable error codes. When Quasar authentication is enabled, these routes require
a human `CanView` role or the scoped service-principal permission described below.
`/api/health` and `/api/ready` include
`configuredClusters`; they do not contact Gateway and remain usable if a Gateway
is down. Recovery readiness is calculated by Gateway from its durable Registry,
Save Catalog, snapshot, WAL, and artifact-holder records; Quasar does not infer it
from node liveness. Command operations and UI editing are later Phase 4 slices;
this query surface does not imply them.

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
versioned cluster query routes; it grants no server logs, configuration access, or
mutation permission. Invalid credentials and forbidden cluster access return
versioned JSON errors instead of redirects. Identical token values assigned to
multiple principals fail closed.
