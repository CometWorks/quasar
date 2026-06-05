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

## Dependencies
- [`Quasar/Models/DedicatedServerRuntimeSnapshot.cs`](DedicatedServerRuntimeSnapshot.cs.md) (field `State`)
- [`Quasar/Program.cs`](../Program.cs.md) (used in `/api/health` runningServers count)
