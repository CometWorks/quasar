# Quasar/Services/ThemePreferenceService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Per-session (scoped) service that manages the user's theme preference (System / Light / Dark), persists the choice to browser `localStorage` under key `"quasar.theme.mode"`, and resolves the effective `IsDarkMode` boolean by querying the OS dark-mode preference via JS interop when the mode is `System`. It also exposes the active `MudTheme` from `BrandingService`.

## Structure
**Namespace:** `Quasar.Services`

**Types:**
- `ThemeMode` (enum) — `System`, `Light`, `Dark`
- `ThemePreferenceService` (sealed class)

| Member | Description |
|---|---|
| `Theme` | Returns `MudTheme` from `BrandingService.BuildMudTheme()`. |
| `ThemeModeChanged` | Event invoked when effective dark/light theme changes, including system-mode updates. |
| `Mode` | Current `ThemeMode` (default: `System`). |
| `IsDarkMode` | Resolved boolean (default: `true`). |
| `InitializeAsync()` | Reads stored preference from localStorage; resolves system mode via JS if needed. Idempotent. |
| `SyncSystemDarkMode(isDark)` | Updates `IsDarkMode` when system preference changes (only effective in System mode). |
| `SetModeAsync(mode)` | Updates mode and dark-mode flag, persists new value to localStorage. |
| `GetSystemDarkModeAsync()` (private) | Calls `quasarConfigs.getSystemDarkMode` JS function; returns `true` on error. |

## Dependencies
- [`Quasar/Services/BrandingService.cs`](BrandingService.cs.md) (theme construction)
- `ILocalStorageService` (Blazored.LocalStorage or equivalent)
- `Microsoft.JSInterop.IJSRuntime`
- MudBlazor (`MudTheme`)

## Notes
- `InvalidOperationException` and `JSDisconnectedException` are silently swallowed during localStorage/JS calls to handle Blazor circuit disconnection gracefully.
- The service is scoped to a Blazor circuit (one instance per browser tab).
