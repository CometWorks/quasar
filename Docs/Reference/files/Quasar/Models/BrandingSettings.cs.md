# Quasar/Models/BrandingSettings.cs

**Module:** Quasar.Models  **Kind:** class  **Tier:** 1

## Summary
Defines the persistent branding and theme configuration serialized to `branding.json`. Contains two sealed classes: `BrandingSettings` (app identity + palette references) and `ThemePalette` (a flat string-valued mirror of MudBlazor's `PaletteLight`/`PaletteDark` plus Quasar hover-list colors). Both classes support cloning and normalization with fallback to the built-in Quasar defaults.

## Structure
Namespace: `Quasar.Models`

**`BrandingSettings`** — sealed class

| Member | Description |
|---|---|
| `PresetId` | Null = hand-edited; non-null = named built-in preset (default `BrandingPresets.QuasarId`) |
| `AppName` | Display name of the application (default `"Quasar"`) |
| `AppSubtitle` | Subtitle shown in the UI (default `"Supervisor control plane"`) |
| `LogoLightPath` | Optional relative path to the light-mode logo asset |
| `LogoDarkPath` | Optional relative path to the dark-mode logo asset |
| `FaviconPath` | Optional relative path to the favicon asset |
| `LightPalette` | `ThemePalette` for the light theme (defaults to `ThemePalette.QuasarLight()`) |
| `DarkPalette` | `ThemePalette` for the dark theme (defaults to `ThemePalette.QuasarDark()`) |
| `Clone()` | Deep copy |
| `static Normalize(BrandingSettings?)` | Returns a fully populated instance; null input yields the out-of-the-box defaults |

**`ThemePalette`** — sealed class; 28 hex-colour string properties (Primary, Secondary, Hover*, Background, Surface, Drawer*, Appbar*, Text*, Lines*, Divider*, Info, Success, Warning, Error, plus corresponding ContrastText variants). `HoverBackground` / `HoverContrastText` drive Quasar's table/list row hover colours; the Quasar dark default hover background is `#bfbfbfff`.

Key methods:
- `static QuasarLight()` / `static QuasarDark()` — extract hex values from `QuasarTheme.Default` and attach Quasar-specific hover-list defaults
- `Clone()` — deep copy
- `static Normalize(ThemePalette?, ThemePalette fallback)` — fills blank fields from fallback
- `ToMudPaletteLight()` / `ToMudPaletteDark()` — convert back to MudBlazor palette objects

## Dependencies
- [`Quasar/Services/BrandingPresets.cs`](../Services/BrandingPresets.cs.md) (references `BrandingPresets.QuasarId`)
- [`Quasar/Services/QuasarTheme.cs`](../Services/QuasarTheme.cs.md) (references `QuasarTheme.Default.PaletteLight` / `.PaletteDark`)
- MudBlazor (`PaletteLight`, `PaletteDark`)

## Notes
Round-tripping hex colours through plain strings avoids `MudColor` serialization complexity. `Normalize` is the canonical entry point when loading from disk — callers should not use raw deserialized instances directly.
