# Quasar/Models/ManagedServerRuntime.cs

**Module:** Quasar.Models  **Kind:** enum  **Tier:** 1

## Summary
Selects which Magnetar build — and therefore which .NET runtime — launches a managed Space Engineers dedicated server. Only meaningful on Windows, where both builds ship side-by-side; non-Windows hosts always run the `DotNet10` (Interim) build and the resolver forces this value regardless of what is persisted.

## Structure
Namespace: `Quasar.Models`  
`public enum ManagedServerRuntime`

| Value | Int | Meaning |
|---|---|---|
| `DotNet10` | 0 | Magnetar "Interim" build running on .NET 10. Default; the only option on Linux. |
| `NetFramework48` | 1 | Magnetar "Legacy" build running on .NET Framework 4.8. Windows only. |

## Dependencies
- [`Quasar/Models/DedicatedServerDefinition.cs`](DedicatedServerDefinition.cs.md) (field `ManagedRuntime`)

## Notes
- The value is honored only on Windows. `ManagedDedicatedServerRuntimeResolver` downgrades a `NetFramework48` selection to `DotNet10` on non-Windows hosts so a `server.json` moved from Windows to Linux still launches.
- On Windows both launcher executables (`MagnetarInterim.exe` / `MagnetarLegacy.exe`) install together into one folder, so switching a server's runtime never requires a re-download.
