# Quasar/Models/DedicatedServerProcessState.cs

**Module:** Quasar.Models  **Kind:** enum  **Tier:** 3

## Summary
State machine enum representing the actual OS-process lifecycle of a managed dedicated server, as tracked by `DedicatedServerSupervisor`. Exposed in `DedicatedServerRuntimeSnapshot` and used by the `/api/health` endpoint to count running servers.

## Structure
Namespace: `Quasar.Models`  
`public enum DedicatedServerProcessState`

| Value | Int | Meaning |
|---|---|---|
| `Stopped` | 0 | No process running; server is fully down. |
| `Starting` | 1 | Process has been launched; waiting for agent attach. |
| `Running` | 2 | Process running and agent attached (or grace period active). |
| `Stopping` | 3 | Graceful shutdown initiated; process still alive. |
| `Restarting` | 4 | Intentional restart in progress (stop → start cycle). |
| `Crashed` | 5 | Process exited unexpectedly; restart may follow. |
| `Faulted` | 6 | Exceeded restart attempts or unrecoverable error; no further auto-restart. |

## State Machine

Goal state (`DedicatedServerGoalState.Off` / `On`) is desired state; this enum is observed process lifecycle state.

| Current | Typical next states |
|---|---|
| `Stopped` | `Starting` after goal `On` or Start. |
| `Starting` | `Running` after launch/agent progress, `Stopping` if admin cancels, `Faulted` on launch failure. |
| `Running` | `Stopping`, `Restarting`, `Crashed`, `Faulted`. |
| `Stopping` | `Stopped` after process exit, `Faulted` on stop failure. |
| `Restarting` | `Starting` / `Running` as the stop-start cycle advances, `Faulted` on failure. |
| `Crashed` | `Starting` when restart policy or explicit Start applies, `Stopped` when goal is Off. |
| `Faulted` | `Starting` after explicit admin Start or policy reset. |

UI lifecycle actions follow this policy:

| State | Buttons |
|---|---|
| `Stopped` / `Crashed` / `Faulted` | Start |
| `Starting` | Stop, to cancel an accidental world start |
| `Running` | Stop and Restart |
| `Stopping` / `Restarting` | None |

## Dependencies
- [`Quasar/Models/DedicatedServerRuntimeSnapshot.cs`](DedicatedServerRuntimeSnapshot.cs.md) (field `State`)
- [`Quasar/Program.cs`](../Program.cs.md) (used in `/api/health` runningServers count)
