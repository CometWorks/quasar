# Building and Development

How to build Quasar from source, the project layout, and local development
utilities. For the runtime design see [Architecture](QuasarArchitecture.md).

## Projects

- `Quasar`
  Blazor Server supervisor host, DS process manager, config/runtime preparation,
  and WebSocket endpoint for agents.
- `Quasar.Agent`
  Dedicated Server plugin that attaches to Quasar and exposes telemetry,
  commands, and generic companion-plugin request dispatch.
- `Quasar.Bootstrap`
  Ensure-running helper used for the Quasar startup/bootstrap flow.
- `Quasar.Plugin.Abstractions`
  Public contract assembly for Quasar UI plugins: plugin entry point, manifest
  model, nav contributions, page/component patch contributions, and the generic
  companion-channel interface.
- `Magnetar.Protocol`
  Shared transport, discovery, and Magnetar bridge contracts currently used by
  Quasar, Quasar.Agent, and companion plugins.
- Quasar UI plugins
  Optional UI extensions are discovered through QuasarHub and installed into the
  Quasar install directory at runtime. Entity Viewer now lives in the external
  `CometWorks/viewer` repository and is installed as a Quasar UI plugin instead
  of being staged from this repository during the core Quasar build.

The solution file is `Quasar.sln`.

## Build setup

Quasar consumes the private
`CometWorks.ClusterGateway.AdminContract` package from the `cometworks` source in
the repository `NuGet.Config`. Supply a Forgejo package-reader account or token
through NuGet's source-credential environment variable before restore; never add
credentials to `NuGet.Config`:

```bash
export NuGetPackageSourceCredentials_cometworks='Username=<user>;Password=<token>;ValidAuthenticationTypes=Basic'
dotnet restore Quasar.sln
unset NuGetPackageSourceCredentials_cometworks
```

The release workflow expects equivalent repository secrets named
`COMETWORKS_PACKAGES_USER` and `COMETWORKS_PACKAGES_TOKEN`.

- `Quasar.Agent` depends on a local `DS64` path for Space Engineers Dedicated
  Server assemblies.
- `Quasar.Agent` must not use Magnetar or Quasar release version stamping.
  Release-specific assembly/file/informational version attributes are disabled
  for the agent because they would change `Quasar.Agent.dll` bytes even when the
  agent code did not change. `Magnetar.Protocol` is also version-neutral because
  its assembly identity is recorded in the agent DLL reference metadata. Agent
  deploy drift is detected by comparing the bundled deployable
  `Agent/Quasar.Agent.dll` SHA-256 hash with the deployed Magnetar local-agent
  DLL hash. Running servers are not restarted
  automatically; reconciliation warns when a manual restart is needed to load a
  newly bundled agent.
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
- Building `Quasar/Quasar.csproj` no longer requires the Entity Viewer source tree
  or its npm packages. The viewer plugin is installed from QuasarHub, where the
  catalog pins a commit in `https://github.com/CometWorks/viewer.git`. The plugin
  installer clones that repository, builds the adapter project against the
  running Quasar worker's `Quasar.Plugin.Abstractions.dll`, and serves the viewer
  static assets from `/_quasar/plugins/{pluginId}/`. Single-file release
  packaging leaves `Quasar.Plugin.Abstractions.dll` beside the worker executable
  so packaged installs have the same physical contract path as source builds.
  When the UI plugin manifest owns a Magnetar companion project, the installer
  also builds that project with `MagnetarProtocolAssembly` pointing at Quasar's
  active protocol assembly and stages the output under
  `.quasar/companions/{companionId}`. On server prepare, enabled UI plugin
  companions are copied to the server Magnetar `Local` folder and added to the
  generated profile beside `Quasar.Agent.dll`. Viewer scene data is requested
  through `IQuasarCompanionChannel` from the viewer's Magnetar companion plugin;
  Quasar core does not carry viewer scene DTOs or a viewer-specific HTTP API.
  Runtime-only packaged installs can run Quasar, but QuasarHub source-built UI
  plugin install/update requires a matching .NET SDK on `PATH`; the UI disables
  those build actions when the SDK preflight fails.

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

This uses the development launch profile. Without `QUASAR_INSTALL_DIR`, the
direct worker uses its app base directory as the install root. The Bootstrap
launcher and release/update cutover paths are covered by the packaged installer
and release workflows rather than a local deploy helper.

To run the UI worker from Rider against an installed service/deployed tree,
write that root path into `.quasar-install-dir` at the repository root. The file
is ignored by git and is read through `QUASAR_INSTALL_DIR_FILE` from the worker
launch profile:

```bash
printf '%s\n' "$HOME/.local/share/Quasar" > .quasar-install-dir
```

`QUASAR_INSTALL_DIR` still works and wins when set directly. The worker launch
profile deliberately does not set `applicationUrl`, so host/port come from that
install root's `appsettings.json`. Packaged assets and helper scripts are also
probed from the same install root.

Generate synthetic analytics data for local testing:

```bash
python3 scripts/generate-analytics-data.py
```

Optional `--server <name>` to target one server, `--days <n>`, `--seed <n>`,
`--raw-hours <hours>`, `--raw-interval <seconds>`. Uses `QUASAR_INSTALL_DIR`
automatically if set, otherwise defaults to the local Quasar install root.

When refreshing the local graphify graph, prune generic framework plumbing after
`.graphify_extract.json` is produced and before graph build/report generation:

```bash
python3 scripts/graphify-prune-plumbing.py
```

This removes low-signal C#/.NET primitives such as `Task`,
`CancellationToken`, `string`, and collection types from the graph extraction,
so clustering and god-node reports focus on Quasar concepts instead of async and
framework plumbing.

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
