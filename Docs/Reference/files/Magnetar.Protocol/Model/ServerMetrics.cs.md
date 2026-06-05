# Magnetar.Protocol/Model/ServerMetrics.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
DTO carrying real-time performance and status metrics for a running SE dedicated server. Embedded in every `AgentSnapshot` so the Quasar dashboard can display server health at a glance.

## Structure
Namespace: `Magnetar.Protocol.Model`

Class `ServerMetrics` (concrete, no base type):

| Property | Type | Description |
|---|---|---|
| `PlayersOnline` | `int` | Current online player count. |
| `MaxPlayers` | `int` | Server player limit. |
| `SimulationFrameCounter` | `ulong` | Monotonically increasing simulation tick counter. |
| `SimSpeed` | `float` | Simulation speed ratio (1.0 = real-time). |
| `SimCpuLoadPercent` | `float` | CPU load attributed to the simulation thread. |
| `ServerCpuLoadPercent` | `float` | Total server process CPU load. |
| `IsSaveInProgress` | `bool` | True while a world save is running. |
| `UsedPcu` | `int` | PCU currently consumed. |
| `TotalPcu` | `int` | PCU limit configured on the server. |
| `MemoryWorkingSetMb` | `long?` | Process working set in MB; `null` if unavailable. |
| `ActiveGridCount` | `int?` | Live grid count; `null` if unavailable. |
| `ActiveEntityCount` | `int?` | Total live entity count; `null` if unavailable. |
| `UptimeSeconds` | `int` | Server uptime in seconds since last start. |
| `ModsLoaded` | `int` | Number of mods loaded. |
| `PluginsLoaded` | `int` | Number of plugins loaded. |

## Dependencies
- [`Magnetar.Protocol/Model/AgentSnapshot.cs`](AgentSnapshot.cs.md) — embedded as `Metrics`.
