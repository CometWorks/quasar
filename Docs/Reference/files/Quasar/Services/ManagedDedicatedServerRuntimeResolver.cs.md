# Quasar/Services/ManagedDedicatedServerRuntimeResolver.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`ManagedDedicatedServerRuntimeResolver` resolves the paths needed to launch a dedicated server — the Magnetar launcher executable, its working directory, the `DedicatedServer64` directory, and any native-library search paths required by the child process — and auto-installs Magnetar, SteamCMD, and the DS itself when absent. It also exposes a startup readiness workflow that reports SteamCMD and Dedicated Server download/install progress before managed launches are allowed. It supports `.zip`, `.tar.gz`, and `.7z` archives for both Magnetar and SteamCMD downloads, guarded by per-component `SemaphoreSlim` install locks. Managed Magnetar installs are tracked by a `.quasar-magnetar-release.json` marker, so launch-time and background checks can compare the installed release to GitHub's latest full release instead of reusing stale launchers forever. The Magnetar install path branches by OS: Windows ships both runtime builds (`MagnetarInterim.exe` on .NET 10 and `MagnetarLegacy.exe` on .NET Framework 4.8) side-by-side and honors the per-server `DedicatedServerDefinition.ManagedRuntime` selection, while Linux ships a single Interim build behind a top-level wrapper with the apphost under `Bin/`.

## Structure

Namespace: `Quasar.Services`

**`ManagedDedicatedServerRuntimeResolver`** — sealed class.

| Member | Description |
|---|---|
| `ResolveAsync(DedicatedServerDefinition, ct)` | Entry point: picks the runtime flavor (`definition.ManagedRuntime` on Windows, forced `DotNet10` elsewhere); if the configured executable looks like the DS itself (or is empty) → ensure managed Magnetar install; otherwise validate the custom launcher path. Picks working directory, resolves `DedicatedServer64Path` and Linux native-library search paths, returns `ResolvedDedicatedServerRuntime`. |
| `EnsureManagedRuntimeReadyAsync(progress?, ct)` | Startup readiness path: ensures managed SteamCMD, validates/downloads the managed Dedicated Server install, prepares Linux SteamCMD `linux64` native runtime when needed, reports component progress, and returns `ManagedRuntimeReadiness`. |
| `EnsureManagedMagnetarCurrentAsync(ct)` | Public background-check hook used by `ManagedRuntimeWarmupService`; ensures the managed Magnetar install is present and current with the latest configured archive source. |
| `EnsureManagedMagnetarInstallAsync(runtime, ct)` | Dispatcher: routes to the Windows or Linux install method by `OperatingSystem.IsWindows()`. |
| `EnsureLinuxManagedMagnetarInstallAsync(ct)` | Linux path: resolves the latest archive reference, compares it with the installed marker, and returns the apphost binary directly under `<install>/Bin/` when current; otherwise downloads/extracts the archive, copies `Bin/`, sets exec bit, writes the marker, and is locked by `_magnetarInstallLock`. |
| `EnsureWindowsManagedMagnetarInstallAsync(runtime, ct)` | Windows path: resolves the latest archive reference, compares it with the installed marker, and returns the requested launcher exe (`GetWindowsMagnetarLauncherFileName`) when current; otherwise installs both builds together into one folder and writes the marker. Its containing folder is the working directory (holds the `Libraries` payload). Locked by `_magnetarInstallLock`. |
| `ResolveMagnetarArchiveReferenceAsync(client, ct)` | Resolves a direct `MagnetarArchiveUrl` override or the latest full GitHub release asset matching `MagnetarArchiveAssetPattern` into source kind, release tag, asset name, and download URL. |
| `DownloadAndExtractMagnetarArchiveAsync(archive, extractRoot, ct)` | Shared helper used by both OS paths: downloads the resolved Magnetar archive (5-minute timeout) and extracts it into `extractRoot`. |
| `GetWindowsMagnetarLauncherFileName(runtime)` | Maps `NetFramework48` → `MagnetarLegacy.exe`, otherwise `MagnetarInterim.exe`. |
| `FindWindowsMagnetarSource(extractRoot)` | Locates the archive's `Magnetar/` folder by the `MagnetarInterim.exe` that has a sibling `Libraries/` directory. |
| `ResolveDedicatedServer64PathAsync(...)` | Priority order: path inferred from a DS executable → `DedicatedServer64OverridePath` option → directory adjacent to the launcher → managed steamcmd install (if `PreferManagedDedicatedServerInstall`) → well-known Steam install locations. Throws if none valid. |
| `TryEnsureManagedDedicatedServerInstallAsync(ct, steamCmdPath?, progress?)` | Runs `steamcmd +app_update 298740 validate`; on non-Windows forces Windows platform type; locked by `_dedicatedServerInstallLock`; tries up to three attempts before returning failure; falls back to a prior valid install on final failure and reports Dedicated Server download/install phase with attempt count when a progress sink is provided. |
| `ResolveSteamCmdPathAsync(ct)` | `SteamCmdPath` option → managed install dir → `PATH` → `TryEnsureManagedSteamCmdInstallAsync`. |
| `EnsureManagedSteamCmdInstallAsync(progress?, ct)` | Downloads/extracts managed SteamCMD when missing, reports archive percentage when content length is available, sets exec bits, and is locked by `_steamCmdInstallLock`. |
| `RunSteamCmdAsync(...)` | Runs SteamCMD commands such as `+quit` for native-runtime preparation and throws with trimmed stdout/stderr on failure. |
| `ResolveNativeLibrarySearchPaths()` | Linux-only helper that prefers Quasar's managed SteamCMD `linux64/` runtime folder when it contains `steamclient.so`, `libtier0_s.so`, and `libvstdlib_s.so`; the supervisor prepends this path to `LD_LIBRARY_PATH` so Steam GameServer init can find Steam's native runtime on fresh headless hosts. |
| `CopyToFileWithProgressAsync(...)` | Streams an HTTP response body to disk and reports integer percentage when `Content-Length` is known, otherwise reports indeterminate progress. |
| `ExtractArchive / DetectArchiveKind` | Dispatches to BCL `ZipArchive` or SharpCompress (`.tar.gz`, `.7z`) by 8-byte magic header + extension. |
| `ResolveArchiveEntryPath(...)` | Normalises separators; rejects entries that escape the extraction root (path-traversal guard). |

**`ResolvedDedicatedServerRuntime`** — `sealed record(string ExecutablePath, string WorkingDirectory, string DedicatedServer64Path, IReadOnlyList<string> NativeLibrarySearchPaths)`.

**`ManagedRuntimeReadiness`** — sealed record returned by startup readiness checks: readiness bool, SteamCMD path, SteamCMD runtime path, DedicatedServer64 path, and failure message.

**`ManagedRuntimeInstallProgress`** — sealed record emitted to readiness progress sinks with component (`SteamCmd` / `DedicatedServer`), phase (`Pending`, `Checking`, `Downloading`, `Installing`, `Ready`, `Failed`), message, optional percent, and path.

Internal enum `ArchiveKind` (`Unknown`, `Zip`, `TarGz`, `SevenZip`). Private Magnetar metadata helpers include `MagnetarSource`, `MagnetarArchiveReference`, `InstalledMagnetarRelease`, and `MagnetarArchiveSourceKinds`.

## Dependencies

- [`Quasar/Services/ManagedRuntimeOptions.cs`](ManagedRuntimeOptions.cs.md) — download URLs, install/override directories, preference flags
- [`Quasar/Models/DedicatedServerDefinition.cs`](../Models/DedicatedServerDefinition.cs.md) — input definition
- `Magnetar.Protocol.Runtime` — `MagnetarPaths` (managed runtime cache dir)
- SharpCompress — `ArchiveFactory`, `IArchiveEntry`, `ReaderOptions`
- BCL `System.IO.Compression.ZipArchive`, `System.Diagnostics.Process`
- `IHttpClientFactory` (5-minute download timeout)

## Notes

Each install operation has its own `SemaphoreSlim(1,1)` so multiple servers starting at once cannot trigger duplicate installs. Magnetar checks always attempt to resolve the current configured archive source; if that check fails while a launcher already exists, Quasar logs the failure and continues with the installed runtime instead of blocking a server start. On Linux the Magnetar launcher is resolved to the actual apphost binary under `Bin/` rather than the wrapper script, so Quasar starts it directly (Bin/ as working directory) and the tracked PID is the server's own — essential for cross-restart adoption. The two OS layouts differ: Windows extracts a single `Magnetar/` folder holding both launcher exes plus a `Libraries/` subfolder (no `Bin/` wrapper), so the resolved launcher sits directly in the install root and its folder is the working directory; Linux stages the Interim build behind a top-level wrapper with the apphost under `Bin/`. On Windows the per-server `ManagedRuntime` selects `MagnetarInterim.exe` (.NET 10) or `MagnetarLegacy.exe` (.NET Framework 4.8); on non-Windows hosts a `NetFramework48` selection is silently downgraded to `DotNet10` so a `server.json` moved across platforms still launches. On Linux/macOS, SteamCMD uses `+@sSteamCmdForcePlatformType windows` to fetch the Windows DS binaries, and exec bits are applied via `File.SetUnixFileMode`; Quasar-managed SteamCMD's `linux64/` runtime is prepared during startup readiness and preferred for `NativeLibrarySearchPaths` so `steamclient.so` resolution does not depend on a desktop Steam install. `DedicatedServer64` validation requires the launcher plus core assemblies (`SpaceEngineers.Game.dll`, `VRage.dll`, `Sandbox.Game.dll`) so thin or corrupt DS folders are rejected earlier. Archive entries that resolve outside the extraction root are rejected.
