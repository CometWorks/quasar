# Quasar/Services/ThemePreferenceService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Per-circuit (scoped) service that manages the user's theme preference (System / Light / Dark), persists the choice to browser `localStorage` under key `"quasar.theme.mode"`, and resolves the effective `IsDarkMode` boolean — querying the OS dark-mode preference via JS interop when the mode is `System`. It also surfaces the active `MudTheme` built by `BrandingService`.

## Structure
**Namespace:** `Quasar.Services`

**Types:**
- `ThemeMode` enum — `System`, `Light`, `Dark`
- `ThemePreferenceService` — sealed class

| Member | Description |
|---|---|
| `Theme` | `MudTheme` from `BrandingService.BuildMudTheme()`. |
| `event Action<bool>? ThemeModeChanged` | Invoked when the effective dark/light value changes (including system-mode updates). |
| `Mode` | Current `ThemeMode` (default `System`). |
| `IsDarkMode` | Resolved boolean (default `true`). |
| `InitializeAsync()` | Reads stored preference; resolves system mode via JS when needed; idempotent (guarded by `_initialized`). |
| `SyncSystemDarkMode(isDark)` | Updates `IsDarkMode` when the OS preference changes, only effective in System mode. |
| `SetModeAsync(mode)` | Updates mode + dark flag, fires the event on change, persists to localStorage. |
| `GetSystemDarkModeAsync()` (private) | Calls JS `quasarConfigs.getSystemDarkMode`; returns `true` on error. |

## Dependencies
- `Quasar/Services/BrandingService.cs` — theme construction
- `ILocalStorageService` (Blazored.LocalStorage)
- `Microsoft.JSInterop.IJSRuntime`
- MudBlazor — `MudTheme`

## Notes
- `InvalidOperationException` and `JSDisconnectedException` are swallowed around localStorage/JS calls to tolerate Blazor circuit disconnection (e.g. prerender).
- Scoped per Blazor circuit (one instance per browser tab).
