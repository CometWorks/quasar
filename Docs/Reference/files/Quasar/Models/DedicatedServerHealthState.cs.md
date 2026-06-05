# Quasar/Models/DedicatedServerHealthState.cs

**Module:** Quasar.Models  **Kind:** enum  **Tier:** 3

## Summary
Four-level health classification for a running dedicated server, set by the health-monitoring subsystem and surfaced in the dashboard. Drives automatic restart decisions when combined with `AutoRestartOnUnhealthy`.

## Structure
Namespace: `Quasar.Models`  
`public enum DedicatedServerHealthState`

| Value | Int | Meaning |
|---|---|---|
| `Unknown` | 0 | Health has not yet been determined (e.g., agent not attached). |
| `Healthy` | 1 | Server is operating normally. |
| `Warning` | 2 | Degraded but not actionable (e.g., uptime warning threshold exceeded). |
| `Unhealthy` | 3 | Simulation stalled or agent timed out; triggers auto-restart if enabled. |

## Dependencies
- [`Quasar/Models/DedicatedServerRuntimeSnapshot.cs`](DedicatedServerRuntimeSnapshot.cs.md) (field `HealthState`)
