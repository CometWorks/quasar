# Magnetar.Protocol/Runtime/QuasarActiveReleasePointer.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
Sealed DTO persisted to `Updates/active-release.json` (path from `MagnetarPaths.GetQuasarActiveReleasePath()`). Quasar.Bootstrap reads this file to determine which release binary to launch as the Quasar supervisor.

## Structure
Namespace: `Magnetar.Protocol.Runtime`

Class `QuasarActiveReleasePointer` (sealed, no base type):

| Property | Type | Description |
|---|---|---|
| `Version` | `string` | Semver string of the active Quasar release. |
| `FileName` | `string` | Absolute or relative path to the release executable. |
| `Arguments` | `string` | Command-line arguments to pass to the process. |
| `WorkingDirectory` | `string` | Working directory to set when launching. |
| `ActivatedAtUtc` | `DateTimeOffset` | UTC time this release was activated (defaults to `UtcNow`). |

## Dependencies
- [`Magnetar.Protocol/Runtime/MagnetarPaths.cs`](MagnetarPaths.cs.md) — `GetQuasarActiveReleasePath()` is the file location.
