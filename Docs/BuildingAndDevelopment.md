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
- `Quasar.Agent` inherits the repo/package version. Release and publish builds
  pass the same `Version`, `AssemblyVersion`, `FileVersion`, and
  `InformationalVersion` props into the nested agent build so Quasar can compare
  the bundled agent DLL with agents already loaded in running DS processes after
  a supervisor update.
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
- Managed Magnetar installs record the GitHub release tag, asset name, and
  download URL in `.quasar-magnetar-release.json` under the install directory.
  Quasar compares the stable release identity (release tag + asset name) with
  the latest full Magnetar release at startup and whenever a managed instance
  needs a launcher, so an unchanged release is reused instead of downloaded
  again. A successful GitHub release check is cached in memory for five minutes,
  so multiple managed instance starts in that window reuse the same version
  result instead of calling GitHub again. Direct archive URL overrides are cached
  by exact URL because they do not expose a separate release tag. If the latest
  check or replacement fails while a launcher already exists, Quasar logs the
  failure and continues using the installed launcher. The background Magnetar
  update check runs once per hour after startup warmup.
- At Quasar startup, the managed runtime warmup immediately checks the managed
  SteamCMD install and the managed Space Engineers Dedicated Server install. If
  either is missing, Quasar downloads it before managed Magnetar servers can be
  launched. The dashboard shows live SteamCMD and Dedicated Server preparation
  status while this happens and hides the installer panel once both are ready.
  The Dedicated Server SteamCMD download is tried up to three times before Quasar
  reports failure, and the dashboard exposes a retry action for that row.
- On Linux, Quasar prepares its managed SteamCMD `linux64` native runtime
  directory and exposes it to the Magnetar child process through
  `LD_LIBRARY_PATH` when that directory contains `steamclient.so`,
  `libtier0_s.so`, and `libvstdlib_s.so`. This lets Steam GameServer
  initialization work on fresh headless hosts that do not have a desktop Steam
  install under `~/.local/share/Steam`.

## Utilities

For local web UI development, run the worker directly:

```bash
dotnet run --project Quasar/Quasar.csproj
```

This uses the development launch profile and the normal Quasar data directory
(`~/.config/Quasar` on Linux unless `QUASAR_DATA_DIR` is set). The Bootstrap
launcher and release/update cutover paths are covered by the packaged installer
and release workflows rather than a local deploy helper.

Generate synthetic analytics data for local testing:

```bash
python3 scripts/generate-analytics-data.py
```

Optional `--server <name>` to target one server, `--days <n>`, `--seed <n>`,
`--raw-hours <hours>`, `--raw-interval <seconds>`. Uses `QUASAR_DATA_DIR`
automatically if set, otherwise defaults to the local Quasar data root.

Managed agents collect continuous profiler telemetry for Analytics. The default
agent profiler mode is `SafeContinuous` ("Simple, low overhead" in the UI),
which keeps low-overhead high-level timing for frame/update, scripts, physics,
network/replication/session, and game-loop buckets without patching every entity
update method. Set the per-server mode in the Analytics page to
`DeepContinuous` ("Extensive, deep detail") for Harmony IL call-site
attribution. Deep profiler snapshots surface top grid and entity type timing in
the Profiler: Top Grids and Profiler: Entity Types panels when those patch
groups produce samples. Set it to `Off` when troubleshooting profiler
compatibility. See
[Architecture](QuasarArchitecture.md) for how this telemetry flows through the
supervisor.
