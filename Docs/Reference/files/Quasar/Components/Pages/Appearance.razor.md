# Quasar/Components/Pages/Appearance.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page (`/settings/appearance`) for live branding customisation. Allows editing the app name, subtitle, theme preset, individual palette colors (light and dark), logos (light-mode and dark-mode), and favicon. Changes to logos and favicon take effect immediately on save to disk; palette and name changes apply across all open sessions after the top-level Save is clicked.

## Structure
- **Route:** `@page "/settings/appearance"`
- **Implements:** `IDisposable`
- **Injected services:** `BrandingService`, `ISnackbar`
- **State:** `_draft` — a `BrandingSettings` clone initialised from `BrandingService.GetSettings()` and mutated locally until Save.
- **UI sections:**
  - Branding card: `MudTextField` for `AppName` and `AppSubtitle`.
  - Preset theme card: `MudSelect` over `BrandingPresets.All`; selecting a preset replaces both `LightPalette` and `DarkPalette` in the draft; a disabled "Custom (edited)" sentinel appears when any color was manually changed.
  - Colors section: `MudTabs` (Light / Dark) → `RenderPaletteEditor` render fragment that iterates six `ColorGroup`s (Identity, Surfaces, Navigation, Text, Lines, Status) and renders a `MudColorPicker` per field using `ColorPickerView.Spectrum`.
  - Logo section: two upload areas with `MudFileUpload` (accepts .png/.jpg/.jpeg/.webp/.svg, max 10 MB) — one for light-mode logo, one for dark-mode logo; each shows an `<img>` preview.
  - Favicon section: `MudFileUpload` (accepts .ico/.png) with preview image.
  - Action bar: Save Changes (`BrandingService.SaveAsync`) and Reset to Quasar Default (`BrandingService.ResetToDefaultAsync`) buttons.
- **Key methods:**
  - `RenderPaletteEditor(ThemePalette)` — `RenderFragment` factory producing grouped `MudColorPicker` grids.
  - `ApplyPreset(string)` — copies preset palettes into the draft and sets `PresetId`.
  - `HandleColorChanged(ThemePalette, ColorField, MudColor)` — writes hex color back via the field's `Set` action and clears `PresetId` to mark as custom.
  - `UploadLogoAsync(IBrowserFile, bool)` / `UploadFaviconAsync(IBrowserFile)` — stream file to `BrandingService`, then call `SyncAssetPaths()` to keep the draft's path fields in sync.
  - `SyncAssetPaths()` — pulls updated asset paths from `BrandingService.Settings` into `_draft` so a subsequent Save does not revert the paths.
  - `HandleBrandingChanged()` — re-renders on external branding changes.
- **Private types:**
  - `ColorField` record — label, getter `Func<ThemePalette, string>`, setter `Action<ThemePalette, string>`.
  - `ColorGroup` record — title + list of `ColorField`.

## Dependencies
- [`Quasar/Services/BrandingService.cs`](../../Services/BrandingService.cs.md)
- `Quasar/Services/BrandingSettings.cs` (model)
- [`Quasar/Services/BrandingPresets.cs`](../../Services/BrandingPresets.cs.md) (preset catalog)
- `Quasar/Services/ThemePalette.cs` (palette model)
- MudBlazor (`MudColorPicker`, `MudFileUpload`, `MudSelect`, `MudTabs`, `MudTabPanel`, `MudTextField`)

## Notes
- Logo and favicon uploads are immediately persisted by `BrandingService`; paths are synced back into the draft via `SyncAssetPaths()` so a later Save does not overwrite them with stale values.
- `MudColorOutputFormats.Hex` is used when writing colors back, ensuring consistent hex string storage.
- The page listens to `BrandingService.Changed` so if another session saves branding externally the UI re-renders.
