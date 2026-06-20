# Quasar/Services/Updates/QuasarUpdateService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 2

## Summary

Hosted service that checks Quasar GitHub releases on Linux and Windows, discovers selectable UI-worker releases plus newer launcher assets from a unified combined release, verifies `SHA256SUMS`, stages the selected UI worker under the Quasar updates directory, resolves staged `appsettings.json` through a three-way merge, and activates a staged worker by promoting it into `ManagedRuntime/WebService/<version>` before writing `active-release.json` for Bootstrap. It can also write a Bootstrap update request file when an admin forces launcher activation from the Updates page. It keeps a thread-safe `QuasarUpdateSnapshot` for the UI, raises `Changed` whenever status moves, and persists the operator-controlled prerelease stream and UI auto-stage flags.

## Structure

Namespace: `Quasar.Services.Updates`

**`QuasarUpdateService`** — sealed `BackgroundService`.

| Member | Description |
|---|---|
| `GetSnapshot()` | Returns a defensive copy of the current update status and candidates. |
| `SetIncludePrereleaseAsync(bool, ct)` | Updates the live prerelease-stream flag, writes `Quasar:Updates:IncludePrerelease` to the data-directory `appsettings.json`, and publishes a status message. |
| `SetAutoStageWebUpdatesAsync(bool, ct)` | Updates and persists `Quasar:Updates:AutoStageWebUpdates`, switching checks between automatic download/stage and manual queue-only mode. |
| `SelectWebReleaseAsync(version, ct)` | Selects one UI-worker release from the discovered list, including older versions used for rollback. |
| `CheckNowAsync(ct)` | Checks the configured GitHub releases endpoint, builds all selectable non-draft UI releases containing the configured asset, finds only newer Bootstrap candidates, updates the selected UI version, and auto-stages a newer UI version only when auto-stage mode is enabled. |
| `StageWebUpdateAsync(ct)` / `StageWebUpdateAsync(forceAppSettingsOverride, ct)` | Downloads the selected web asset, resolves/verifies its SHA-256 checksum from `SHA256SUMS`, extracts it into `Updates/Staged/<version>`, validates required web layout files, resolves staged `appsettings.json`, and marks it staged. Current-version staging is rejected; older selected releases can be staged for rollback. |
| `ReadAppSettingsConflictTextAsync(ct)` / `ResolveAppSettingsConflictAsync(text, ct)` | Supports the Updates page conflict editor by reading the staged conflict file, validating the resolved JSON, persisting it, and marking the release staged once conflict markers are gone. |
| `ActivateStagedWebUpdateAsync(ct)` | Copies the staged payload into `ManagedRuntime/WebService/<version>`, syncs the resolved active `appsettings.json` back to the install directory when `QUASAR_INSTALL_DIR` is known, writes `QuasarActiveReleasePointer` to the active-release path so Bootstrap can swap workers, and clears old staged payloads. Staged older UI releases are valid rollback targets. |
| `RequestBootstrapUpdateActivationAsync(ct)` | Validates that a newer Bootstrap candidate exists and the worker is launcher-managed, then writes `Updates/bootstrap-update-request.json` for Bootstrap to consume and publishes an activating status message. |
| `ExecuteAsync(stoppingToken)` | Runs an initial delayed check and repeats every configured interval while enabled. |
| `GetReleasesAsync(ct)` / `BuildCandidates(...)` | Calls GitHub releases API (`per_page=100`), ignores drafts, optionally includes prereleases, and maps matching release assets into UI/Bootstrap candidates. |
| `PersistUpdateBooleanAsync(...)` / `GetOrCreateObject(...)` | Preserves or creates the data-directory `appsettings.json` object graph and atomically writes update-page boolean settings. |
| `ResolveStagedAppSettingsAsync(...)` / `MergeObjects(...)` | Uses `Updates/appsettings.base.json` as the release-base ancestor, overlays current install-directory values onto the new release file when possible, writes git-style conflict markers when both sides changed the same setting, and supports force-staging release defaults. |
| `ResolveExpectedSha256Async(...)` / `VerifySha256Async(...)` | Reads `SHA256SUMS` on demand and validates downloaded assets. |
| `ExtractArchive(...)` | Extracts `.zip`, `.tar.gz`, or `.tgz` Quasar UI archives. |

Private nested DTOs `GitHubRelease` and `GitHubAsset` model the small subset of GitHub API JSON the service needs.

## Dependencies

- [`Quasar/Services/Updates/QuasarUpdateOptions.cs`](QuasarUpdateOptions.cs.md) — repository, asset, and interval settings
- [`Quasar/Services/Updates/QuasarUpdateSnapshot.cs`](QuasarUpdateSnapshot.cs.md) — published status and candidate records
- `Quasar/Services/WebServiceOptions.cs` — current UI worker version and Bootstrap version passed by the launcher
- [`Magnetar.Protocol/Runtime/MagnetarPaths.cs`](../../../Magnetar.Protocol/Runtime/MagnetarPaths.cs.md) — update staging/cache/active-release and managed web-release paths
- `Magnetar.Protocol/Runtime/QuasarActiveReleasePointer.cs` — activation pointer payload
- `Magnetar.Protocol/Runtime/QuasarReleaseVersion.cs` — normalized/prerelease-aware update comparison
- `Magnetar.Protocol/Runtime/QuasarWebReleaseLayout.cs` — staged web archive validation
- [`Quasar/Services/AtomicFileWriter.cs`](../AtomicFileWriter.cs.md) — atomic persistence for the data-directory settings override
- `IHttpClientFactory`, `BackgroundService`, `System.Text.Json`, `System.Text.Json.Nodes`, `System.Security.Cryptography`

## Notes

UI-worker activation stays explicit from the Updates page on both Linux and Windows. Auto-stage mode only downloads/stages the newer selected UI release; manual mode queues releases until the operator stages one. Launcher updates are reported in the UI; normally Bootstrap installs them automatically from the platform asset (`quasar-installer-linux.tar.gz` on Linux, `quasar-installer-windows.zip` on Windows), but a launcher-managed worker can request immediate activation by writing `Updates/bootstrap-update-request.json`. Bootstrap consumes that request and runs its normal verified self-update path, restarting on Linux via systemd exit-75 or on Windows by spawning a detached replacement launcher. Staged UI payloads are rejected before activation when core Blazor/MudBlazor/app static assets are missing or `appsettings.json` has unresolved conflict markers; older staged UI payloads are allowed so the operator can roll back the worker. Active UI releases live outside `Updates/Staged`, so the Updates folder only contains transient staged payloads plus the active pointer, the Bootstrap update request file, and the stored `appsettings.json` merge base. The prerelease switch affects the running worker immediately; Bootstrap reads the persisted data-directory override after its next restart.
