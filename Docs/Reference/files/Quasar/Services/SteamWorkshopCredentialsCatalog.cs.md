# Quasar/Services/SteamWorkshopCredentialsCatalog.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Stores and retrieves the Steam Workshop Web API key with ASP.NET Core Data Protection encryption. The credential file lives at `<QuasarDir>/workshop-options.json`; new writes store only the DPAPI-protected `ProtectedWebApiKey` field. On load the service transparently migrates legacy plaintext `WebApiKey` fields to protected storage. A file-system watcher with 250 ms debounce reloads credentials when the file is externally modified.

## Structure
**Namespace:** `Quasar.Services`

**Types:**
- `SteamWorkshopCredentialsCatalog` (sealed class, implements `IDisposable`) — the catalog service
- `SteamWorkshopCredentials` (sealed class) — DTO with `WebApiKey` string, `Clone()`, and static `Normalize()`

`SteamWorkshopCredentialsCatalog` members:
| Member | Description |
|---|---|
| `event Action? Changed` | Fired after any credential change. |
| `HasWebApiKey` | Thread-safe property; true when key is non-empty. |
| `GetCredentials()` | Returns a clone of the current credentials. |
| `SaveAsync(credentials, ct)` | Normalizes, protects key, writes JSON atomically, updates in-memory state. |
| `Dispose()` | Stops watcher and cancels debounce. |

Private:
- `LoadCredentials(out requiresMigration)` — reads JSON, tries `ProtectedWebApiKey` first; falls back to plaintext `WebApiKey` and sets migration flag
- `MigrateLegacyPlaintextAsync()` — re-saves current credentials to overwrite plaintext with protected form (fire-and-forget)
- `PersistedCredentials` (private nested sealed class) — JSON shape on disk; `FromCredentials()` and `ToCredentials()` handle protect/unprotect
- File-watcher + debounce pattern identical to other catalogs

## Dependencies
- `Magnetar.Protocol.Runtime.MagnetarPaths` (`GetQuasarWorkshopOptionsPath`)
- `Magnetar.Protocol.Runtime.AtomicFileWriter`
- `Microsoft.AspNetCore.DataProtection.IDataProtectionProvider` / `IDataProtector`
- `System.Text.Json`

## Notes
- Data Protection purpose string is `"Quasar.SteamWorkshopCredentials.v1"` — if the DPAPI keyring is rotated or replaced, unprotect will fail and the key is cleared with a warning (not silently retained).
- Migration is fire-and-forget from the constructor; failure is logged as a warning and the plaintext key remains usable for the session.
- Thread safety: `_credentials` and `_snapshot` guarded by `_sync`.
