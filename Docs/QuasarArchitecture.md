# Quasar Architecture

This document is the implementation reference for the supervisor-based management stack that replaces the PoC Python web UI and the REST-only admin plugin.

It captures the agreed hybrid flow:

- `Quasar` is the primary long-running supervisor
- `Quasar.Agent` runs inside each Dedicated Server
- agents attach to an already-running supervisor over raw WebSockets
- if `Quasar` is missing, the agent may trigger bootstrap/setup
- the agent must not become the long-running owner of the web host

## Naming

### Final product names

- `Quasar`
  - Supervisor
  - Blazor Server host
  - DS process manager
  - config editor
  - WebSocket server for DS agents

- `Quasar.Bootstrap`
  - lightweight installer / ensure-running helper
  - can be invoked manually or from `Quasar.Agent`
  - responsible for setting up or starting `Quasar`

- `Quasar.Agent`
  - plugin loaded into Space Engineers Dedicated Server
  - telemetry, command execution, and supervisor attachment

- `Magnetar.Protocol`
  - shared contracts between agent and supervisor

### Current code mapping

The on-disk project layout now matches the runtime naming:

- `Quasar/` contains the supervisor host
- `Quasar.Agent/` contains the DS plugin
- `Quasar.Bootstrap/` contains the ensure-running helper

## Runtime Ownership

`Quasar` owns the host machine management workflow.

That means `Quasar` is responsible for:

- starting and stopping DS servers
- storing desired goal state for each server
- reconciling actual state back to desired state
- restart policy and crash recovery
- persistent server definitions
- editing DS and Magnetar configuration
- opening the Web UI
- supervising the overall management session

`Quasar.Agent` is not the supervisor. It is an in-process DS companion that:

- reports state
- receives commands
- executes game-thread actions
- assists with bootstrap when the supervisor is missing

## Target Workflow

Primary workflow:

1. user starts `Quasar`
2. `Quasar` prints a short banner and the UI URL
3. `Quasar` optionally opens the browser in interactive console mode
4. user edits one or more DS server configurations
5. user starts one or more DS servers from the UI
6. each DS server loads `Quasar.Agent`
7. each agent connects to `Quasar`
8. `Quasar` keeps DS servers running and restarts them as configured
9. user can return later via bookmark or the printed URL

Hybrid bootstrap workflow:

1. a DS server starts with `Quasar.Agent`
2. agent tries to discover `Quasar`
3. if missing, agent may invoke `Quasar.Bootstrap`
4. `Quasar.Bootstrap` ensures `Quasar` is installed and running
5. agent retries discovery and then attaches normally

Important boundary:

- the agent may trigger bootstrap
- the agent must not directly become the long-running host owner
- the long-running owner remains `Quasar`

## Hosting Modes

`Quasar` must support both:

- interactive console mode
- background unattended mode

### Linux

Support:

- foreground console mode
- `systemd` service mode

### Windows

Support:

- foreground console mode
- Scheduled Task startup/keep-alive mode

Windows Service integration is not required.

No GUI shell is required on either platform.

## Dedicated Server Scope

All managed DS servers are headless.

`Quasar` must be able to supervise:

- multiple DS servers on the same host
- separate config/world/plugin setups per server
- separate DS app-data per server
- separate Magnetar app-data per server
- restart behavior per server

Multi-server on one host is required now.

True multi-host federation across multiple hosts is not required for the first delivery, but contracts should remain compatible with it.

## Server Filesystem Isolation

Each managed DS server must have its own isolated runtime/configuration roots.

Required separation per server:

- DS app-data directory
- Magnetar app-data directory
- world/save directory
- plugin/configuration surface
- Quasar-captured server logs

Quasar should treat these as explicit server properties rather than assuming one shared machine-global app-data location.

This separation is required because different servers may:

- run different worlds
- run different plugin sets
- run different Magnetar configurations
- be upgraded or restarted independently

Command-line arguments may be used to direct Magnetar and DS to their server-specific roots, but Quasar remains responsible for owning and reconciling the complete server definition.

## Transport Model

The transport model should distinguish between:

- control plane traffic
- future bulk-state/data-plane traffic

This distinction matters because the current runtime needs reliable lifecycle/configuration messaging, while future same-host server meshing may need much heavier local traffic.

### Browser to Quasar

Use Blazor Server’s required SignalR/circuit transport.

This is framework plumbing, not the domain transport decision.

### Quasar.Agent to Quasar

Use raw WebSockets.

This channel is the main runtime communication path for:

- hello / identity
- snapshots
- commands
- command results
- admin-stop notifications (the agent reports an in-game admin shutdown so Quasar flips goal state to `Off`)
- heartbeats / reconnect handling

This is the current control-plane transport and remains the active implementation path.

### Future Local Bulk-State Transport

Future same-host server meshing may require a higher-throughput local data path than WebSockets.

For that future capability, the architecture should allow:

- shared-memory bulk-state channels for same-host traffic
- Quasar-controlled channel setup and policy
- DS-to-DS local data exchange coordinated by Quasar

Important boundary:

- Quasar remains the control-plane authority
- Quasar does not need to be the hot-path byte relay for every local bulk update

So the intended long-term split is:

- control plane: `Quasar.Agent <-> Quasar`
- local same-host bulk state plane: `DS <-> DS`, with Quasar coordinating setup

Recommended abstraction boundary:

- `IControlChannel`
- `IBulkStateChannel`

Current implementation:

- WebSocket for control plane
- no separate bulk-state channel yet

Future same-host optimization:

- shared-memory ring buffers or equivalent for bulk-state exchange
- separate control messages for setup, flow control, and error handling

Shared memory is a future transport option, not part of the active implementation pipeline right now.

### REST

REST must remain minimal.

Allowed uses:

- health
- discovery
- bootstrap/setup endpoints if needed

Domain management should not be REST-first.

## UI Theme

The Blazor Server UI should stay deliberately neutral:

- black / white primary palette
- grey accents
- no loud brand-color-first design

The UI must support both:

- light mode
- dark mode

Theme preference should be stored in browser local storage using the `Blazor.LocalStorage` package so the user returns to the same mode on the next visit.

## Discovery and Bootstrap

Supervisor discovery should use a local manifest plus health probe.

Expected local mechanism:

- runtime manifest file
- local health check
- process identity / server metadata

Bootstrap/setup should be handled by `Quasar.Bootstrap`.

Expected behavior:

- detect missing supervisor
- install or locate supervisor binaries if needed
- start supervisor in the correct mode
- return enough information for the agent to retry attachment

The current direct agent-side process spawn is only an implementation stepping stone and should be replaced by bootstrap-assisted supervisor setup.

## Process Supervision

`Quasar` must contain a DS process supervisor.

It needs:

- persistent server definitions
- stable `UniqueName` per DS server
- desired `GoalState` per DS server
- desired state tracking
- crash detection
- server health assessment
- agent attach grace handling
- agent heartbeat freshness checks
- long-uptime warning and recycle policy
- automated health recovery actions
- restart policy
- restart backoff
- last exit code / last crash reason
- captured stdout/stderr or redirected server logs

Suggested state model:

- `GoalOff`
- `GoalOn`
- `Stopped`
- `Starting`
- `Running`
- `Stopping`
- `Restarting`
- `Crashed`
- `Faulted`

Desired state is not the same thing as observed process state.

Quasar should behave like infrastructure/configuration management:

- if goal state is `On` and the server is not running, Quasar starts it
- if goal state is `On` and the server crashes, Quasar restarts it according to policy
- if goal state is `On` and the server is unhealthy, Quasar evaluates the health policy and recovers it automatically where configured
- if goal state is `Off` and the server is running, Quasar stops it
- if an admin stops the server from in-game (the Magnetar `!quit`/`!stop` command), the agent reports the shutdown intent and Quasar sets goal state to `Off`, so the server stays stopped instead of being treated as a crash and restarted
- operator actions should usually mutate goal state first, then let reconciliation perform the transition

This should be treated more like Terraform or other IaC reconciliation than like a passive dashboard.

Space Engineers dedicated servers are known to degrade over long uptimes. Health monitoring is therefore not optional polish. It is part of the core reconciliation loop.

For simulation-health checks, Quasar should mirror the dedicated server's own watcher logic rather than inventing a separate heuristic. The dedicated server computes a minimum acceptable frame advance over a time window from:

- `WatcherInterval`
- `WatcherSimulationSpeedMinimum`
- `requiredFrames = windowSeconds * 60 * minimumSimulationSpeed`

Quasar should therefore track total simulation frames reported by `Quasar.Agent`, compare frame deltas against elapsed wall-clock time, and derive a frame-progress score:

- `frameProgressScore = deltaFrames / (elapsedSeconds * 60)`

That score should be compared against a configurable minimum threshold, and save-in-progress windows should reset the baseline instead of being treated as a stall.

Each launched DS process should receive:

- stable unique name
- supervisor endpoint
- session/auth token
- config/world identifiers

Process-derived IDs alone are not sufficient.

## Headless DS Startup Model

Managed DS servers are headless.

That means:

- Quasar prepares the startup configuration
- Quasar selects the world to load
- DS does not rely on an interactive DS UI

`LastSession.sbl` is the world-selection mechanism and must be prepared by Quasar before the server is started.

Quasar owns:

- writing or updating `LastSession.sbl`
- ensuring it points at the intended world/save
- ensuring the DS and Magnetar app-data roots for that server are consistent

Launch arguments remain configurable per server, but Quasar should treat `LastSession.sbl` preparation as part of server reconciliation, not as a manual side-step.

### Splash behavior

Quasar should minimize background-start clutter:

- Quasar should launch Magnetar headless with `-noconsole`
- Quasar should pass server-specific `-path` and `-config` roots
- Quasar should pass explicit `-ds64` so Magnetar targets the intended DS install
- `-nosplash` is no longer required for current Magnetar builds

## Logging

`Quasar` must have its own dedicated logging configuration.

### Console behavior

Normal console output should be minimal:

- welcome banner
- clickable URL
- fatal error if the supervisor terminates unexpectedly

The console should not continuously mirror normal ASP.NET request/application noise during routine operation.

### File logging

Use the existing NLog approach already present in the repository rather than introducing a second logging stack.

Requirements:

- separate `Quasar` log file
- configurable text or JSON file format
- configurable minimum level
- separate supervisor logs from DS server logs

Suggested layout:

- `logs/quasar/`
- `logs/magnetars/{uniqueName}/`

### Service mode behavior

In unattended background mode:

- no browser auto-open
- no interactive console expectations
- logs go to configured log files and platform host logging as appropriate

## Browser Launch Policy

`Quasar` should only auto-open the browser when all of the following are true:

- running in interactive console mode
- browser auto-open is enabled
- an interactive desktop/session is available

It must always print the URL even when auto-open is disabled.

If the user closes the browser, `Quasar` keeps running and the user can return via bookmark or the printed URL.

## Self-Update and Version Rollover

`Quasar` should be able to stage its own updates and roll forward without stopping managed Dedicated Server processes.

The important nuance is what "seamless" actually means here.

Required guarantees for the Linux-first update path:

- DS servers keep running throughout a Quasar supervisor upgrade
- the control-plane URL stays stable after the short worker restart window
- Quasar state survives worker turnover
- agents and browsers reconnect against the new worker without operator repair

Not realistically guaranteed:

- preserving the exact same live Blazor Server circuit across a version rollover
- preserving the exact same already-open raw WebSocket agent connection across worker replacement

Required model:

- stage new versions side-by-side
- validate staged payload before cutover
- retire the old worker before starting the new worker when both use the same public port
- preserve a stable entrypoint for the browser and `Quasar.Agent` attachments
- keep managed Magnetar servers detached so worker turnover does not kill them

Expected layout:

- active runtime under a versioned release directory
- active managed web releases under `~/.config/Quasar/ManagedRuntime/WebService/<version>/`
- transient staged payloads under `~/.config/Quasar/Updates/Staged/`
- stable release pointer / manifest for the currently active version
- release identity from `AssemblyInformationalVersion` and the active-release
  pointer, not from numeric `AssemblyVersion`

Linux-first cutover ownership:

- `Quasar.Bootstrap` owns the systemd service entrypoint
- the replaceable `Quasar` worker owns the public port
- updates stage a new worker side-by-side under the Quasar data root
- activation promotes the staged payload into `ManagedRuntime/WebService/<version>/`
  and writes `Updates/active-release.json`
- Bootstrap observes the pointer change, drains the old worker, then starts the managed worker
- the browser and `Quasar.Agent` reconnect after the short listener gap
- Bootstrap self-update drains only when the primary release asset is actually
  newer than the running launcher's normalized release identity

This implies a two-layer deployment:

1. stable lightweight launcher/proxy layer
2. replaceable Quasar worker layer

The current Linux implementation deliberately uses Bootstrap as a launcher, not
as a reverse proxy. Replacing the worker creates a short listener gap, which is
acceptable because `Quasar.Agent` reconnects and managed Magnetar processes run
detached.

Future strict no-downtime rollover would still require Bootstrap to become a
stable proxy/front door and run workers on internal ports.

Practical guarantee:

- browser sessions may briefly reconnect
- `Quasar.Agent` sockets may briefly reconnect
- the supervisor must preserve enough state that reconnect is operationally seamless
- managed DS processes continue running independently during the rollover

### Linux update flow

1. Bootstrap downloads the latest web asset on startup if no usable worker exists
2. Quasar checks GitHub releases every 15 minutes while running
3. new Linux web assets are downloaded into a staged version directory
4. UI notifies admins that the update is queued/staged
5. admin activates the staged UI update from `/settings/updates`
6. activation promotes the staged payload into `ManagedRuntime/WebService/<version>/`
   and writes the active-release pointer
7. Bootstrap drains the old worker without stopping managed servers
8. Bootstrap starts the managed worker on the same port
9. browsers and agents reconnect

### Future proxy update flow

1. download or place a new Quasar release into a staged version directory
2. validate package shape and version metadata
3. start the new worker on an internal staging port
4. wait for health and warm-up
5. switch the stable launcher/proxy target to the new worker
6. stop sending new browser and agent connections to the old worker
7. drain old worker connections for a grace window
8. force remaining old connections to reconnect if needed
9. retire the old worker

### State requirements for rollover

Quasar worker state needed after rollover must not live only in process memory.

At minimum this includes:

- server definitions
- goal state per server
- current active version pointer
- reconciliation-relevant config paths
- enough runtime metadata for the new worker to resume control

Observed live process state can be rebuilt from:

- process inspection
- persisted server definitions
- reconnecting `Quasar.Agent` sessions

## Configuration Management

The current Python Web UI behavior must move into `Quasar`.

This includes:

- DS configuration editing
- Magnetar core configuration editing
- plugin profile editing
- source management

### Authoritative store

Quasar configuration should be file-system backed.

Rationale:

- easy backup and restore
- easy manual inspection
- easy diffing
- simple operator mental model

The authoritative per-server configuration should live in Quasar-managed files on disk.

Recommended format:

- JSON for Quasar-owned server configuration

The JSON does not need to mirror DS XML one-to-one.

It is expected to extend the DS model with Quasar-specific data such as:

- goal state
- server paths
- restart policy
- health policy
- world selection
- launch policy
- Quasar-specific metadata

### Rendered runtime artifacts

DS XML files are runtime artifacts, not the long-term source of truth.

That means:

- Quasar stores authoritative server config as JSON
- Quasar renders the DS-facing XML/config artifacts into the server-specific app-data tree
- Quasar prepares `LastSession.sbl` before launch
- Quasar starts DS against those rendered artifacts

This keeps the DS launch surface compatible with the game while letting Quasar own a richer configuration model.

### Config flow

Config flow should work like this:

1. Quasar stores desired config in JSON on disk
2. Quasar renders effective DS/Magnetar runtime config into the server app-data tree before launch
3. DS starts headless with server-specific paths/arguments
4. `Quasar.Agent` attaches and can request effective config/state from Quasar on startup
5. Quasar can push config updates to the DS where the DS/plugin can apply them dynamically
6. if a change is not dynamically applicable, Quasar marks it as restart-required and reconciliation applies it on restart

So the model is:

- file-backed desired state in Quasar
- rendered runtime artifacts for DS
- runtime config pull on attach/start
- push updates where supported

### Manual access and watchers

Operators should still be able to inspect and edit the Quasar-managed JSON files directly.

Quasar should therefore support:

- manual file-based backup workflows
- file watching on Quasar-owned config files
- validation/reload after external edits

Quasar-managed writes remain authoritative, but operator edits on disk are a supported path rather than something the system fights.

### Write safety

Config writes must be safe.

Required behavior:

- write to a new temporary file first
- fsync/flush as appropriate
- atomically replace or rename over the destination
- never truncate the authoritative file in-place

Atomic swap is the baseline requirement for all Quasar-managed config writes.

### Config history

Quasar should keep past config versions as a safety net.

Required goals:

- diff old vs new
- restore previous known-good config
- inspect when a bad config entered the system

Recommended approach:

- keep the current authoritative JSON file at a stable path
- write timestamped or versioned historical copies alongside it in a history directory
- keep history per server

History retention policy can be simple at first, for example:

- keep the last `N` versions
- or keep all versions within a bounded size/time policy

### Import and compatibility

Existing DS XML may still need to be imported during migration, but that is a migration concern, not the steady-state ownership model.

Steady state should be:

- JSON is authoritative
- Quasar renders DS XML
- DS consumes rendered XML

Important requirement:

- config round-tripping must preserve unknown fields where practical

For migration/import paths, Quasar should avoid silently dropping data from existing DS XML where practical.

But once a server is under Quasar management, the primary model is no longer "round-trip whatever XML happened to be there"; it is "own the desired config in Quasar JSON and render deterministic DS runtime artifacts."

## Security and Trust

First delivery may remain host-local and pragmatic, but the protocol shape should leave room for:

- per-server tokens
- authenticated agent attachment
- future multi-host trust boundaries

Security hardening is not the first blocking stage, but identity and attachment should not be left completely undefined.

## What Is Required Now

Required for the first meaningful delivery:

- `Quasar` as primary supervisor
- `Quasar.Agent` attachment over raw WebSockets
- multiple DS servers on one host
- isolated DS and Magnetar app-data per server
- goal-state reconciliation (`On` / `Off`)
- DS process start/stop/restart supervision
- strong server health monitoring with agent attach grace, heartbeat freshness, uptime policy, and automated recovery
- simulation-frame progress scoring aligned with the dedicated server watcher formula (`deltaFrames / (elapsedSeconds * 60)` versus a configurable minimum threshold)
- `LastSession.sbl` preparation by Quasar
- JSON file-backed authoritative config store
- atomic config writes
- per-server config history
- Blazor Server UI for management
- NLog-based file logging with minimal console output
- bootstrap/setup path from the agent side via `Quasar.Bootstrap`
- neutral light/dark UI theme with persisted preference
- config editing migrated out of Python

## What Can Wait

These can be deferred after the first host-local supervisor release:

- true multi-host federation
- cluster scheduling
- shared-memory local bulk-state channels for future same-host server meshing
- advanced event replay/history
- polished installer packaging
- high-complexity auth models
- fully seamless Quasar worker rollover through a stable launcher/proxy layer

The protocol and IDs should remain compatible with those later additions.

## Implementation Stages

### Stage 1: Naming and ownership correction

- align project, folder, assembly, and solution names with `Quasar` and `Quasar.Agent`
- remove agent-primary ownership assumptions

### Stage 2: Logging and runtime polish

- add NLog-backed supervisor logging
- support text/json file output
- reduce console output to banner, URL, fatal error
- add interactive/service mode detection
- add browser auto-open policy

### Stage 3: Theme and UX shell

- apply neutral black/white/grey MudBlazor theme
- add light/dark mode toggle
- persist theme preference in browser local storage

### Stage 4: Server model and persistence

- define persistent DS server records
- add stable `UniqueName`
- define launch settings, world/config selection, restart policy
- define isolated DS and Magnetar app-data roots per server
- define desired goal state per server

### Stage 5: DS supervisor

- add process start/stop/restart
- add crash monitoring and restart backoff
- add server health monitoring and health-state surfacing
- detect missing/stale `Quasar.Agent` attachment with configurable grace/timeout thresholds
- add simulation-frame progress scoring using the same threshold model as the dedicated server watcher
- add long-uptime warning and recycle policy
- trigger automated recovery when health policy marks a server unhealthy
- pass supervisor endpoint and server identity into launched DS processes
- reconcile actual state back to desired `On` / `Off` state
- prepare `LastSession.sbl` before launch
- apply headless / `-nosplash` policy correctly per platform and launch mode

### Stage 6: Agent bootstrap correction

- replace direct agent-side host spawn with `Quasar.Bootstrap`
- keep agent-side ensure-running flow
- preserve raw WebSocket attachment behavior

### Stage 7: Config migration

- migrate DS config editing from `webui/`
- migrate Magnetar config/profile/source editing from `webui/`
- define Quasar JSON config schemas
- render DS XML/runtime artifacts from Quasar JSON
- add atomic writes and per-server config history
- add file watching and reload for manual operator edits
- keep XML import/migration tolerant where practical

### Stage 8: Self-update staging and cutover

- add staged release management
- add stable active-release pointer
- add stable launcher/proxy ownership of the public endpoint
- add worker warm-up and cutover flow
- add graceful drain of old workers
- keep DS supervision state persistent across worker turnover

### Stage 9: Future mesh transport

- add transport abstractions for control plane vs bulk-state plane
- keep WebSocket control plane intact
- add optional shared-memory bulk-state channels for same-host meshing
- let Quasar coordinate channel setup without becoming the bulk-data relay

### Stage 10: UI completion

- complete management views around servers, configs, logs, lifecycle, and restart policy

### Stage 11: Cleanup

- remove obsolete `webui/`
- remove stale REST/plugin documentation
- rename projects and docs to final product names where appropriate

## Current Repository Status

As of this document:

- shared protocol exists
- a first Blazor Server host exists
- a first raw WebSocket `Quasar.Agent` path exists
- `Quasar.Bootstrap` exists as an ensure-running helper
- Quasar logging is now separated from console noise
- per-server JSON-backed server definitions exist
- atomic config history/versioning groundwork exists for server definitions
- first desired goal-state reconciliation exists
- first process supervision exists for start/stop/restart and per-server logs
- first health-monitoring and auto-recovery pass exists for agent attach grace, heartbeat freshness, simulation-frame progress scoring aligned with the DS watcher, and uptime-based warning/recycle policy
- initial runtime launch preparation now exists for isolated app-data roots, runtime config sync, `LastSession.sbl`, and enforced headless launch shaping
- neutral light/dark theming exists with local-storage persistence
- config editing is now migrated out of Python into Quasar-managed JSON profiles and rendered runtime artifacts
- file watching/reload now exists for manual edits to Quasar-managed server/profile JSON
- backup/restore now exists as versioned ZIP archives for Quasar configuration, whole server data, and world-only data. Configuration backups cover servers, config profiles, world-template definitions, branding, and singleton settings files, with manual download/upload and semantic-version compatibility checks. Automatic backup rules are configured separately for Quasar config, server backups, and world backups, each with its own schedule and retention. Server backups include server config and game data; world backups restore world files while keeping existing config, using the latest Space Engineers `Backup` snapshot when present so backups can be taken while servers run.
- per-server CPU affinity pinning now exists (cpuset strings applied via `taskset` on Linux and `Process.ProcessorAffinity` on Windows), enforced by the supervisor on process start and reconcile alongside process priority
- per-server managed .NET runtime selection now exists on Windows, where Quasar installs both Magnetar builds side-by-side (`MagnetarInterim.exe` on .NET 10, the default, and `MagnetarLegacy.exe` on .NET Framework 4.8) and the runtime resolver launches the build chosen by `DedicatedServerDefinition.ManagedRuntime`; non-Windows hosts always run the .NET 10 build
- runtime config preparation now derives a unique `SteamPort` (`ServerPort + 1000`) and `RemoteApiPort` (`ServerPort + 2000`) per server so multiple servers co-hosted on one machine never collide on the SE defaults (8766 / 8080)
- server naming across the UI now consistently prefers the operator-configured `DedicatedServerDefinition.DisplayName` over the agent's in-game `ConfigDedicated.ServerName` (the analytics filters/legends, Discord per-server panels, the entities/plugins server selectors, the players list, and the plugin log panel all resolve names this way, falling back to the live agent name and then the unique name)
- the Analytics dashboard renders metrics as client-side uPlot canvas charts: the browser fetches compact, timeline-aligned series from a JSON HTTP endpoint (`/api/analytics/series`, backed by `AnalyticsSeriesService`, which selects the RRD consolidation tier by span — raw ≤2h, 1-minute ≤24h, 1-hour beyond — and drops empty buckets); profiler game-loop timing buckets (frame, update, physics, scripts, network, other) and deep profiler top grids/entity types are surfaced as additional chart panels through the same endpoint via `ProfilerAnalyticsMetrics` and `ProfilerEntryAnalyticsMetrics`; the same page also edits each server/agent profiler mode and pushes live changes through `ServerCommandType.SetProfilerMode`; the previous inline `ProfilerSummaryCard` tables and the `blocks`/`floating-objects` scalar metrics have been removed
- deep per-server profiler telemetry now exists: `Quasar.Agent` runs a continuous in-process profiler with `SafeContinuous` enabled by default, with per-server persisted `AgentProfilerMode` values and a global `Quasar:AgentProfilerMode` / `QUASAR_AGENT_PROFILER_MODE` fallback for older definitions. Safe mode uses Harmony prefix/postfix timing only for named high-level paths: frame/update, programmable-block script, physics, replication/network/session, GPS, and block-limit work. It deliberately avoids broad entity update method patching and detailed network-event hooks so the always-on default stays low overhead. Deep mode adds detailed network-event method hooks plus Magnetar-compatible Harmony IL call-site transpilers for `MySession.Update` / `UpdateComponents`, session component calls, replication simulation, entity update dispatch, parallel waits/callbacks, and Havok physics stepping internals. Runtime mode changes reconfigure Harmony patches so Safe, Deep, and Off can be selected without restarting the server. Hot-path measurements use numeric call-site ids and rolling accumulators, split main-thread vs off-thread time, and publish one-second windows with bounded top-lists for grids, scripts, entity types, system methods, physics detail, and network/replication/session work where the active patch depth can observe them. Patch failures are logged and the agent keeps the remaining profiler surface; entity call-site misses stay at high-level timing rather than adding broad method wrapping. Each `ProfilerSnapshot` rides the regular agent snapshot, is validated, and is kept in a small recent in-memory `ProfilerStoreService` ring (~720 samples per server, about 12 minutes at one snapshot per second), then surfaced on the Analytics page as game-loop timing and top grid/entity-type chart panels
- Discord per-server options now include simspeed alert rules. `DiscordSimSpeedAlertService` evaluates fresh raw metric samples for connected/running agents on the registry change path, sending alerts through the configured simspeed channel or the server's analytics channel. Baseline rules detect sharp sample-to-sample drops across every unseen raw sample pair and sustained low average simspeed, and the Discord page exposes thresholds, windows, cooldowns, and per-rule enable switches.
- a unified GitHub-release-based update/publish pipeline now exists covering both Linux and Windows in a single combined release (`.github/workflows/release.yml`): each build produces `quasar-linux-x64.tar.gz` / `quasar-web-linux-x64.tar.gz` (Linux) and `quasar-win-x64.zip` / `quasar-web-win-x64.zip` (Windows) under one tag; tag pushes and `main` publish full releases while pull requests publish draft prereleases; the release carries one combined `SHA256SUMS` covering every archive; release identity is normalized from `AssemblyInformationalVersion` and the active-release pointer (not numeric `AssemblyVersion`); four-part build tags such as `0.1.2.37` are canonical and numeric prerelease aliases such as `0.1.2-37` normalize to them; every downloaded asset is verified against `SHA256SUMS`; the UI stages web updates and queues them for explicit activation from `/settings/updates`; Bootstrap self-upgrades from the launcher stream only when an actually-newer asset appears (see [Linux Deployment and Updates](LinuxDeploymentAndUpdates.md) and [Windows Deployment and Updates](WindowsDeploymentAndUpdates.md))
- `Quasar.Bootstrap` runs as the stable launcher that owns the public port on both Linux (systemd service) and Windows (Scheduled Task): it activates web releases through the `Updates/active-release.json` pointer after staged payloads are promoted into `ManagedRuntime/WebService/<version>/`, and performs worker cutover by draining the old worker and starting the managed one on the same port — a launcher, not yet a reverse proxy — so the public endpoint stays stable across the short listener gap while managed Magnetar servers keep running; on Linux the launcher exits with code 75 so systemd restarts it; on Windows the launcher spawns a detached replacement `Quasar.exe serve --quiet` and exits 0, with the Scheduled Task restart-on-failure as the safety net
- Windows deployment exists via `install.ps1`/`uninstall.ps1`: `install.ps1` publishes to `%ProgramFiles%\Quasar` and registers a Scheduled Task (`Quasar`) that starts at boot and restarts the launcher on failure; the task runs with `QUASAR_MODE=Service` and `QUASAR_OPEN_BROWSER_ON_START=false` mirroring the Linux systemd environment
- staged relaunch now persists supervisor runtime state so managed DS processes survive worker turnover
- obsolete `webui/` is removed from the repository
- per-server isolated app-data path handling groundwork exists
- Windows Service hosting is intentionally out of scope
- future shared-memory local bulk-state transport is planned but not implemented

This document supersedes older assumptions that the DS plugin might directly own the long-running web host lifecycle.
