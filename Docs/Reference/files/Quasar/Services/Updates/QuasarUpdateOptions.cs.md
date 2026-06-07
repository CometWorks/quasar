# Quasar/Services/Updates/QuasarUpdateOptions.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 2

## Summary

Configuration record for Quasar's GitHub-release update checker. It controls whether checks run, which repository is queried, whether prereleases are eligible, how often automatic checks run, and which per-OS release asset names represent the web worker and Bootstrap packages. `WebAssetName` and `BootstrapAssetName` computed properties select the Windows or Linux variant based on `OperatingSystem.IsWindows()`. `IncludePrerelease` is mutable so the Updates page can change the active worker's release stream immediately.

## Structure

Namespace: `Quasar.Services.Updates`

**`QuasarUpdateOptions`** — sealed class with init-only properties.

| Property | Default / env var override |
|---|---|
| `Enabled` | `true`; `QUASAR_UPDATES_ENABLED` |
| `Owner` | `viktor-ferenczi`; `QUASAR_UPDATES_OWNER` |
| `Repository` | `Quasar`; `QUASAR_UPDATES_REPOSITORY` |
| `IncludePrerelease` | `false`; `QUASAR_UPDATES_INCLUDE_PRERELEASE` |
| `CheckInterval` | 300 seconds minimum 60; `QUASAR_UPDATES_CHECK_INTERVAL_SECONDS` |
| `LinuxWebAssetName` | `quasar-web-linux-x64.tar.gz`; `QUASAR_UPDATES_LINUX_WEB_ASSET` |
| `LinuxBootstrapAssetName` | `quasar-linux-x64.tar.gz`; `QUASAR_UPDATES_LINUX_BOOTSTRAP_ASSET` |
| `WindowsWebAssetName` | `quasar-web-win-x64.zip`; `QUASAR_UPDATES_WINDOWS_WEB_ASSET` |
| `WindowsBootstrapAssetName` | `quasar-win-x64.zip`; `QUASAR_UPDATES_WINDOWS_BOOTSTRAP_ASSET` |
| `WebAssetName` *(computed)* | resolves to `WindowsWebAssetName` on Windows, `LinuxWebAssetName` otherwise |
| `BootstrapAssetName` *(computed)* | resolves to `WindowsBootstrapAssetName` on Windows, `LinuxBootstrapAssetName` otherwise |

`Create(IConfiguration)` loads environment variables first, then `Quasar:Updates`, then defaults. `IncludePrerelease` can also be persisted by `QuasarUpdateService` into the data-directory `appsettings.json`, which both worker and Bootstrap read on startup.

## Dependencies

- ASP.NET Core `IConfiguration`
