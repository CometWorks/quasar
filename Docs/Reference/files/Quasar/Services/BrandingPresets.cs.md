# Quasar/Services/BrandingPresets.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
`BrandingPresets` is a static catalogue of the four built-in UI theme presets (Quasar Default, Midnight Blue, Slate, High Contrast). It exposes `GetLightPalette` / `GetDarkPalette` factory methods that layer identity/surface colour overrides on top of the base `ThemePalette.QuasarLight()` / `QuasarDark()` palettes, keeping all variants internally coherent. `BrandingPresetDefinition` is the companion display record.

## Structure
Namespace: `Quasar.Services`

**`BrandingPresetDefinition`** — `sealed record(string Id, string DisplayName)`

**`BrandingPresets`** — `static class`

| Member | Notes |
|--------|-------|
| `QuasarId`, `MidnightId`, `SlateId`, `HighContrastId` | String constants for preset IDs |
| `All` | `IReadOnlyList<BrandingPresetDefinition>` of the four presets |
| `IsKnownPreset(string?)` | Case-insensitive membership check |
| `GetLightPalette(string presetId)` | Returns the light `ThemePalette` for the given preset ID |
| `GetDarkPalette(string presetId)` | Returns the dark `ThemePalette` for the given preset ID |

Private palette factory methods: `MidnightLight/Dark`, `SlateLight/Dark`, `HighContrastLight/Dark` — each clones the base palette and patches specific colour tokens.

## Dependencies
- `Quasar/Models/ThemePalette.cs` — `QuasarLight()`, `QuasarDark()` base palettes
- `Quasar/Models/QuasarTheme.cs` (indirectly, base palettes reference it)
