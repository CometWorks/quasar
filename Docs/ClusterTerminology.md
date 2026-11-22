# Cluster terminology

These operator-facing terms were agreed individually after reviewing cluster
integration #137 and the readiness follow-up #165. They apply to cluster setup,
host enrollment, conversion, controls, monitoring and the standalone server editor.

## Agreed labels

| Previous terminology | Agreed terminology | Meaning / application |
| --- | --- | --- |
| Host, machine, cluster host | **Host machine** | Physical or virtual computer running cluster processes; navigation and placement use this term. |
| Quasar Host, Quasar.Host, executor | **Host executor** | Background service installed on a host machine. `Quasar.Host` remains its executable and project name. |
| Gateway and World Authority machine / Host | **Cluster Gateway and World Authority host machine** | Placement selector. That host machine also runs the Cluster Registry. |
| Config profile, Selected config profile | **Configuration profile** | Profile fields and links. |
| Player port, Public Steam port, Listen port | **Public server port** | Public UDP listener for player connections; separate from internal node and Registry administration ports. |
| World Authority, WA | **World Authority** | Full role name in the UI. Documentation may use WA after introducing the role; protocol identifiers stay exact. |
| Gateway / Registry | **Cluster Gateway / Cluster Registry** | Cluster Gateway routes player traffic. Cluster Registry owns coordination, administration and retained control events. |
| Display name | **Server name** | Human-readable server/cluster name. Host enrollment uses **Host machine name**. The optional standalone **In-game server name** continues to override the advertised name. |
| Host ID, Unique server name, Identifier | **Host machine ID / Cluster ID / Server ID** | Permanent identifiers; separate from editable human-readable names. |
| Connect existing Gateway | **Register existing cluster** | Registers an independently installed Cluster Registry. Does not provision host machines or deploy a new cluster. |
| Standalone conversion setup | **Destination cluster / Create a new cluster / Convert to cluster** | Destination selection and one action. Quasar generates placement, ports, release inputs and credentials. |
| Prepared (Hosts column) | **Configured clusters** | Count of configured attachments on a Host executor, not live readiness. |
| Connected Agents | **Connected agents** | Connected in-process telemetry plugins; separate from Host executors. |

## Generated suggestions

New enrollment, cluster creation/registration and standalone server dialogs prefill
editable names and IDs. Suggestions use an entity prefix plus twelve hexadecimal
characters, for example `host-machine-a1b2c3d4e5f6`, `cluster-a1b2c3d4e5f6` or
`server-a1b2c3d4e5f6`. They are actual field values, not placeholders, and are saved
with the submitted request. Clearing a name supplies another editable suggestion
on blur or submission. Supplied names are preserved.

Initial name and ID suggestions match when both fields are empty. Subsequent name
changes preserve explicit IDs. The standalone editor retains its existing automatic
name-to-ID derivation until the ID is manually edited. Cluster-to-standalone conversion initially uses
the source cluster's name and keeps its proposed destination ID when the name is
cleared. Resumed setup/conversion requests retain their bound identity and names.
Standalone-to-cluster conversion copies source names and generates its new cluster ID from the conversion UUID.
Catalog validation remains authoritative for ID syntax, collisions and removed IDs;
a suggestion does not reserve an ID or bypass those checks.

## Current occurrences

Counts cover exact, case-sensitive source lines in the WASM cluster panels, cluster setup and conversion, host management and standalone server editor.

| Exact phrase | Lines | Occurrence locations |
| --- | ---: | --- |
| `Add host machine` | 2 | [HostMachines](../Quasar.Client/Components/HostMachines.razor), [SetupCluster](../Quasar.Client/Components/SetupCluster.razor) |
| `Host machine ID` | 1 | [HostEnrollmentForm](../Quasar.Client/Components/HostEnrollmentForm.razor) |
| `Host machine name` | 1 | [HostEnrollmentForm](../Quasar.Client/Components/HostEnrollmentForm.razor) |
| `Cluster ID` | 2 | [SetupCluster](../Quasar.Client/Components/SetupCluster.razor) |
| `Server ID` | 4 | [ConvertCluster](../Quasar.Client/Components/ConvertCluster.razor), [FleetServerEditor](../Quasar.Client/Components/FleetServerEditor.razor) |
| `Server name` | 8 | [ClusterAdminPanel](../Quasar.Client/Clusters/ClusterAdminPanel.razor), [ConvertCluster](../Quasar.Client/Components/ConvertCluster.razor), [FleetServerEditor](../Quasar.Client/Components/FleetServerEditor.razor), [SetupCluster](../Quasar.Client/Components/SetupCluster.razor) |
| `Configuration profile` | 6 | [ClusterAdminPanel](../Quasar.Client/Clusters/ClusterAdminPanel.razor), [ClusterDetailPanel](../Quasar.Client/Clusters/ClusterDetailPanel.razor), [FleetServerEditor](../Quasar.Client/Components/FleetServerEditor.razor), [SetupCluster](../Quasar.Client/Components/SetupCluster.razor) |
| `Public server port` | 4 | [ConvertCluster](../Quasar.Client/Components/ConvertCluster.razor), [FleetServerEditor](../Quasar.Client/Components/FleetServerEditor.razor), [SetupCluster](../Quasar.Client/Components/SetupCluster.razor) |
| `World Authority` | 10 | [ClusterAdminPanel](../Quasar.Client/Clusters/ClusterAdminPanel.razor), [ClusterDetailPanel](../Quasar.Client/Clusters/ClusterDetailPanel.razor), [ClusterFleetPanel](../Quasar.Client/Clusters/ClusterFleetPanel.razor), [ClusterHealthOverview](../Quasar.Client/Clusters/ClusterHealthOverview.razor), [ClusterPartitionsPanel](../Quasar.Client/Clusters/ClusterPartitionsPanel.razor), [SetupCluster](../Quasar.Client/Components/SetupCluster.razor) |
| `Cluster Gateway` | 4 | [ClusterAdminPanel](../Quasar.Client/Clusters/ClusterAdminPanel.razor), [SetupCluster](../Quasar.Client/Components/SetupCluster.razor) |
| `Cluster Registry` | 31 | [ClusterAdminPanel](../Quasar.Client/Clusters/ClusterAdminPanel.razor), [ClusterConsoleDialog](../Quasar.Client/Clusters/ClusterConsoleDialog.razor), [ClusterDeploymentPanel](../Quasar.Client/Clusters/ClusterDeploymentPanel.razor), [ClusterDetailPanel](../Quasar.Client/Clusters/ClusterDetailPanel.razor), [ClusterFleetPanel](../Quasar.Client/Clusters/ClusterFleetPanel.razor), [ClusterHealthOverview](../Quasar.Client/Clusters/ClusterHealthOverview.razor), [ClusterPartitionsPanel](../Quasar.Client/Clusters/ClusterPartitionsPanel.razor), [SetupCluster](../Quasar.Client/Components/SetupCluster.razor) |
| `Register existing cluster` | 2 | [SetupCluster](../Quasar.Client/Components/SetupCluster.razor) |
| `Add placement` | 0 | None |
| `Remove placement` | 0 | None |
| `Configured clusters` | 2 | [HostMachines](../Quasar.Client/Components/HostMachines.razor) |
| `Connected agents` | 2 | [HostMachines](../Quasar.Client/Components/HostMachines.razor) |

## Deliberate compatibility terminology

| Retained technical name | Operator-facing meaning | Why it remains |
| --- | --- | --- |
| `GatewayUrl`, `GatewayAdminTokenEnvironmentVariable`, `ClusterGatewayClient` | Cluster Registry admin endpoint/client | Existing JSON fields and code contracts retain their names. |
| `ClusterGateway.AdminContract`, `X-Cluster-Gateway-Protocol` | Cluster Registry admin contract | Package, namespace, project and HTTP header compatibility. Upstream sources now live in `ClusterRegistry.AdminContract`. |
| `HostId`, `HostCommandUrl`, `Quasar.Host`, `Hosts/`, `/hosts` | Host machine identity, Host executor transport, executable, storage and routes | No schema, executable, storage or route migration. |
| `GatewaySpec`, Host status `Gateways` | Managed cluster-service process specification/status | Existing supervision contract. UI calls its process status **Cluster Registry process** or **Cluster services** rather than claiming it proves the player Gateway is reachable. |
| `DisplayName`, `UniqueName` | Server name and permanent ID | Existing model and API fields. |
| `WorldAuthority`, `WA`, `world-authority`, `cluster-wa` | World Authority enum, wire role, slot and plugin ID | Exact spellings required by the upstream contracts and release artifacts. |
| `SteamPort`, `PlayerPort`, `steamListen` | Public server port | Existing request/specification fields. |

The contract [README](../Contracts/ClusterGateway.AdminContract/README.md) now agrees
with [SOURCE.md](../Contracts/ClusterGateway.AdminContract/SOURCE.md): DTO package
`0.4.0`, cluster source release `v1.1.7`. These are independent version schemes.
Only documentation changed; pinned DTO sources and checksums remain intact.

Enrollment, connection, reachability and setup readiness remain separate states.
Likewise slot/node/epoch, prepare/stage/activate, and save/export/backup/restore/
recovery/rollback name distinct operations or artifacts. Terminology alignment
preserves their behavior and security checks.
