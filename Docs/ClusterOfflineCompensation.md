# Offline partitions and compensation

Open a cluster, then **Nodes & players → Partitions**. Refresh reads Registry placement,
offline rows and the shared rate cache. Inspect reads a profile from the selected owner or
an advertised live holder of the exact offline Final. Browsing, scanning, refresh and
prediction do not wake, claim, load or save a partition.

The table shows profile readiness, model confidence, unknown reasons and remote-use
presence. Remote users prevent ordinary offlining and clientless movement. A `remote-use`
conflict means another node is using the partition remotely. Wait for that use to end;
the panel does not force movement or bypass game access checks.

Viewer and Editor can inspect the data. **Profile**, **Dry run** and **Clear this key**
require the authenticated Administrator role in Quasar. A `cluster.manage` service
credential alone does not grant this role. The Gateway credential must also have manage
scope for mutations. Existing cluster management permissions are unchanged.

## Profile and prediction

Select an online partition with a current owner and press **Profile** to deliberately
advance its real profiling pass under the cluster's normal policy. This does not request
a wake. Profiling readiness can remain incomplete; refresh and inspect again.

For an offline partition, select one of the exact Final holders and enter **0–168 game
hours**, including fractional hours. Press **Dry run (prediction only)**. Quasar rejects
empty, nonfinite, negative and oversized intervals. The Gateway resolves the selected
registered node ID, verifies ownership or the exact Final and rechecks it after reading.
The caller cannot supply a file path or node URL. A missing holder, changed Final, legacy
save without profiles or unsupported node produces an unavailable, empty or conflict
state. Refresh before retrying a changed selection.

Results remain predictions. **Exact**, **Approximate**, **Frozen** and **Creative** are
the source model's confidence labels, not proof of a native game differential. Unknown
blocks and skipped content mismatches remain explicit. A capped interval reports only
the modeled game hours; the authority end marker can include uncompensated excess time.
Authority stamps use 16 ms clock units. They are not wall-clock timestamps. Zone timers
use native 60 Hz frames and are displayed as seconds.

The result tables show stored energy change in MWh, per-producer delivered electricity
and fuel, per-consumer electricity, storage input, production recipe units, inventory
definition quantities and chip counts. Production totals are recipe units, not a second
inventory credit; the net inventory table includes production inputs and consumed fuel.
All modeled consumers, including production and player safe zones, share the source
model's power allocation and producer fuel/storage costs.

Player safe-zone rows show bubble presence, activation, chip exhaustion and remaining
paid upkeep. Power loss pauses paid credit, recovery requires vanilla activation, and
chip exhaustion removes the bubble while preserving the block switch and settings.
Admin-created zones follow separate rules. Missing accounting is unavailable, not a
fabricated zero.

## Shared rate cache

Expand **Shared production rate cache**. It shows the current World Authority,
incarnation epoch, per-key epoch, formula and measured rates, sample counts, relative
spread and scripted status. Rates are displayed per second, converted from the source's
per-native-frame values using 60 frames per second. Scripted entries are not reused as
known production rates.

**Clear this key** submits only that key and its displayed epoch through the existing
durable command path. The World Authority checks the expected epoch atomically and
journals the clear. A stale submission reports `epoch_mismatch`; the panel refreshes
the cache so the administrator can review its new state before another deliberate
clear. A running operation remains visible and blocks another maintenance submission
until refresh observes its recorded outcome. A clear does not run a new measurement.

## Compatibility and API

The Gateway must advertise `features.offlinePartitions = "v1"` and the requested
operation in `/admin/v1/capabilities`. Older packages keep ordinary cluster management
and live Registry rows. Offline diagnostics and actions show unsupported or unavailable;
Quasar never falls back to direct node control or exposes node credentials.

Quasar extends its existing `ClusterGatewayClient`, `ClusterCommandService` and
`ClusterApi`. Reads remain under cluster query authorization and cluster restrictions:

| Quasar route | Purpose |
| --- | --- |
| `GET /api/v1/clusters/{name}/offline/partitions` | Exact offline catalog and holders |
| `GET /api/v1/clusters/{name}/offline/details?node=ID&partition=P` | Selected owner/holder profile and last results |
| `GET /api/v1/clusters/{name}/offline/cache` | Current World Authority cache |
| `POST /api/v1/clusters/{name}/offline/prediction` | Deliberate admin prediction, body `{ "node": "ID", "partition": 17, "hours": 1 }`; forwarded as a read-only Gateway GET |
| `POST /api/v1/clusters/{name}/commands` | Admin `trigger` with target `profile` and exact node/partition scope, or `offline-cache-clear` with `key` and `expectedEpoch` |

Command submissions retain the existing `Idempotency-Key`, operation store, audit,
deployment locks and error envelopes. Selection changes cancel reads and discard late
responses. No background wake or compensation loop is added to Quasar.

Focused checks use bUnit rendering, fixture HTTP handlers and in-memory TestServer:

```bash
dotnet test Quasar.Tests/Quasar.Tests.csproj --filter 'FullyQualifiedName~ClusterOffline|FullyQualifiedName~RbacAuthorizationTests|FullyQualifiedName~ClusterApiRouteTests'
```

Set `MagnetarBin` to the current compatible SDK directory as described in
[Building and Development](BuildingAndDevelopment.md). `QUASAR_OFFLINE_RENDER_DIR`
exports rendered fixture markup; `QUASAR_OFFLINE_TEST_DIR` places transient fixture
catalogs and operation records outside the checkout. These checks start no Quasar
worker or game process. They do not establish deployed Gateway, interactive browser,
native compensation or performance acceptance.
