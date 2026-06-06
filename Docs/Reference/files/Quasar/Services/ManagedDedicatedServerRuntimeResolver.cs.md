# Quasar/Services/ManagedDedicatedServerRuntimeResolver.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`ManagedDedicatedServerRuntimeResolver` resolves the three paths needed to launch a dedicated server — the Magnetar launcher executable, its working directory, and the `DedicatedServer64` directory — and auto-installs Magnetar, SteamCMD, and the DS itself when absent. It supports `.zip`, `.tar.gz`, and `.7z` archives for both Magnetar and SteamCMD downloads, guarded by per-component `SemaphoreSlim` install locks. The Magnetar install path branches by OS: Windows ships both runtime builds (`MagnetarInterim.exe` on .NET 10 and `MagnetarLegacy.exe` on .NET Framework 4.8) side-by-side and honors the per-server `DedicatedServerDefinition.ManagedRuntime` selection, while Linux ships a single Interim build behind a top-level wrapper with the apphost under `Bin/`.

## Structure

Namespace: `Quasar.Services`

**`ManagedDedicatedServerRuntimeResolver`** — sealed class.

| Member | Description |
|---|---|
| `ResolveAsync(DedicatedServerDefinition, ct)` | Entry point: picks the runtime flavor (`definition.ManagedRuntime` on Windows, forced `DotNet10` elsewhere); if the configured executable looks like the DS itself (or is empty) → ensure managed Magnetar install; otherwise validate the custom launcher path. Picks working directory, resolves `DedicatedServer64Path`, returns `ResolvedDedicatedServerRuntime`. |
| `EnsureManagedMagnetarInstallAsync(runtime, ct)` | Dispatcher: routes to the Windows or Linux install method by `OperatingSystem.IsWindows()`. |
| `EnsureLinuxManagedMagnetarInstallAsync(ct)` | Linux path (byte-for-byte the previous behavior): returns the apphost binary directly under `<install>/Bin/` (never the top-level `MagnetarInterim` wrapper script), downloading/extracting the archive if missing; sets exec bit; locked by `_magnetarInstallLock`. |
| `EnsureWindowsManagedMagnetarInstallAsync(runtime, ct)` | Windows path: installs both builds together into one folder, then resolves to the requested launcher exe (`GetWindowsMagnetarLauncherFileName`); its containing folder is the working directory (holds the `Libraries` payload). Once either launcher is present the install is complete, so switching runtime per server never re-downloads. Locked by `_magnetarInstallLock`. |
| `DownloadAndExtractMagnetarArchiveAsync(extractRoot, ct)` | Shared helper used by both OS paths: downloads `MagnetarArchiveUrl` (5-minute timeout) and extracts it into `extractRoot`. |
| `GetWindowsMagnetarLauncherFileName(runtime)` | Maps `NetFramework48` → `MagnetarLegacy.exe`, otherwise `MagnetarInterim.exe`. |
| `FindWindowsMagnetarSource(extractRoot)` | Locates the archive's `Magnetar/` folder by the `MagnetarInterim.exe` that has a sibling `Libraries/` directory. |
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

Each install operation has its own `SemaphoreSlim(1,1)` so multiple servers starting at once cannot trigger duplicate installs. On Linux the Magnetar launcher is resolved to the actual apphost binary under `Bin/` rather than the wrapper script, so Quasar starts it directly (Bin/ as working directory) and the tracked PID is the server's own — essential for cross-restart adoption. The two OS layouts differ: Windows extracts a single `Magnetar/` folder holding both launcher exes plus a `Libraries/` subfolder (no `Bin/` wrapper), so the resolved launcher sits directly in the install root and its folder is the working directory; Linux stages the Interim build behind a top-level wrapper with the apphost under `Bin/`. On Windows the per-server `ManagedRuntime` selects `MagnetarInterim.exe` (.NET 10) or `MagnetarLegacy.exe` (.NET Framework 4.8); on non-Windows hosts a `NetFramework48` selection is silently downgraded to `DotNet10` so a `server.json` moved across platforms still launches. On Linux/macOS, SteamCMD uses `+@sSteamCmdForcePlatformType windows` to fetch the Windows DS binaries, and exec bits are applied via `File.SetUnixFileMode`. Archive entries that resolve outside the extraction root are rejected.
