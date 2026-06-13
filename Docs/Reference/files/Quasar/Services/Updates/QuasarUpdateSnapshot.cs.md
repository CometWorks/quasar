# Quasar/Services/Updates/QuasarUpdateSnapshot.cs

**Module:** Quasar.Services.Core  **Kind:** class (DTO)  **Tier:** 2

## Summary

DTO records and status enum used by the update service and Updates page. `QuasarUpdateSnapshot` describes the current checker state, current UI worker and Bootstrap versions, active prerelease/staging settings, appsettings conflict state, last check/change timestamps, the selected web release, all selectable web releases, and optional web/Bootstrap candidates. `QuasarUpdateCandidate` describes one release asset and whether it is current, newer, prerelease, staged, or requires privileged installation.

## Structure

Namespace: `Quasar.Services.Updates`

**`QuasarUpdateSnapshot`** — sealed record.

| Property | Description |
|---|---|
| `Enabled` / `SupportedPlatform` | Whether update checks are configured and whether this platform can stage/activate updates. |
| `CurrentVersion` | Running Quasar UI worker version from `WebServiceOptions`. |
| `CurrentBootstrapVersion` | Bootstrap launcher version passed through `QUASAR_BOOTSTRAP_VERSION` when the worker is managed by Bootstrap. |
| `IncludePrerelease` / `AutoStageWebUpdates` | Live update stream and UI-worker staging mode. |
| `Status` / `Message` | Current `QuasarUpdateStatus` and display message. |
| `LastCheckedUtc` / `LastChangedUtc` | Timestamps for release checks and snapshot changes. |
| `WebReleases` / `SelectedWebVersion` / `Web` | Selectable UI-worker release list, selected version key, and selected candidate. Older entries are allowed so the UI can stage rollbacks. |
| `Bootstrap` | Optional newer launcher update candidate. |
| `AppSettingsConflict` / `AppSettingsConflictPath` / `AppSettingsConflictMessage` | Whether staged appsettings rollover needs manual resolution, plus the staged conflict file path and user-facing merge message. |

**`QuasarUpdateCandidate`** — sealed record with release version, asset name/download URL, checksum URL/expected SHA-256, size, publish time, staged directory, availability/staged flags, privileged-install flag, and current/newer/prerelease flags.

**`QuasarUpdateStatus`** — enum: `Idle`, `Checking`, `UpdateQueued`, `Staging`, `Staged`, `Activating`, `Failed`.

## Dependencies

- [`Quasar/Services/Updates/QuasarUpdateService.cs`](QuasarUpdateService.cs.md) — produces snapshots and candidates
- [`Quasar/Components/Pages/Updates.razor`](../../Components/Pages/Updates.razor.md) — displays snapshots and maps status to UI state
