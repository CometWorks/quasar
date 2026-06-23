# Plan: MudBlazor Whitelabel / Theme Configurator

## Context
Quasar currently has a static theme (`QuasarTheme.cs`) with hardcoded light/dark palettes, and hardcoded branding (app name, subtitle, logos, favicon) scattered across `MainLayout.razor` and `App.razor`. The goal is a persistent, UI-driven theme and branding configurator — custom colors, logos, favicon — with the existing "Quasar Default" still available as a preset. Follows the existing file-based JSON storage pattern (no database).

---

## Architecture

```
BrandingService (singleton)
  └── loads/saves  <quasar-data-root>/branding.json
  └── exposes      BuildMudTheme() → MudTheme
  └── fires        Changed event → MainLayout calls StateHasChanged

ThemePreferenceService (scoped, minimal change)
  └── Theme property delegates to BrandingService.BuildMudTheme()

MainLayout.razor
  └── subscribes to BrandingService.Changed for live re-render
  └── reads BrandingService.Settings for app name, subtitle, logo paths

BrandingHeadContent.razor  (new, inside MainLayout render tree)
  └── <HeadContent> with reactive favicon href

Appearance.razor  (new settings page /settings/appearance)
  └── preset picker + color pickers + file upload
```

---

## Files to Create

### 1. `Quasar/Models/BrandingSettings.cs`
Data contract serialized to `branding.json`.

```csharp
public sealed class BrandingSettings
{
    public string? PresetId { get; set; }        // "quasar" | "midnight" | "slate" | null (custom)
    public string AppName { get; set; } = "Quasar";
    public string AppSubtitle { get; set; } = "Supervisor control plane";
    public string? LogoLightPath { get; set; }   // e.g. "/branding/logo-light.png?v=..."
    public string? LogoDarkPath { get; set; }
    public string? FaviconPath { get; set; }     // includes cache-bust query string
    public ThemePalette LightPalette { get; set; } = ThemePalette.QuasarLight();
    public ThemePalette DarkPalette { get; set; } = ThemePalette.QuasarDark();

    public BrandingSettings Clone() { ... }
    public static BrandingSettings Normalize(BrandingSettings? s) { ... }
}

public sealed class ThemePalette
{
    // Same fields as QuasarTheme.cs palette: Primary, PrimaryContrastText,
    // Secondary, Background, BackgroundGray, Surface, DrawerBackground,
    // DrawerText, DrawerIcon, AppbarBackground, AppbarText, TextPrimary,
    // TextSecondary, LinesDefault, LinesInputs, TableLines, Divider,
    // DividerLight, Info, InfoContrastText, Success, SuccessContrastText,
    // Warning, WarningContrastText, Error, ErrorContrastText
    // All string hex values.

    public static ThemePalette QuasarLight() { /* copy from QuasarTheme.Default.PaletteLight */ }
    public static ThemePalette QuasarDark()  { /* copy from QuasarTheme.Default.PaletteDark  */ }
    public ThemePalette Clone() { ... }
    public PaletteLight ToMudPaletteLight() { /* maps fields to new PaletteLight { ... } */ }
    public PaletteDark  ToMudPaletteDark()  { /* maps fields to new PaletteDark  { ... } */ }
}
```

`Normalize` fills null fields from `QuasarLight()` / `QuasarDark()` defaults.

### 2. `Quasar/Services/BrandingPresets.cs`
```csharp
public sealed record BrandingPresetDefinition(string Id, string DisplayName);

public static class BrandingPresets
{
    public static IReadOnlyList<BrandingPresetDefinition> All { get; } = [ ... ];

    // "quasar": reads directly from QuasarTheme.Default — single source of truth
    // "midnight": Primary #1e3a5f/#93c5fd, Background #f0f4ff/#0f1729
    // "slate": Primary #475569/#cbd5e1, Background #f8fafc/#1e293b
    public static ThemePalette GetLightPalette(string presetId) { ... }
    public static ThemePalette GetDarkPalette(string presetId)  { ... }
}
```

### 3. `Quasar/Services/BrandingService.cs`
Singleton. Mirrors the `DiscordOptionsCatalog` file-watch pattern.

- Constructor: `ILogger<BrandingService>`, `IWebHostEnvironment`
- Stores data at `MagnetarPaths.GetQuasarBrandingPath()`
- Uploads to `{WebRootPath}/branding/` (created on first use)
- Key members:
  - `BrandingSettings Settings { get; }` — live reference (lock-protected)
  - `MudTheme BuildMudTheme()` — builds from current settings, copies `LayoutProperties` from `QuasarTheme.Default`
  - `event Action? Changed`
  - `Task SaveAsync(BrandingSettings settings)`
  - `Task SaveLogoAsync(bool isDark, Stream data, string ext, CancellationToken ct)` — writes file, appends `?v={timestamp}` to path in settings, saves
  - `Task SaveFaviconAsync(Stream data, string ext, CancellationToken ct)` — same pattern
  - `Task ResetToDefaultAsync(CancellationToken ct)` — saves `Normalize(null)` (preset "quasar")
- FileSystemWatcher debounced reload on `branding.json` changes

### 4. `Quasar/Components/Layout/BrandingHeadContent.razor`
Reactive favicon — placed inside `MainLayout.razor` render tree so it participates in Blazor Server's interactive render.

```razor
@inject BrandingService BrandingService
@implements IDisposable

<HeadContent>
    <link rel="icon" type="image/x-icon"
          href="@(BrandingService.Settings.FaviconPath ?? "/Quasar.ico")" />
</HeadContent>

@code {
    protected override void OnInitialized() => BrandingService.Changed += OnChanged;
    public void Dispose() => BrandingService.Changed -= OnChanged;
    void OnChanged() => _ = InvokeAsync(StateHasChanged);
}
```

The `?v=...` timestamp in `FaviconPath` busts browser favicon cache without a full page reload.

### 5. `Quasar/Components/Pages/Appearance.razor`
Route: `@page "/settings/appearance"`

Layout:
```
MudText h4 "Appearance"

MudGrid
  [col 4] Branding card
    MudTextField: App Name
    MudTextField: Subtitle

  [col 8] Preset Theme card
    MudSelect<string>: preset → applies palette to draft immediately

  [col 12] Colors card
    MudTabs: Light / Dark
      [each tab] MudGrid of MudColorPicker bound to draft palette fields
      Group: Identity (Primary, Secondary)
      Group: Surfaces (Background, BackgroundGray, Surface)
      Group: Navigation (Drawer*, Appbar*)
      Group: Text (TextPrimary, TextSecondary)
      Group: Lines (LinesDefault, LinesInputs, TableLines, Divider, DividerLight)
      Group: Status (Info, Success, Warning, Error + contrast texts)

  [col 6] Logo card
    img preview (light logo)
    MudFileUpload accept=".png,.jpg,.jpeg,.webp,.svg": light logo
    img preview (dark logo)
    MudFileUpload: dark logo

  [col 6] Favicon card
    img preview
    MudFileUpload accept=".ico,.png": favicon

  [col 12] Action row
    MudButton Filled Primary: "Save Changes"
    MudButton Text: "Reset to Quasar Default"
```

Draft state: `_draft = BrandingService.GetSettings()` clone on init. Preset selection copies palettes into draft. Color picker changes null out `_draft.PresetId`. Save calls `BrandingService.SaveAsync(_draft)`. File uploads call the relevant `Save*Async` directly (no draft needed — immediate effect).

---

## Files to Modify

### 6. `Magnetar.Protocol/Runtime/MagnetarPaths.cs`
Add two methods after `GetQuasarDiscordOptionsPath()`:
```csharp
public static string GetQuasarBrandingPath() =>
    Path.Combine(GetQuasarDirectory(), "branding.json");

public static string GetQuasarBrandingDirectory(string webRootPath) =>
    Path.Combine(webRootPath, "branding");
```

### 7. `Quasar/Services/ThemePreferenceService.cs`
- Add `BrandingService _brandingService` field, inject via constructor
- Change `Theme` property: `public MudTheme Theme => _brandingService.BuildMudTheme();`
- No event subscription needed — `MainLayout` drives re-render via its own subscription

### 8. `Quasar/Components/Layout/MainLayout.razor`
- Add `@inject BrandingService BrandingService` and `@implements IDisposable`
- Add `<BrandingHeadContent />` inside the layout markup
- Replace hardcoded "Quasar" → `@BrandingService.Settings.AppName`
- Replace hardcoded "Supervisor control plane" → `@BrandingService.Settings.AppSubtitle`
- Replace logo `src` → `@(_isDarkMode ? (BrandingService.Settings.LogoDarkPath ?? "/Quasar.png") : (BrandingService.Settings.LogoLightPath ?? "/Quasar-inverted-transparent.png"))`
- Add in `@code`: subscribe `BrandingService.Changed += OnBrandingChanged` in `OnInitialized`, `Dispose()` unsubscribes, `OnBrandingChanged` calls `InvokeAsync(StateHasChanged)`

### 9. `Quasar/Components/App.razor`
Remove the two hardcoded `<link rel="icon">` lines (lines 12-13). The favicon is now rendered dynamically by `BrandingHeadContent` via `HeadOutlet`. Optionally keep one as a static fallback for pre-JS render.

### 10. `Quasar/Components/Layout/NavMenu.razor`
Add a "Settings" section at the bottom of the `<MudStack>`:
```razor
<MudDivider />
<MudStack Spacing="0" Class="magnetar-nav-section">
    <MudText Typo="Typo.overline">Settings</MudText>
</MudStack>
<MudNavMenu Class="magnetar-nav-menu">
    <MudNavLink Href="/settings/appearance" Icon="@Icons.Material.Filled.Palette">
        Appearance
    </MudNavLink>
</MudNavMenu>
```

### 11. `Quasar/Program.cs`
Add before `ThemePreferenceService` registration:
```csharp
builder.Services.AddSingleton<BrandingService>();
```
`IWebHostEnvironment` is already in DI — no extra registration.

---

## Key Design Decisions

| Decision | Rationale |
|---|---|
| File-based JSON (`branding.json`) | Matches all existing storage (no DB in project) |
| `ThemePalette` maps exact `QuasarTheme.cs` fields | No duplication; `QuasarLight()` factory reads from `QuasarTheme.Default` |
| `BrandingService` singleton, `ThemePreferenceService` scoped | Follows existing registration pattern; no event lifetime risk since `ThemePreferenceService` doesn't subscribe |
| Logos/favicon in `wwwroot/branding/` | Served by static file middleware; runtime writes work with `MapStaticAssets()` |
| `?v={timestamp}` in favicon URL | Cache-busting without page reload; stored in `FaviconPath` field |
| `BrandingHeadContent.razor` in MainLayout render tree | Only way to make `HeadOutlet` reactive in Blazor Server |
| Draft pattern in Appearance.razor | User can edit freely, save atomically |

---

## Verification

1. **Build**: `dotnet build Quasar/Quasar.csproj` — no errors
2. **Default state**: Start app, verify existing Quasar branding unchanged (no `branding.json` = defaults)
3. **Preset switch**: Navigate to `/settings/appearance`, select "Midnight Blue", Save → all palette colors update live across all open tabs
4. **Custom color**: Change Primary color, Save → AppBar and buttons update without page reload
5. **Logo upload**: Upload a PNG → logo in AppBar updates immediately
6. **Favicon upload**: Upload a `.ico` → browser tab favicon updates (may need one tab refresh for aggressive browser caching)
7. **Branding text**: Change App Name → AppBar title updates live
8. **Reset**: Click "Reset to Quasar Default" → all branding reverts
9. **Persistence**: Restart app → custom theme/branding persists from `branding.json`
10. **Preset accuracy**: Verify "Quasar Default" palette exactly matches original `QuasarTheme.Default` colors
