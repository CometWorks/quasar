# Phase 4 Quasar integration — continuation plan

Updated 2026-09-13. Step 1 complete: upstream baseline refresh and plan reconciliation.
Step 2 has not started.

## Plan authority

The local sibling `clustering-plan` checkout on `main` is the authoritative design,
verified at `6621d1b7324a6b7da4285f87b354ef974fcb0a9f` after its latest pull.
Upstream takes priority over all earlier integration-branch decisions. Keep the
upstream checkout unchanged; record Quasar implementation gaps and progress here.

The [upstream Quasar design](https://git.cometworks.se/CometWorks/clustering-plan/src/commit/6621d1b7324a6b7da4285f87b354ef974fcb0a9f/Plan/Quasar.md)
§§2/5 makes the Gateway/Registry the decider and Quasar the phase 4 executor:

- Retain NodePlan actualization and per-host process execution. The registry chooses
  placement, drain, rotation and WA designation; Quasar executes the desired process set.
- Disable independent standalone restart/health/uptime decisions for live cluster nodes.
- Start the Gateway first and stop it last, with registry-mediated graceful transitions.
- Preserve running nodes across Quasar restart or outage; Quasar is not a data-plane dependency.
- Expose one managed cluster through ordinary server/config/world workflows, with
  registry-authoritative cluster metrics and Agent process telemetry.
- Retain full-downtime updates, offline world conversion and scriptable admin/executor parity.

The [upstream phase 3 contract scope](https://git.cometworks.se/CometWorks/clustering-plan/src/commit/6621d1b7324a6b7da4285f87b354ef974fcb0a9f/Plan/Clustering.md)
§13 assigns reachable administration to `/admin/v1` and down-state lifecycle to the
packaged `cluster` CLI. Integrate those concrete entrypoints with the phase 4 executor
model when available. This is not a blanket prohibition on Quasar process execution.

The [phase 4 scenario gate](https://git.cometworks.se/CometWorks/clustering-plan/src/commit/6621d1b7324a6b7da4285f87b354ef974fcb0a9f/Plan/TestPlan.md)
§2.4 permits Quasar development before phase 3 exits. Stable phase 3 suites gate live
acceptance. P4-QSR-01…06 remain the upstream acceptance list: parity, convergence,
non-dependency, upgrades, monitoring and conversion.

Earlier branch-only declarations that the standalone cluster must own every executor,
that Quasar must never invoke conversion tools, or that additional phase 4 test IDs and
stock-DS migration prerequisites are locked do not override upstream. The old gap audit
is historical implementation evidence. The previous version of this continuation plan
incorrectly treated that audit as higher authority; the sequence below follows upstream.

## Reviewed implementation baselines

| Component | Revision | Use |
| --- | --- | --- |
| Local `clustering-plan` main | `6621d1b` | Authoritative plan; clean checkout |
| Quasar integration before merge | `69aa510` | Existing cluster implementation |
| Quasar main merged in step 1 | `e50ca11` | Current standalone product, packaging and shutdown fixes |
| Gateway Forgejo origin/main | `0546d1a` | Current concrete operator API, inspected from fetched source |
| Retained Gateway integration | `8067a57` | Historical prototype; not the current API baseline |

The Quasar merge preserves the existing `phase4/quasar-integration` branch and its
history. The older `agent/phase4-headless-api` branch is not a newer continuation.
The original Quasar worktree remains separate from `Quasar-phase4`.

## Step 1 — refresh the integration baseline

Merged Quasar main at `e50ca11`, including its 25 commits absent from integration.
The five textual conflicts are resolved as follows:

| File | Resolution |
| --- | --- |
| `Docs/Configuration.md` | Keep cluster documentation and upstream initial-admin provisioning instructions |
| `Quasar.Bootstrap/Quasar.Bootstrap.csproj` | Keep cluster contract/host packaging and upstream shared GitHub retry handler |
| `Quasar.Tests/RbacAuthorizationTests.cs` | Keep initial-admin tests and cluster-page authorization coverage |
| `Quasar/Components/Layout/NavMenu.razor` | Keep cluster Tools/Hosts navigation and upstream policy-protected navigation |
| `Quasar/Services/Auth/RbacConfigCatalog.cs` | Keep upstream initial-admin provisioning and last-admin protection |

The combined test project references both Bootstrap and worker, which each compile
`GitHubRetryHandler`. Alias the Bootstrap project reference and import that alias in
`ClusterCliTests` to remove the duplicate-type ambiguity without changing runtime code.
Retain the upstream executor architecture. No Gateway/DirectTransport/ClusterRuntime
implementation history is merged as part of this baseline refresh.

Validation results are recorded below. Contract migration remains step 2.

## Remaining implementation gaps

1. Quasar pins the historical `0.5.0` admin-contract package; current Forgejo source
   declares `0.3.0` of a different lineage. Both use wire protocol v1. Select a reproducible
   contract artifact matching the standalone source; package version order or the protocol
   header alone does not prove compatibility. Do not overwrite a published version.
2. `ClusterGatewayClient` omits Gateway `Idempotency-Key` headers. Current config writes
   require `AdminConfigUpdate` with an expected revision, and lifecycle mutations return
   durable `AdminOperation` records instead of prototype lifecycle results.
3. `ClusterOperationStore` treats a returned envelope as completion; a Gateway operation
   may still be running. Persist its ID/key and reconcile terminal state across lost
   responses and Quasar restart.
4. `Quasar.Host` implements the planned executor role but targets the older contract.
   Verify plan/report/credential support against current Gateway and packaging before
   expanding it. Observation of an existing cluster should not require provisioning a host.
5. Current Gateway implements GET/PUT `/admin/v1/handover-config` outside its advertised
   `AdminProtocol.Operations` and thin C# CLI list. Track this parity gap upstream before
   relying on it for an advanced editor; basic integration can proceed independently.
6. Gateway `9512282` removed node-link restart patches after regressions. Reusing executor
   code does not justify replaying those data-plane patches into current Gateway.

Upstream stabilization also changes identity allocation to registry-issued ranges with
separate 64-bit per-slot incarnation epochs, adds hot-spare policy, and excludes conversion
of worlds created under the retired 8-bit epoch scheme. Use these rules in identity,
capacity and conversion work. Placement and handover algorithms remain registry/runtime
responsibilities. The `~/HotSpareNodes.md` reference is absent from the plan tree; verify
concrete API support rather than inventing its missing details.

## Step 2 — align the contract and observe an existing cluster

Pin the current standalone contract, using an isolated development artifact/feed if package
publication is not ready. Extend the existing client and application services for
capabilities, health, status, fleet/WA, recovery readiness, snapshots, configuration,
operations and events. Render Gateway-computed health, reason codes and admission state.
Represent unsupported features, stale observations, unavailable Gateway and query-only
access explicitly. Keep the executor; make observation independent of host provisioning.

Acceptance: current wire fixtures deserialize and map correctly; query-only credentials
cannot mutate; unavailable does not mean stopped. This slice needs no deployment package.

## Step 3 — durable operations and executor alignment

Forward stable idempotency keys, track Gateway operations to completion and recover by
ID/key after restart. Route UI and headless actions through the same application methods.
Implement CAS config, save/shutdown and supported administration against the current API.
Align executor plan polling, spawn reports, exact-incarnation kills and Gateway lifecycle
with the upstream design and available package entrypoints. Keep registry decisions
separate from process execution; preserve standalone supervision.

Acceptance: replay does not repeat effects, changed requests conflict, stale revisions fail,
202 is not completion, authorization holds, and executor restart adopts existing processes
without churn. Missing public executor contracts are explicit integration gaps.

## Step 4 — Agent and management expansion

Complete cluster/slot/node/epoch identity and key plugin/config/stats/log observations by
current incarnation. Reuse existing plugin and telemetry services. Expand fleet/player/event
panels and normal server/profile/world workflows without promoting branch-only policy or
Agent requirements into upstream mandates.

Acceptance: replacement nodes cannot inherit stale Agent state; registry remains cluster
truth; telemetry disconnection does not trigger independent node lifecycle decisions.

## Step 5 — packaged lifecycle, backups and recovery

Use the package's documented start/import/restore/teardown entrypoints in the upstream
executor and UI workflows. Verify package identity, run roots, credentials, noninteractive
arguments, results and re-adoption before live use. Reuse cluster conversion tooling and
world-export artifacts with existing backup/retention services. Respect source preservation,
world compatibility and actual quiescent/crash-consistent labels. SharedPath is the current
Gateway artifact implementation, not an immutable product-wide restriction.

Acceptance: start → serve → clean down → warm start, verified backup retrieval, and
conversion parity with the standalone tool. Live tests need a runnable package and stable
phase 3 fixtures. Package internals must not be guessed from test harness layouts.

## Step 6 — full-downtime updates and phase 4 acceptance

Drain, activate a complete compatible bundle and restart through the executor/package
interface. Refuse partial version combinations. Run upstream P4-QSR-01…06 and unchanged
phase 1–3 regressions; verify Quasar restart/outage during idle, drain and rotation.
Additional headless/recovery coverage can supplement the upstream scenarios without
renumbering or replacing their acceptance contract.

## Validation

- Before merge: 55 focused prototype tests passed.
- After merge and Bootstrap reference fix: all 192 `Quasar.Tests` tests passed; none skipped.
- `dotnet build Quasar.sln --verbosity minimal` succeeded with zero errors. Existing
  assembly-reference conflict and NuGet advisory warnings remain.
- `dotnet Quasar.Host/bin/Debug/net10.0/Quasar.Host.dll --self-test` passed, covering
  command handling, process start/re-adoption, exact process kills and bundle checks.
- `python3 scripts/test-bootstrap-shutdown.py` passed for foreground and service modes:
  stale shutdown requests, crash restart and successful intentional shutdown. This uses
  an isolated fake HTTP worker, not the Quasar web service.

These checks validate the refreshed implementation baseline, not current-Gateway wire
compatibility or live phase 4 acceptance. No Quasar web service or game cluster was launched.
