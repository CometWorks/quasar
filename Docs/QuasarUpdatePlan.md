# Quasar Automatic Update Plan

This note captures the recommended implementation path for automatic updates and seamless rollover for both `Quasar` and `Quasar.Agent`.

It is written against the current repository state:

- `Quasar.Bootstrap` now owns the stable public endpoint and worker reverse-proxy flow.
- the active release pointer is now used for worker activation and cutover.
- local release activation now drains the old worker and hands DS supervision state to the new worker.
- `Quasar.Agent` can already trigger `Quasar.Bootstrap` when the supervisor is missing.
- automatic bundle download, release packaging, and feed polling are still future work.

## Goal

Provide automatic update behavior with these practical guarantees:

- Dedicated Server instances keep running during a `Quasar` supervisor upgrade.
- the control-plane URL stays stable.
- browsers and agents reconnect automatically after worker turnover.
- `Quasar.Agent` updates can be rolled out without manual reinstall work.

Non-goal:

- preserving an already-live Blazor Server circuit across version rollover.
- preserving an already-live raw WebSocket agent connection across worker replacement.

## Core Design

Do not let the `Quasar` worker overwrite or restart itself in place.

Use a two-layer model:

1. `Quasar.Bootstrap` becomes the persistent launcher and stable front door.
2. `Quasar` becomes a replaceable worker running on an internal loopback port.

### Launcher responsibilities

- own the public URL
- own the local manifest and active release pointer
- start, stop, and monitor workers
- reverse proxy browser traffic
- reverse proxy `/ws/agent` WebSocket traffic
- stage, activate, drain, and roll back releases

### Worker responsibilities

- UI and operator workflows
- DS supervision
- state persistence
- update check UI and operator commands
- requesting stage/apply actions from the launcher

## Why This Was Required

The old worker-owned public port created a listener gap during replacement. The current launcher/worker split removes that gap by keeping the public endpoint in `Quasar.Bootstrap` while `Quasar` turns over underneath it.

## Release Bundle Format

Use one versioned release bundle containing:

- `Quasar` worker binaries
- `Quasar.Bootstrap` binaries
- `Quasar.Agent` binaries
- release manifest

The release manifest should include:

- version
- release channel
- compatibility info
- file checksums
- optional minimum launcher version
- optional minimum worker version

Because repo ownership will change soon, feed configuration must not be hardcoded to one GitHub owner or repo. Keep update source configurable.

## GitHub Pipeline and Artifact Work

There are currently no GitHub Actions workflows in this repository. They need to be added as part of the update system work, because the automatic updater depends on a predictable release bundle format and stable artifact naming.

### Required workflow split

Use at least two workflows:

1. `ci.yml`
2. `release.yml`

### `ci.yml`

Purpose:

- restore and build the solution on every PR and protected-branch push
- catch packaging regressions before release
- validate release-manifest generation logic

Minimum checks:

- build `Quasar`
- build `Quasar.Bootstrap`
- build `Magnetar.Protocol`
- build `Quasar.Agent` on a runner that has the required DS reference assemblies
- validate release bundle layout in a smoke-test packaging step

### `release.yml`

Purpose:

- produce versioned update artifacts
- attach them to GitHub Releases
- emit checksums and release manifest files consumed by the updater

Trigger options:

- tag push for release tags
- manual dispatch for pre-release and recovery publishing

### Agent build prerequisite

`Quasar.Agent` does not currently build in a clean GitHub runner by default.

Its project file references Space Engineers dedicated-server assemblies through `$(DS64)\*.dll`, which today assumes a local machine install. That is fine for local development, but not for CI.

Before the pipeline is reliable, pick one of these approaches:

1. maintain a CI-only reference pack containing the required compile-time DS assemblies
2. fetch that reference pack during the workflow from a controlled private artifact location
3. check in a legally-approved reference assembly pack into the repository or a companion repository

The workflow should not depend on a runner having Space Engineers preinstalled.

### Artifact outputs

The release workflow should emit both raw build artifacts and assembled updater artifacts.

Raw build artifacts:

- `quasar-worker-win-x64`
- `quasar-bootstrap-win-x64`
- `quasar-worker-linux-x64`
- `quasar-bootstrap-linux-x64`
- `quasar-agent`

Assembled updater artifacts:

- `quasar-release-win-x64-vX.Y.Z.zip`
- `quasar-release-linux-x64-vX.Y.Z.zip`
- `quasar-release-manifest-vX.Y.Z.json`
- `quasar-release-checksums-vX.Y.Z.txt`

The updater should consume the assembled release bundle, not a loose collection of individual project outputs.

### Publishing shape

For `Quasar` and `Quasar.Bootstrap`, prefer published host-specific outputs rather than plain build output.

Recommended initial targets:

- `win-x64`
- `linux-x64`

For the first pass, self-contained publish is the most operationally predictable option for the main `Quasar` worker, because it removes dependency on a preinstalled .NET runtime on the target host.

`Quasar.Bootstrap` is a good candidate for framework-dependent single-file publish instead: one launcher file per RID, smaller payload, host runtime still required.

`Quasar.Agent` should be packaged as a managed plugin payload inside the release bundle, separate from worker RID differences.

### Bundle assembly step

The release workflow should have an explicit assembly step that creates the final updater bundle layout.

That step should:

- place worker files under a versioned runtime directory layout
- include launcher payload
- include agent payload
- generate release manifest JSON
- generate checksums for every file in the bundle
- stamp version, channel, and compatibility metadata

The bundle manifest should include:

- release version
- release channel
- target host OS / architecture
- worker version
- launcher version
- agent version
- protocol compatibility version
- file list with checksums
- publication timestamp

### GitHub Release publication

The release workflow should upload the assembled artifacts to a GitHub Release, not just keep them as ephemeral workflow artifacts.

Recommended publication outputs:

- one release bundle per host RID
- one manifest per release
- one checksum file per release

Optional next step:

- publish a channel pointer such as `stable.json` and `prerelease.json` that the launcher can poll

That channel pointer should not be tied to a hardcoded repository owner. The update source URL should remain configurable so repo transfer to the official org does not require updater redesign.

### Retention and rollback

Workflow artifact retention alone is not enough.

GitHub Releases must keep enough historical bundles for rollback. At minimum:

- current stable
- previous stable
- latest prerelease if prerelease channel is used

The updater design already assumes side-by-side staged versions and rollback. The release pipeline needs to preserve the artifacts that make rollback possible.

### Security and safety guardrails

At minimum, the workflows should:

- pin action versions
- use concurrency control so two release jobs do not publish the same channel at once
- produce checksums as part of the official release output
- gate stable-channel publication behind a protected branch or manual approval

Later hardening can add:

- signed release manifests
- artifact provenance / attestations
- code-signing for Windows executables

## Quasar Update Flow

Implement this in two phases.

### Phase A: local staged install

First support local staged update and activation:

1. place release bundle in staging
2. validate bundle shape and manifest
3. extract into versioned release directory
4. start new worker on a staging loopback port
5. wait for health and warm-up
6. atomically switch launcher proxy target
7. stop routing new connections to old worker
8. drain old worker for a grace window
9. force-retire old worker if needed
10. retain rollback target until the new worker proves stable

Do this before building automatic download logic.

### Phase B: automatic feed

After local staging works:

- poll configured release feed
- download release bundle
- verify checksum
- stage locally
- ask launcher to activate

The first feed can be GitHub Releases or a repo-hosted manifest, but the launcher should treat it as a generic release source.

## Agent Update Strategy

Do not try to live hot-swap `Quasar.Agent` inside the DS process.

`Quasar.Agent` is a DS plugin. Realistically, an update means:

- stage new agent bits
- mark desired agent version per instance
- deploy into the instance plugin location
- apply on next DS restart
- optionally orchestrate rolling restarts one instance at a time

So "seamless" for agent update means:

- `Quasar` control plane stays up
- other DS instances keep running
- the updated instance reconnects automatically after restart
- there is no manual reinstall path

## Repo-Specific Blocker

Before true agent auto-update exists, `Quasar` must own agent deployment.

Right now `DedicatedServerRuntimePreparer` prepares DS and Magnetar config, but it does not appear to deploy `Quasar.Agent` into a Quasar-managed plugin location for each instance.

That needs to be added first.

Required additions:

- Quasar-managed agent package directory
- per-instance desired agent version
- runtime preparation that deploys `Quasar.Agent`
- instance state showing update pending / applied / failed
- version comparison beyond plain assembly version

The existing `AgentHello.PluginVersion` field is useful for reporting, but protocol compatibility should be tracked separately from simple plugin version.

## Suggested Implementation Order

1. turn `Quasar.Bootstrap` into a long-lived launcher and reverse proxy
2. move manifest and active-release ownership from worker to launcher
3. add local release bundle staging, activation, and rollback
4. add worker warm-up and cutover flow
5. add old-worker drain behavior
6. add Quasar-owned `Quasar.Agent` deployment path
7. add per-instance desired agent version and rolling restart policy
8. add automatic download and feed polling last

This order matters. Building auto-download first would create a downloader without a safe activation path.

## Practical Result

With this design:

- the public control-plane endpoint stays continuously available
- browsers may reconnect briefly during cutover
- agents may reconnect briefly during cutover
- DS instances continue running through worker replacement
- agent upgrades become operationally automatic, even though each DS instance still needs a restart to load new plugin bits
