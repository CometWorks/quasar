# Cluster integration verification — 2026-09-20

Implementation covers remaining stage 3 through stage 7 on Quasar's
`phase4/quasar-integration` branch. Baseline `8edd33a` is pushed in Quasar #137;
subsequent review fixes remain local on that branch. Upstream reviews:

- [Magnetar #57](https://github.com/CometWorks/magnetar/pull/57), `8f8d44f`.
- [cluster #12](https://github.com/CometWorks/cluster/pull/12), `b0a8427`.
- [generic plan #4](https://git.cometworks.se/CometWorks/clustering-plan/pulls/4), `06a5e71`.

## Passing implementation checks

| Check | Evidence |
| --- | --- |
| Quasar services/API/CLI | Full suite, 338 tests; includes update restart/partial activation, rejected preflight without downtime, rollback, exact workflow wait, snapshot cleanup replay/lifecycle fence and six artifact inventory/corruption cases. |
| Magnetar PluginSdk | 139 tests; CAS/conflict/tombstone/restore, durable standalone store, missing-provider lifecycle, queued dispatch and observer isolation. |
| Magnetar launcher/example | Build succeeds; example uses the public consumer API. |
| Gateway/node/WA | Build succeeds; Gateway self-tests include lifecycle restart replay and old-owner CAS replay/new-write fencing. |
| Release package | Fresh complete build from committed sources; packaged Gateway/admin CLI self-tests pass. |
| Python CLI/generator | 30 tests pass; includes scoped Gateway bearer and SDK rejection/preparation checks. |
| Quasar Host | Inert-process self-test passes, plus portable preparation/import/tamper script. |
| Plugin bundle exporter | 2 tests pass. |
| Source/binary pins | SDK/protocol hash checks and byte-for-byte vendored admin contracts match. |

Existing game reference/NuGet advisory warnings remain in Quasar test builds. No
Quasar web service or live game cluster was launched by this implementation run.
The Linux durability path was exercised; Windows power-failure behavior was not tested.

## Review artifact

Complete archive: `cluster-quasar-feedback-package/ClusterForLinux-1.1.0.tar.gz`
(sibling to the Quasar checkout). SHA-256:
`b69d42caaaaff081cd653c1f73f0d889908b371099213091be4c3aa5e0fc24fd`.

Compiled PluginSdk SHA-256: `a24d6245b362b90bdfe15ed5442c7f32ddb26bff324406627c2213aecea525e3`.
The review archive is locally built, not a published upstream release. Never modify
it to satisfy deployment capability checks; build a new coordinated package instead.

Detailed logs are retained in the sibling `quasar-integration-evidence/2026-09-19/`
directory, with review-fix logs in `quasar-integration-evidence/2026-09-20/`. The current [integration plan](Phase4IntegrationPlan.md) records authority,
workflow semantics and acceptance gates.

## Live acceptance prerequisites

The upstream suite's read-only doctor ran against the review package. It reports
missing Pulsar launcher/compiler/profile, Remote API Python module and `httpx` in the
selected interpreter. Node compatibility caches are not prewarmed, the server build
has no recorded live log, and the planet fixture is absent. No configured second Host
or retail Steam-client Host is available to that suite.

Complete those test-host prerequisites before the full packaged P4/SDK/outage/upgrade
and phase 1–3 regression run. These tests were deliberately separated from implementation
checks. Actual provider startup, end-to-end game behavior and Quasar/Agent outage
acceptance are **not proven** by the passing checks above. Coordinated upstream
merge/releases and live acceptance remain required before first post-beta release.

## PR #12 review fixes

Commit `5d2541c` adds explicit stopped-fleet recovery for dirty Down and persisted
Serving. Gateway tests prove old node fencing, retained character/plugin records and
store versions, unchanged revision/inventory, and replay preserving subsequent drains.
Quasar tests prove refusal before Gateway stop when a Host is not ready, a second fleet
check after stopping, successful retry with a new operation key, retained deployment,
no fabricated clean proof and replay without reversing a later Off goal. Host self-tests
reject unknown in-flight launches and wrong attachment hashes.

Other regressions cover steady-state executor polls leaving unrelated WAL mutations
coalesced, managed legacy executor refusal, and actual SDK metadata version/capability
rejection. Release, CLI and managed preparation enforce compatible SDK inputs. The
complete package built from this commit; its Gateway and admin CLI self-tests pass.
Both role plugins build without errors. Vendored DTOs match upstream byte-for-byte.

Persistence failure intentionally stays fail-closed (503, paused relay, process alive).
Control-health supervision and storage repair/restart are operational requirements.
Compatible Magnetar release and full live acceptance remain mandatory; these changes
were not tested with running Space Engineers nodes or the Quasar web service.

## Guided conversion checks

The standalone/cluster conversion UI, API and CLI are implemented on this branch;
see [Cluster Conversion](ClusterConversion.md) for supported behavior and limits.
The full Quasar suite passes 333 tests. New checks cover historical SDK configuration
persistence, exact stopped-server publication/rollback, forward stop/review/reservation
fences, immutable conversion IDs and failed-operation replay, reverse clean-lifecycle
requirements, source commit/inventory matching, regular seed-slot placement, conversion
route permissions and CLI mutations. World-transfer checks reject traversal, links,
duplicate entries and corrupted payloads while preserving source files. Native snapshot
checks assemble verified copies from multiple Hosts and reject a wrong lifecycle or
corrupted retained archive; unused spare-node stores are skipped.

Quasar and Quasar.Host build successfully; the offline Host portable
preparation/import/tamper script passes. Evidence logs are in
`quasar-integration-evidence/2026-09-20/conversion/` beside the checkout.
No browser interaction, running game conversion, or live player cutover was tested.
The passing implementation checks do not establish end-to-end conversion parity.
Reverse plugin settings are preserved as a reviewable export; arbitrary plugin-owned
configuration files and shared/private storage need explicit reapplication/migration.

Generic plan PR #4 was rebased onto `c1c35b9` after #3 merged. New head `bcc46c9`
preserves the `HotSpareNodes.md` link correction and retired checkout-sync test notice;
the rebased patch matches the original by `git range-diff`.

## Runtime feedback fixes — 2026-09-20

Upstream Magnetar `8f8d44f` and cluster `b0a8427` are pushed to their existing PRs.
The current complete package was built from those commits and passed its packaged
Gateway and admin CLI self-tests. Cross-repository protocol equality and SDK metadata
validation pass; the PR-only SDK build uses an exact public source pin. Public release
packaging still requires the compatible released Magnetar SDK.

WA readiness now waits for checkpoint/ledger restore and game-session readiness.
An offline test exercises 48 transition combinations; no live WA startup was rerun.
Authenticated plugin rejection, admission-versus-relay behavior and held config-drift
slots have regression coverage. SDK tests cover lazy/nonfatal standalone storage,
loader names with spaces, per-plugin quotas, numeric equivalence and SDK-loaded
private/static/multiple config types. Quasar's 338 tests include per-type conversion,
editor sibling preservation and immutable historical configuration snapshots.

Plugin WAL tests cover bounded journal records, payload recovery, torn tails, corrupt
CRC rejection, replay without rate-budget consumption, full state following a delta,
and published snapshots with an old WAL. Forty 32 KiB mutations produce bounded
individual records below 48 KiB; a subsequent small mutation stays below 2 KiB despite
unrelated pending Registry changes. This proves bounded serialization size, not a live
latency target: fsync still holds the Registry lock and rollover still checkpoints.

Registry storage declares v2 for CGR3 records. Old readers cannot consume them; managed
format-map gates prevent silent activation/restore/downgrade. No in-place managed
v1-to-v2 migrator is provided. Unmanaged adoption uses stopped world conversion into a
fresh managed deployment with explicit plugin-data migration, never Registry deletion.

Quasar Host build, inert-process self-test and preparation/import/tamper script pass.
Logs are retained under `quasar-integration-evidence/2026-09-20/feedback-fixes/` beside
this checkout. Magnetar's Linux/Windows PR builds and cluster's contract, package-build and release
workflow jobs pass. Packaged game behavior, live
conversion, drift observation and storage latency remain live acceptance gates.
