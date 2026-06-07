# Building and Development

How to build Quasar from source, the project layout, and local development
utilities. For the runtime design see [Architecture](QuasarArchitecture.md); for
the full per-file reference see the generated [code handbook](Reference/TOC.md).

## Projects

- `Quasar`
  Blazor Server supervisor host, DS process manager, config/runtime preparation,
  and WebSocket endpoint for agents.
- `Quasar.Agent`
  Dedicated Server plugin that attaches to Quasar and exposes telemetry/commands.
- `Quasar.Bootstrap`
  Ensure-running helper used for the Quasar startup/bootstrap flow.
- `Magnetar.Protocol`
  Shared transport and discovery contracts currently used by Quasar and
  Quasar.Agent.

The solution file is `Quasar.sln`.

## Build setup

- `Quasar.Agent` depends on a local `DS64` path for Space Engineers Dedicated
  Server assemblies.
- On Windows the solution builds out-of-the-box: `Directory.Build.props`
  auto-resolves `DS64` from the Steam registry `InstallLocation` (falling back to
  the default `C:\Program Files (x86)\Steam\...\DedicatedServer64` library) and
  `MagnetarBin` to `$(Magnetar)\Libraries\MagnetarLegacy`. On Linux `MagnetarBin`
  resolves to `$(Magnetar)/Bin`.
- A local-only override can live at `Quasar.Agent/Directory.Build.props`. This
  repo keeps the machine-specific override out of source control.
- The Linux release workflow probes the Space Engineers Dedicated Server public
  build id, restores/caches only `DedicatedServer64/` by that id, and feeds the
  cached path to the build through `DS64`. On a cache miss it downloads the
  Windows depot with SteamCMD and retries the install to work around transient
  missing-configuration failures.

## Managed runtime selection

- On Windows, managed servers can run on either Magnetar build — .NET 10 (the
  "Interim" build, default) or .NET Framework 4.8 (the "Legacy" build). Pick the
  build per server with the `.NET runtime` field in the server editor; Quasar
  downloads both builds together from the latest full GitHub Magnetar release
  asset matching `MagnetarForWindows-*.7z` so switching never re-downloads.
- On Linux only the .NET 10 (Interim) build ships, from the latest full GitHub
  Magnetar release asset matching `MagnetarForLinux-*.7z`; a `NetFramework48`
  selection carried over from a Windows `server.json` is silently downgraded to
  .NET 10.

## Utilities

Generate synthetic analytics data for local testing:

```bash
python3 scripts/generate-analytics-data.py
```

Optional `--server <name>` to target one server, `--days <n>`, `--seed <n>`,
`--raw-hours <hours>`, `--raw-interval <seconds>`. Uses `QUASAR_DATA_DIR`
automatically if set, otherwise defaults to the local Quasar data root.

Managed agents collect default low-duty profiler telemetry for Analytics:
per-grid, per-script, per-entity, physics, network/replication/session, and
game-loop timing buckets. See [Architecture](QuasarArchitecture.md) for how this
telemetry flows through the supervisor.
