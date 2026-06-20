# Quasar Configuration

This document covers the runtime settings most operators need to change: the web
UI **listening host and port**, and the **browser auto-open** behavior on start.

Other settings (auth, updates, analytics, logging, managed runtime) live in the
same `appsettings.json` under the `Quasar` section; the update settings are
documented in [Windows](WindowsDeploymentAndUpdates.md) and
[Linux](LinuxDeploymentAndUpdates.md) deployment guides.

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

## Magnetar data handling consent

Magnetar's anonymous plugin-usage statistics are opt-in. Quasar stores the
operator's decision in `data-handling-consent.json` under the Quasar data
directory and passes that decision to every managed Magnetar start:

- `YES` -> Quasar appends `-consent`
- `NO` -> Quasar appends `-noconsent`
- no stored decision -> Quasar appends `-noconsent`

The Dashboard shows a top-of-page YES/NO consent prompt until a decision is
stored. The same decision can be changed later from **Settings -> Security**.
Changes apply to the next server start or restart; running servers keep their
current Magnetar consent state.

Magnetar sends only the enabled plugin IDs plus a random local instance ID when
consent is granted. It does not send a Steam ID, account, world, or server
content.

## Where configuration is read from

Both the **Bootstrap launcher** (`Quasar`/`Quasar.exe`) and the replaceable **web
worker** read JSON config from these locations, later ones overriding earlier:

1. The install directory `appsettings.json` (next to the executable). Auto-updates
   preserve this file during Bootstrap self-updates. UI-worker activation updates
   it from the staged, resolved `appsettings.json` so Bootstrap and the managed
   worker keep the same base settings.
2. The Quasar **data directory** `appsettings.json` — the recommended place for
   persistent local overrides because it is never touched by updates:
   - Windows: `%APPDATA%\Quasar\appsettings.json`
   - Linux: `~/.config/Quasar/appsettings.json` by default for `install.sh`
     systemd installs (or `$QUASAR_DATA_DIR/appsettings.json`)

The shipped defaults are defined in [`Quasar/appsettings.json`](../Quasar/appsettings.json).

During UI-worker staging, Quasar performs a three-way merge for `appsettings.json`:
the previous release base stored under the data directory is the merge base, the
current install-directory `appsettings.json` supplies local values, and the new
release file supplies new defaults. Clean local changes are carried into the
staged version automatically. If both the local file and the release changed the
same setting differently, the Updates page shows a conflict editor with
git-style markers. Resolve and save the JSON before activation, or use **Force
release defaults** to discard local appsettings values for that staged release.

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

Relative paths are resolved under the Quasar data directory. If the folder is on
a network share, make sure it is mounted before Quasar starts and that the
Quasar service account can create, list, read, and delete files in it. Changes
from the Backup page apply to new stored-backup operations immediately; direct
file edits need a Quasar restart. Existing backup ZIPs are not moved
automatically; move them manually if they should appear in the new folder. When
`QUASAR_BACKUP_DIR` is set, it takes precedence and the Backup page shows the
active folder as read-only.

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

Quasar can grant a trusted-network session to loopback or same-subnet clients.
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
        "AllowSameSubnet": true,
        "TrustedProxies": [ "172.18.0.0/16" ],
        "Roles": [ "admin" ]
      }
    }
  }
}
```

For public deployments, prefer disabling same-subnet bypass so browser access is
always tied to Steam/RBAC identity:

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

### Development port

Running the worker directly with `dotnet run --project Quasar/Quasar.csproj` uses
[`Quasar/Properties/launchSettings.json`](../Quasar/Properties/launchSettings.json),
which sets `applicationUrl` to `http://0.0.0.0:8080`. Change `applicationUrl` there
to use a different port for local `dotnet run` sessions.

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
