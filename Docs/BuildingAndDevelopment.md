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

Quasar, Bootstrap and Quasar.Host compile the exact current Gateway contract source pinned under
`Contracts/ClusterGateway.AdminContract`. `SOURCE.md` records its upstream revision and
checksum. No neighboring Gateway checkout or private contract package is needed.

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
  resolves to `$(Magnetar)/Libraries/MagnetarInterim`, falling back to
  `$(Magnetar)/Bin` only while an older Magnetar install with that layout is
  still present.
- Release CI locates `PluginSdk.dll` recursively in the extracted Magnetar
  archive and sets `MagnetarBin` to its directory. This supports both the older
  Linux `Bin/` layout and the current `Libraries/MagnetarInterim/` layout.
- A local-only override can live at `Quasar.Agent/Directory.Build.props`. This
  repo keeps the machine-specific override out of source control.
- The Linux release workflow probes the Space Engineers Dedicated Server public
  build id, restores/caches only `DedicatedServer64/` by that id, and feeds the
  cached path to the build through `DS64`. On a cache miss it downloads the
  Windows depot with SteamCMD and retries the install to work around transient
  missing-configuration failures.
- Building `Quasar/Quasar.csproj` no longer requires the Entity Viewer source tree
  or its npm packages. The viewer plugin is installed from QuasarHub, where the
  catalog pins a commit in `https://github.com/CometWorks/viewer`. The plugin
  installer downloads that commit's GitHub archive, builds the adapter project
  against the running Quasar worker's `Quasar.Plugin.Abstractions.dll`, and serves
  the viewer static assets from `/_quasar/plugins/{pluginId}/`. Single-file release
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
  Runtime-only packaged installs can run Quasar. When a QuasarHub source-built
  UI plugin is installed or updated, Quasar first uses a matching .NET SDK on
  `PATH`, then a previously downloaded private SDK. If neither is available, the
  admin can approve an on-demand download of the pinned SDK into Quasar's
  managed data directory, choose to install it with the system package manager,
  or cancel the plugin operation.

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
- On Linux, Quasar runs its managed SteamCMD with a private `HOME` (and XDG base
  directories) under `{Quasar data}/ManagedRuntime/Tools/SteamCmdHome`, overridable
  with `QUASAR_STEAMCMD_HOME_DIR` or `Quasar:ManagedRuntime:SteamCmdHomeDirectory`.
  SteamCMD resolves its Steam root through `~/.steam`, and with the desktop Steam
  client installed it would otherwise log into the client's directory and rewrite
  the client's `libraryfolders.vdf` files on every run, dropping every app it did
  not touch itself. The Dedicated Server install location is still passed with
  `+force_install_dir`, so the isolated home only holds SteamCMD's own state.

## Docker image

Full releases publish `ghcr.io/cometworks/quasar` with `latest`, numeric version,
and `v`-prefixed version tags. The Dockerfile consumes the already-packaged Linux
worker archive and verifies `SHA256SUMS`, keeping release packaging as the single
source of truth. Container publishing runs only for pushes to `main`; pull
requests, tags, and manual workflow runs never publish images. Build provenance
is disabled because GHCR otherwise lists the single-platform image, attestation,
and their index as three separate package versions:

```bash
docker build --build-arg QUASAR_VERSION=1.1.0.31 -t quasar:1.1.0.31 .
```

See [Docker Deployment](Docker.md) for runtime use. The Dockerfile is not the
local source-build path; use `dotnet` commands below when testing code changes.

GitHub creates a new GHCR package as private even when its source repository is
public. After the first workflow publish, a CometWorks organization owner must
change the `quasar` container package visibility to **Public** once. The workflow
logs out of GHCR and verifies an anonymous pull, so it fails visibly until this
one-time package setting is correct.

## Utilities

For local web UI development, run the worker directly:

```bash
dotnet run --project Quasar/Quasar.csproj
```

This uses the development launch profile. Without `QUASAR_INSTALL_DIR`, the
direct worker uses its app base directory as the install root. The Bootstrap
launcher and release/update cutover paths are covered by the packaged installer
and release workflows rather than a local deploy helper.

To check Bootstrap shutdown without starting the Quasar web service or any
dedicated servers:

```bash
dotnet build Quasar.Bootstrap/Quasar.Bootstrap.csproj
python3 scripts/test-bootstrap-shutdown.py
```

This dependency-free Python check runs an isolated Bootstrap build with a fake
HTTP worker in foreground and service modes. It verifies stale-request cleanup,
worker crash recovery, and intentional shutdown of both processes with Bootstrap
exit code `0`. Windows Task Scheduler and Linux systemd policy still need native
deployment validation; the installed restart-on-failure policies must remain in
place for intentional shutdown to stay stopped.

The worker-side regression checks run without a web service as well:

```bash
dotnet test Quasar.Tests/Quasar.Tests.csproj --filter 'FullyQualifiedName~QuasarShutdownTests|FullyQualifiedName~QuasarControlDialogTests'
```

bUnit exercises the power dialog's confirmation buttons. The shutdown tests run
the real state-save and shutdown methods on a Blazor dispatcher, check that state
is saved before host shutdown, and verify that a failed Bootstrap request leaves
the worker running. The dispatcher test catches the synchronous-wait deadlock
that a fake-worker Bootstrap test cannot exercise.

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

Generate synthetic analytics data for local testing, including separate process CPU
(`Cpu`, potentially above 100%) and simulation CPU (`Scpu`) values:

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

## Cluster release consumer checks

`dotnet test Quasar.Tests/Quasar.Tests.csproj` covers cluster package staging with
small generated archives and fixture HTTP responses. Package staging tests are Linux
only. To run the positive staging/tamper check against a downloaded release archive:

```bash
QUASAR_TEST_CLUSTER_ARCHIVE=/path/to/ClusterForLinux-1.0.3.tar.gz \
  dotnet test Quasar.Tests/Quasar.Tests.csproj
```

The optional fixture is currently v1.0.3. Tests extract into temporary directories,
verify the installation and exercise replay/tamper detection; they do not start
Gateway, game nodes or the Quasar web service. GitHub requests remain fixtures.
Package selection tests disable fixture network access after staging and verify local
receipt/manifest/file checks, persisted selection, revision conflicts, restart/replay,
missing/tampered packages, cluster allow-list enforcement and API/CLI parity. Catalog
tests also verify that selection preserves lifecycle identity and concurrent selections
cannot overwrite each other.
Dependency tests use small file trees to verify isolated copies, offline reuse after
input deletion, changed-input rejection, missing/linked/unpinned inputs, manifest/file
tampering, selection conflicts and unchanged lifecycle state. The Direct Transport
packaging helper can be exercised against a pinned source checkout without launching
any server; see [dependency provisioning](Configuration.md#cluster-dependency-provisioning-linux).
Provisioning also rejects transport metadata with the obsolete `NETCoreApp` runtime name;
the helper emits `CoreCLR`/`Linux` for its net10.0 Linux build.

The upstream CLI follow-up lives in the sibling `cluster-quasar-integration` checkout.
Run its offline profile/wrapper/registration tests with:

```bash
python3 -m unittest discover -s ../cluster-quasar-integration/Cli -p 'test_packaged_plugins.py' -v
python3 -m unittest discover -s scripts -p 'test_package_cluster_plugins.py' -v
dotnet build Quasar.Host/Quasar.Host.csproj
python3 -m unittest discover -s scripts -p 'test_host_deployment.py' -v
```

These checks create temporary configs, resolved-cache fixtures and inert Host deployment
payloads. Host preparation verifies and copies files only; they start no Gateway,
Magnetar or game process. The modified upstream CLI needs a future verified package before
managed use; never copy modified CLI files into a previously verified release installation.
See [Phase 4 Integration](Phase4IntegrationPlan.md) for remaining live acceptance.

## Host enrollment from a development build

Host enrollment serves the single-file `Quasar.Host` executable that a release ships as
`Host/Quasar.Host` next to the web worker. Plain build output (`dotnet run --project Quasar`)
does not contain it, so enrollment answers "does not include the Host installer". Publish the
Host once and point the web worker at it with `QUASAR_HOST_BINARY`:

```bash
dotnet publish Quasar.Host/Quasar.Host.csproj -c Release -r linux-x64 -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o artifacts/dev-host
```

```bash
export QUASAR_HOST_BINARY="$PWD/artifacts/dev-host/Quasar.Host"
```

The variable is a development aid only; releases keep using the packaged `Host/Quasar.Host`,
which always matches the web worker.

## Cluster integration verification

The seven-stage [integration plan](Phase4IntegrationPlan.md) separates focused checks
from full local acceptance. Build/test Quasar services and `Quasar.Host --self-test`
without launching the web service. Upstream cluster `Build/release.sh` builds the entire
package, runs shipped Gateway/CLI self-tests and records the exact build PluginSdk hash.
Use the coordinated Magnetar build through `MAGNETAR_SDK`; old published dependencies
cannot provide the new provider contract. Check protocol parity with upstream
`python3 Build/verify-plugin-protocol.py /path/to/magnetar`.

Magnetar's `Examples/ClusterState` compiles an opt-in shared-state plugin. SDK tests
exercise durable standalone writes, CAS/restore and dispatch fences. Cluster self-tests
exercise Registry replay and lifecycle outcomes. These do not replace packaged live
provider, ownership, conversion, outage and upgrade acceptance.

## Cluster Host release package

Release packaging also produces `quasar-host-linux-x64.tar.gz` and
`quasar-host-win-x64.zip`, included in the combined `SHA256SUMS`. Each contains the
complete self-contained Host publish tree with `Quasar.Host` (or `.exe`) at its root.
Extract it into a dedicated directory on each cluster machine. Host has its own
configuration and process lifecycle; the web launcher does not install or start it.

For offline package validation, run the extracted `Quasar.Host --self-test`. This uses
inert child processes and temporary state, without launching Space Engineers or Quasar's
web service. Host source, shared deployment code and contract changes trigger release
builds alongside the web/launcher projects.
