# Cluster health overview

Open a cluster from Dashboard or Servers. **Overview** shows its current state;
use Analytics for history. Status refreshes every five seconds.

The live map connects players to the Gateway, game nodes and World Authority.
Each game node shows its Registry state, routed player count, partition count,
simulation speed, process RAM and process CPU. Hot spares are marked explicitly.
When the cluster has at least two configured hosts, nodes show their host names.
Configured slots remain visible while no node is registered there.

Solid links show player routing over Direct Transport. Dashed links show partition
synchronization with World Authority. The internal Direct Transport node links
show whether their Registry lease is current. Player and partition counts come
from the Registry; they are not measurements of transport sockets. The current
admin contract does not expose internal socket counts.

CPU is total process usage: **100% equals one logical CPU**. Four small bars each
hold one core equivalent. They do not identify physical cores or measure each
core separately. Values above 400% retain their numeric total and show the extra
core equivalents. See [CPU units](AnalyticsCpuLoad.md).

The totals show connected players, partitions, reported RAM and reported CPU.
The coverage line states how many processes supply resource readings. Missing,
disconnected or stale readings are excluded, rather than counted as zero.
Unavailable player or partition queries show a dash on the relevant edges.
Registry health remains the authority for overall cluster health; process
telemetry supplies diagnostic detail.

Gateway CPU and RAM come from Quasar.Host. Update the Host together with Quasar
to enable these readings. The Host measures working set and the change in total
processor time between observations, without dividing by processor count. CPU
is unavailable until two observations of the same process exist. Older Hosts
remain compatible and show unavailable Gateway resource readings. Gateway
readings older than 15 seconds are excluded. Node readings use the existing
authenticated, incarnation-matched agent telemetry and its 15-second freshness
limit.

**Service addresses** lists Steam and optional Direct Transport player endpoints,
internal Direct Transport and management endpoints for nodes and World Authority,
Registry administration, and the Host executor management ports. It uses the
active deployment's recorded specification and current registered nodes; a staged
update cannot replace the displayed active ports. Wildcard player binds display
the enrolled host address where available, with the bind address underneath.
Addresses describe configuration, not proof that a listener is open, a firewall
allows it, or a public address forwards to it. Deployments without a recorded
specification can only show known Registry, node and Host endpoints.

The page groups controls into five views:

| View | Contents |
| --- | --- |
| Overview | Current health, topology, service addresses, expandable runtime details. |
| Nodes | Node operations and detailed process telemetry. |
| Events | Cluster Registry event history. |
| Mods & plugins | Content observations, update checks and queues. |
| Maintenance | Deployed/latest versions, release staging, pending updates, backups, recovery and deployment tools. |
| Settings | Name, profile, hot spares, shutdown grace period, plugin configuration, advanced controls and cluster removal. |

Start, Stop, Save and the current operation result stay visible across views.
Save world requires a reachable Gateway with management scope and save support;
it is disabled during another operation or an outage, including on Maintenance.
Its tooltip explains why it is unavailable. The click handler also checks availability.

Use the top-level **Players** page for live and historical players. Each cluster
is one server in its filter, with one row per player regardless of node moves.
Quasar collects Registry clients in the background every five seconds and retains
history in the existing known-player store. Disconnects and restarts keep that
history, subject to the Players page's retention and cleanup settings. Cluster
Kick, Ban and Unban use the Gateway, with cluster management permission. Individual
node role edits and kick-cooldown controls are not cluster actions.
Settings retain the existing staging and maintenance workflow: changing a profile
or hot spare count prepares an update; applying it still requires full downtime.
Removal remains confirmed through the existing removal dialog. All existing
query, management and configuration permissions remain enforced.

Views have bookmarkable fragments: `#cluster-overview`, `#cluster-nodes`,
`#cluster-events`, `#cluster-content`, `#cluster-configuration` (Maintenance) and `#cluster-settings`.
Existing content notifications and maintenance links select their corresponding
view. Opened views retain their component state when switching views.

Overview, Maintenance and Settings remain available while Quasar checks the
Gateway admin connection or it is unavailable. Host-based recovery, backups and
local settings remain reachable through their usual tabs. Each action retains its
permissions, confirmations and safety checks.

Nodes, Events and Mods & plugins unlock when the authenticated Gateway capabilities
endpoint responds, regardless of desired On/Off state. A failed check returns a
selected live-only view to Overview. The five-second poll restores the requested
view and its existing state when the connection recovers, including bookmarks.

The map uses native HTML, CSS and SVG, with textual metrics alongside colors and
bars. Large clusters scroll within a labelled, keyboard-focusable map region. On
phones, the node row scrolls while Gateway and World Authority remain visible;
the rest of the page stays within the viewport. No graphing dependency or
generated image template is required.

## Verification

Run the focused component, polling, incarnation and Host metric checks without
launching the Quasar web service:

```bash
dotnet test Quasar.Tests/Quasar.Tests.csproj --filter 'FullyQualifiedName~ClusterHealthOverviewTests|FullyQualifiedName~ClusterDetailPanelTests|FullyQualifiedName~ClusterFleetPanelTests|FullyQualifiedName~ClusterDetailPageTests|FullyQualifiedName~HostGatewayPreparationTests|FullyQualifiedName~ClusterAgentIdentityTests'
```

Set `MagnetarBin` to a current Magnetar SDK directory as described in
[Building and Development](BuildingAndDevelopment.md). Set
`QUASAR_CLUSTER_PREVIEW_OUTPUT` to a temporary path to export the actual map
component with sample data for offline layout inspection. This export does not
exercise a live Blazor circuit, game cluster, firewall or network listener.
