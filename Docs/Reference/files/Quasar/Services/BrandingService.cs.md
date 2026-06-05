# Quasar/Services/BrandingService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
`BrandingService` is the singleton store for all branding and theme configuration. It persists settings to `branding.json` in the Quasar data directory, saves uploaded logo and favicon assets into `{WebRootPath}/branding/`, and live-reloads after external edits via a debounced `FileSystemWatcher`. It also builds the `MudTheme` consumed by the Blazor layout.

## Structure
Namespace: `Quasar.Services`

**`BrandingService`** — `sealed class` : `IDisposable`

| Member | Notes |
|--------|-------|
| `event Action? Changed` | Raised after any settings mutation or disk reload |
| `Settings` | Lock-guarded live reference to current `BrandingSettings` |
| `GetSettings()` | Returns a deep-cloned copy safe for UI draft editing |
| `BuildMudTheme()` | Constructs `MudTheme` from current light/dark palettes and `QuasarTheme.Default.LayoutProperties` |
| `SaveAsync(BrandingSettings, CancellationToken)` | Normalises, serialises, writes via `AtomicFileWriter`, updates in-memory state, fires `Changed` |
| `SaveLogoAsync(bool isDark, Stream, string extension, CancellationToken)` | Writes logo asset to `{brandingDir}/logo-dark|light.{ext}?v={cachebust}` and calls `SaveAsync` |
| `SaveFaviconAsync(Stream, string extension, CancellationToken)` | Writes favicon asset and calls `SaveAsync` |
| `ResetToDefaultAsync(CancellationToken)` | Saves a default-normalised `BrandingSettings` |
| `Dispose()` | Disposes watcher and debounce CTS |

Internal file watcher uses a 250 ms debounce (`ScheduleReload`) and compares a JSON snapshot to suppress no-op reloads.

## Dependencies
- [`Quasar/Services/AtomicFileWriter.cs`](AtomicFileWriter.cs.md) — atomic JSON persistence
- [`Quasar/Models/BrandingSettings.cs`](../Models/BrandingSettings.cs.md) — settings model, `Normalize`, `Clone`
- `Quasar/Models/ThemePalette.cs` — `ToMudPaletteLight()`, `ToMudPaletteDark()`
- `Quasar/Models/QuasarTheme.cs` — `Default.LayoutProperties`
- `Magnetar.Protocol.Runtime` — `MagnetarPaths` (file locations)
- MudBlazor — `MudTheme`, `PaletteLight`, `PaletteDark`

## Notes
Assets are written with a cache-busting query string (`?v={unix-ms}`). Old assets sharing the same `baseName` are deleted before writing the new one. Extension sanitisation strips any invalid filename characters. The snapshot-based change detection prevents spurious `Changed` events when Quasar itself writes the file (the watcher sees its own writes).
