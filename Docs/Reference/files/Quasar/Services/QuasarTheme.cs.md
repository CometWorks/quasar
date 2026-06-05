# Quasar/Services/QuasarTheme.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Provides the default MudBlazor `MudTheme` instance for the Quasar UI, defining both light and dark color palettes with a neutral/monochrome visual language (near-black primary, gray secondary, zinc-scale surfaces and dividers). Border radius is set to 6 px globally.

## Structure
**Namespace:** `Quasar.Services`

**Type:** `QuasarTheme` (static class)

| Member | Description |
|---|---|
| `Default` (static readonly `MudTheme`) | Fully configured theme with `PaletteLight` and `PaletteDark`. |

Key palette highlights:
- Light: primary `#111111`, background `#f5f5f5`, surface `#ffffff`
- Dark: primary `#f5f5f5`, background `#18181b`, surface `#232326`
- Both: success green, amber warning, red error, gray info

## Dependencies
- MudBlazor (`MudTheme`, `PaletteLight`, `PaletteDark`, `LayoutProperties`)
