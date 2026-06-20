# Quasar/Services/ManagedRuntimeOptions.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
`ManagedRuntimeOptions` is the configuration record for all managed-runtime paths and download URLs. It is populated from environment variables (highest priority), then the `Quasar:ManagedRuntime` config section, then sensible defaults. The defaults resolve the latest full Magnetar GitHub release asset by OS-specific wildcard and point SteamCMD to Valve's CDN.

## Structure
Namespace: `Quasar.Services`

**`ManagedRuntimeOptions`** — `sealed class` (init-only properties)

| Property | Default / env var override |
|----------|---------------------------|
| `MagnetarArchiveUrl` | Empty by default; direct URL override via `QUASAR_MAGNETAR_ARCHIVE_URL` |
| `MagnetarReleaseApiUrl` | `https://api.github.com/repos/CometWorks/magnetar/releases/latest`; `QUASAR_MAGNETAR_RELEASE_API_URL` |
| `MagnetarArchiveAssetPattern` | Windows: `MagnetarForWindows-*.7z`; Linux/other: `MagnetarForLinux-*.7z`; `QUASAR_MAGNETAR_ARCHIVE_ASSET_PATTERN` |
| `MagnetarInstallDirectory` | `MagnetarPaths.GetQuasarManagedMagnetarInstallDirectory()`; `QUASAR_MAGNETAR_INSTALL_DIR` |
| `SteamCmdArchiveUrl` | Windows: ZIP from Valve; Linux: `.tar.gz` from Valve; `QUASAR_STEAMCMD_ARCHIVE_URL` |
| `SteamCmdInstallDirectory` | `MagnetarPaths.GetQuasarManagedSteamCmdInstallDirectory()`; `QUASAR_STEAMCMD_INSTALL_DIR` |
| `DedicatedServerInstallDirectory` | `MagnetarPaths.GetQuasarManagedDedicatedServerInstallDirectory()`; `QUASAR_DS_INSTALL_DIR` |
| `DedicatedServer64OverridePath` | Empty; `QUASAR_DS64_PATH` or `DS64` |
| `SteamCmdPath` | Empty; `QUASAR_STEAMCMD_PATH` |
| `PreferManagedDedicatedServerInstall` | `true`; `QUASAR_PREFER_MANAGED_DS` |

`Create(IConfiguration)` is the factory method used during DI registration.

## Dependencies
- `Magnetar.Protocol.Runtime` — `MagnetarPaths`
- ASP.NET Core `IConfiguration`
