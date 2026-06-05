# Quasar/Services/ManagedDedicatedServerRuntimeResolver.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`ManagedDedicatedServerRuntimeResolver` resolves the three paths needed to launch a dedicated server — the Magnetar launcher executable, its working directory, and the `DedicatedServer64` directory — and auto-installs Magnetar, SteamCMD, and the DS itself if they are absent. It supports `.zip`, `.tar.gz`, and `.7z` archives for both Magnetar and SteamCMD downloads, guarded by per-component `SemaphoreSlim` install locks.

## Structure

Namespace: `Quasar.Services`

**`ManagedDedicatedServerRuntimeResolver`** — sealed class.

| Member | Description |
|---|---|
| `ResolveAsync(DedicatedServerDefinition, ct)` | Entry point: detects whether configured executable path is the DS itself (→ managed Magnetar install) or a custom launcher; resolves `DedicatedServer64Path`; returns `ResolvedDedicatedServerRuntime`. |
| `EnsureManagedMagnetarInstallAsync(ct)` | Downloads and extracts the Magnetar archive if the install directory is incomplete; sets executable bits on Linux; guarded by `_magnetarInstallLock`. |
| `ResolveDedicatedServer64PathAsync(...)` | Checks inferred path, override option, adjacent directory, managed install, and known Steam install locations in priority order. |
| `TryEnsureManagedDedicatedServerInstallAsync(ct)` | Runs `steamcmd +app_update 298740 validate` to install/update the DS; on Linux forces Windows platform; guarded by `_dedicatedServerInstallLock`. |
| `ResolveSteamCmdPathAsync(ct)` | Checks override option, managed install directory, PATH env, then calls `TryEnsureManagedSteamCmdInstallAsync`. |
| `TryEnsureManagedSteamCmdInstallAsync(ct)` | Downloads and extracts SteamCMD archive; sets executable bits; guarded by `_steamCmdInstallLock`. |
| `ExtractArchive(archivePath, destinationRoot)` | Dispatches to `ExtractZipArchive` (BCL), `ExtractReaderArchive`/`ExtractSevenZipArchive` (SharpCompress) based on magic bytes + extension. |
| `DetectArchiveKind(string)` | Reads 8-byte file header; recognises `.7z` (magic `37 7A BC AF 27 1C`), `.zip` (`PK`), `.tar.gz`/`.tgz`. |
| `ResolveArchiveEntryPath(...)` | Normalises entry paths; verifies resolved path is within extraction root (path traversal guard). |

**`ResolvedDedicatedServerRuntime`** — `sealed record(string ExecutablePath, string WorkingDirectory, string DedicatedServer64Path)`.

Internal enum `ArchiveKind` (`Unknown`, `Zip`, `TarGz`, `SevenZip`).

## Dependencies

- `Quasar/Services/ManagedRuntimeOptions.cs` — download URLs and install directories
- [`Quasar/Models/DedicatedServerDefinition.cs`](../Models/DedicatedServerDefinition.cs.md) — input definition
- `Magnetar.Protocol.Runtime` — `MagnetarPaths`
- SharpCompress — `ArchiveFactory`, `IArchiveEntry`, `ReaderOptions` (`.tar.gz`, `.7z`)
- BCL `System.IO.Compression.ZipArchive` (`.zip`)
- `IHttpClientFactory` (5-minute timeout for downloads)

## Notes

All three install operations are protected by separate `SemaphoreSlim(1,1)` to prevent concurrent duplicate installations when multiple servers start simultaneously. On Linux, SteamCMD uses `+@sSteamCmdForcePlatformType windows` to download the Windows DS binaries. Executable bits are applied via `File.SetUnixFileMode` on Linux/macOS. Path traversal in archive entries is prevented by verifying the fully-resolved path starts with the extraction root.
