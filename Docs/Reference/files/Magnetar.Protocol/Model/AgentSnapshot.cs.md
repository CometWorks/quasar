# Magnetar.Protocol/Model/AgentSnapshot.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Periodic snapshot pushed by `Quasar.Agent` to the Quasar supervisor containing the full observable state of one running SE dedicated server: identity fields, runtime status, scalar performance metrics, current profiler mode, optional profiler timing data, online players, kicked players (serving a kick cooldown), recent chat, recent deaths, and loaded plugin list.

## Structure
Namespace: `Magnetar.Protocol.Model`

Class `AgentSnapshot` (concrete, no base type):

| Property | Type | Description |
|---|---|---|
| `UniqueName` | `string` | Matches the supervisor's server unique name. |
| `AgentId` | `string` | Runtime connection GUID (echoed from `AgentHello`). |
| `HostId` / `HostName` | `string` | Hosting machine identity. |
| `ServerId` / `ServerName` / `WorldName` | `string` | SE server/world identity. |
| `IsRunning` | `bool` | Whether the simulation loop is active. |
| `CapturedAtUtc` | `DateTimeOffset` | Snapshot capture time (defaults to `UtcNow`). |
| `Metrics` | `ServerMetrics` | CPU, sim-speed, PCU, uptime, etc. |
| `ProfilerMode` | `string` | Current agent profiler mode (`Off`, `SafeContinuous`, or `DeepContinuous`). |
| `Profiler` | `ProfilerSnapshot?` | Latest completed profiler window, if available. |
| `Players` | `List<PlayerSnapshot>` | All currently connected players. |
| `KickedPlayers` | `List<KickedPlayerSnapshot>` | Offline players currently serving a server-side kick cooldown (separate from `Players`). |
| `RecentChat` | `List<ChatMessageSnapshot>` | Chat messages since last snapshot. |
| `RecentDeaths` | `List<DeathEventSnapshot>` | Death events since last snapshot. |
| `Plugins` | `List<PluginRuntimeInfo>` | Loaded plugin registry. |

## Dependencies
- [`Magnetar.Protocol/Model/ServerMetrics.cs`](ServerMetrics.cs.md)
- [`Magnetar.Protocol/Model/ProfilerSnapshot.cs`](ProfilerSnapshot.cs.md)
- [`Magnetar.Protocol/Model/PlayerSnapshot.cs`](PlayerSnapshot.cs.md)
- [`Magnetar.Protocol/Model/KickedPlayerSnapshot.cs`](KickedPlayerSnapshot.cs.md)
- [`Magnetar.Protocol/Model/ChatMessageSnapshot.cs`](ChatMessageSnapshot.cs.md)
- [`Magnetar.Protocol/Model/DeathEventSnapshot.cs`](DeathEventSnapshot.cs.md)
- [`Magnetar.Protocol/Model/PluginRuntimeInfo.cs`](PluginRuntimeInfo.cs.md)
- [`Magnetar.Protocol/Transport/AgentWireMessage.cs`](../Transport/AgentWireMessage.cs.md) — carried as the `Snapshot` field.
