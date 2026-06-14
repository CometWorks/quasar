# Quasar/Services/Backup/ServerRestoreCoordinator.cs

**Module:** Quasar.Services.Backup  **Kind:** class  **Tier:** 2

## Summary
`ServerRestoreCoordinator` tracks which managed server unique names currently have a backup restore in progress. Restores rewrite server files in place, so callers can use this coordinator to prevent a server start or a second restore from racing against the same server data.

## Structure
Namespace: `Quasar.Services.Backup`

`public sealed class ServerRestoreCoordinator`

| Member | Description |
|---|---|
| `IsRestoreInProgress(string uniqueName)` | Returns `true` when the given server unique name is currently claimed by an active restore scope; blank names return `false`. |
| `TryBeginRestore(string uniqueName, out IDisposable? scope)` | Claims the restore slot for a server and returns a disposable scope that releases it. Returns `false` when another restore already owns that server. |

Private state is guarded by `_sync` and stored in a case-insensitive `HashSet<string> _restoring`. The private `RestoreScope` releases the unique name once disposed and is idempotent.

## Dependencies
- BCL `System.Diagnostics.CodeAnalysis.NotNullWhenAttribute`
- BCL `HashSet<T>`, `IDisposable`

## Notes
The coordinator is intentionally in-memory. It protects concurrent requests inside the current Quasar process; it does not persist restore state across process restarts.
