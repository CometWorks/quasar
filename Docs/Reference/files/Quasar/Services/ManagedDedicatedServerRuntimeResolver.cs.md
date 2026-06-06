# Quasar/Services/ManagedDedicatedServerRuntimeResolver.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`ManagedDedicatedServerRuntimeResolver` resolves the three paths needed to launch a dedicated server — the Magnetar launcher executable, its working directory, and the `DedicatedServer64` directory — and auto-installs Magnetar, SteamCMD, and the DS itself when absent. It supports `.zip`, `.tar.gz`, and `.7z` archives for both Magnetar and SteamCMD downloads, guarded by per-component `SemaphoreSlim` install locks.

## Structure

Namespace: `Quasar.Services`

**`ManagedDedicatedServerRuntimeResolver`** — sealed class.

| Member | Description |
|---|---|
| `ResolveAsync(DedicatedServerDefinition, ct)` | Entry point: if the configured executable looks like the DS itself (or is empty) → ensure managed Magnetar install; otherwise validate the custom launcher path. Picks working directory, resolves `DedicatedServer64Path`, returns `ResolvedDedicatedServerRuntime`. |
| `EnsureManagedMagnetarInstallAsync(ct)` | Returns the apphost binary directly under `<install>/Bin/` (never the top-level `MagnetarInterim` wrapper script), downloading/extracting the archive if missing; sets exec bit on Linux; locked by `_magnetarInstallLock`. |
| `ResolveDedicatedServer64PathAsync(...)` | Priority order: path inferred from a DS executable → `DedicatedServer64OverridePath` option → directory adjacent to the launcher → managed steamcmd install (if `PreferManagedDedicatedServerInstall`) → well-known Steam install locations. Throws if none valid. |
| `TryEnsureManagedDedicatedServerInstallAsync(ct)` | Runs `steamcmd +app_update 298740 validate`; on non-Windows forces Windows platform type; locked by `_dedicatedServerInstallLock`; falls back to a prior valid install on failure. |
| `ResolveSteamCmdPathAsync(ct)` | `SteamCmdPath` option → managed install dir → `PATH` → `TryEnsureManagedSteamCmdInstallAsync`. |
| `TryEnsureManagedSteamCmdInstallAsync(ct)` | Downloads/extracts SteamCMD; sets exec bits; locked by `_steamCmdInstallLock`. |
| `ExtractArchive / DetectArchiveKind` | Dispatches to BCL `ZipArchive` or SharpCompress (`.tar.gz`, `.7z`) by 8-byte magic header + extension. |
| `ResolveArchiveEntryPath(...)` | Normalises separators; rejects entries that escape the extraction root (path-traversal guard). |

**`ResolvedDedicatedServerRuntime`** — `sealed record(string ExecutablePath, string WorkingDirectory, string DedicatedServer64Path)`. Internal enum `ArchiveKind` (`Unknown`, `Zip`, `TarGz`, `SevenZip`). Private `MagnetarSource` record (`Directory`, `LauncherPath`, `BinDirectory`).

## Dependencies

- `Quasar/Services/ManagedRuntimeOptions.cs` — download URLs, install/override directories, preference flags
- `Quasar/Models/DedicatedServerDefinition.cs` — input definition
- `Magnetar.Protocol.Runtime` — `MagnetarPaths` (managed runtime cache dir)
- SharpCompress — `ArchiveFactory`, `IArchiveEntry`, `ReaderOptions`
- BCL `System.IO.Compression.ZipArchive`, `System.Diagnostics.Process`
- `IHttpClientFactory` (5-minute download timeout)

## Notes

Each install operation has its own `SemaphoreSlim(1,1)` so multiple servers starting at once cannot trigger duplicate installs. The Magnetar launcher is resolved to the actual apphost binary under `Bin/` rather than the wrapper script, so Quasar starts it directly (Bin/ as working directory) and the tracked PID is the server's own — essential for cross-restart adoption. On Linux/macOS, SteamCMD uses `+@sSteamCmdForcePlatformType windows` to fetch the Windows DS binaries, and exec bits are applied via `File.SetUnixFileMode`. Archive entries that resolve outside the extraction root are rejected.
