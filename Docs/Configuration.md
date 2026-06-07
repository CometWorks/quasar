# Quasar Configuration

This document covers the runtime settings most operators need to change: the web
UI **listening host and port**, and the **browser auto-open** behavior on start.

Other settings (auth, updates, analytics, logging, managed runtime) live in the
same `appsettings.json` under the `Quasar` section; the update settings are
documented in [Windows](WindowsDeploymentAndUpdates.md) and
[Linux](LinuxDeploymentAndUpdates.md) deployment guides.

## Where configuration is read from

Both the **Bootstrap launcher** (`Quasar`/`Quasar.exe`) and the replaceable **web
worker** read JSON config from these locations, later ones overriding earlier:

1. The install directory `appsettings.json` (next to the executable). Auto-updates
   preserve this file.
2. The Quasar **data directory** `appsettings.json` — the recommended place for
   persistent local overrides because it is never touched by updates:
   - Windows: `%APPDATA%\Quasar\appsettings.json`
   - Linux: `~/.config/Quasar/appsettings.json` (or `$QUASAR_DATA_DIR/appsettings.json`)

The shipped defaults are defined in [`Quasar/appsettings.json`](../Quasar/appsettings.json).

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
- A foreground run (e.g. `Quasar ensure-running`, or `./deploy.sh --run`) prints the
  URL and does not open a browser.

The URL is always printed regardless of the auto-open setting. To restrict the web
UI to the local machine, set `Quasar:Host` to `127.0.0.1` as described above.

### Auto-open setting (interactive use)

`Quasar:OpenBrowserOnStart` (env `QUASAR_OPEN_BROWSER_ON_START`, default `true`)
controls whether an **interactive console** worker auto-opens the browser. It has
no effect in service mode (services force it off). The developer launch profile
(`Quasar.Bootstrap/Properties/launchSettings.json`) still requests auto-open via
`ensure-running --open-browser` for convenience when running from an IDE.
