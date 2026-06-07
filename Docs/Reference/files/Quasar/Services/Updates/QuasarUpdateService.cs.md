# Quasar/Services/Updates/QuasarUpdateService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 2

## Summary

Hosted service that checks Quasar GitHub releases on Linux, discovers newer UI-worker and launcher assets from separate release tags, verifies `SHA256SUMS`, stages the UI worker under the Quasar updates directory, and activates a staged worker by writing `active-release.json` for Bootstrap. It keeps a thread-safe `QuasarUpdateSnapshot` for the UI and raises `Changed` whenever status moves.

## Structure

Namespace: `Quasar.Services.Updates`

**`QuasarUpdateService`** — sealed `BackgroundService`.

| Member | Description |
|---|---|
| `GetSnapshot()` | Returns a defensive copy of the current update status and candidates. |
| `CheckNowAsync(ct)` | Checks the configured GitHub releases endpoint, independently finds the newest non-draft UI and Bootstrap releases containing their configured asset names, builds candidates for newer versions, and auto-stages the UI asset when available. |
| `StageWebUpdateAsync(ct)` | Downloads the queued web asset, verifies its SHA-256 checksum, extracts it into `Updates/Staged/<version>`, validates required web layout files, and marks it staged. |
| `ActivateStagedWebUpdateAsync(ct)` | Writes `QuasarActiveReleasePointer` to the active-release path so Bootstrap can swap workers. |
| `ExecuteAsync(stoppingToken)` | Runs an initial delayed check and repeats every configured interval while enabled. |
| `GetLatestReleaseWithAssetAsync(assetName, ct)` | Calls GitHub releases API (`per_page=100`), ignores drafts, optionally includes prereleases, and returns the newest release containing the requested asset. |
| `GetChecksumsAsync(...)` / `VerifySha256Async(...)` | Reads `SHA256SUMS` and validates downloaded assets. |
| `ExtractArchive(...)` | Extracts `.zip`, `.tar.gz`, or `.tgz` Quasar UI archives. |

Private nested DTOs `GitHubRelease` and `GitHubAsset` model the small subset of GitHub API JSON the service needs.

## Dependencies

- [`Quasar/Services/Updates/QuasarUpdateOptions.cs`](QuasarUpdateOptions.cs.md) — repository, asset, and interval settings
- [`Quasar/Services/Updates/QuasarUpdateSnapshot.cs`](QuasarUpdateSnapshot.cs.md) — published status and candidate records
- `Quasar/Services/WebServiceOptions.cs` — current UI worker version and Bootstrap version passed by the launcher
- [`Magnetar.Protocol/Runtime/MagnetarPaths.cs`](../../../Magnetar.Protocol/Runtime/MagnetarPaths.cs.md) — update staging/cache/active-release paths
- `Magnetar.Protocol/Runtime/QuasarActiveReleasePointer.cs` — activation pointer payload
- `Magnetar.Protocol/Runtime/QuasarReleaseVersion.cs` — normalized/prerelease-aware update comparison
- `Magnetar.Protocol/Runtime/QuasarWebReleaseLayout.cs` — staged web archive validation
- `IHttpClientFactory`, `BackgroundService`, `System.Text.Json`, `System.Security.Cryptography`

## Notes

Linux UI-worker activation stays explicit from the Updates page. Launcher updates are reported in the UI, but the launcher itself installs them automatically from `quasar-linux-x64.tar.gz` and restarts under systemd. Staged UI payloads are rejected before activation when core Blazor/MudBlazor/app static assets are missing.
