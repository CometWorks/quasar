# Quasar/Components/Layout/MainLayout.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Top-level application shell layout. Provides the MudBlazor theme/provider setup, a responsive app bar with branding, theme-mode switcher, auth (login/logout) and Quasar-shutdown controls, a collapsible side drawer with `NavMenu`, and the main content area that renders `@Body`. Also renders a full-screen shutdown overlay and a confirmation dialog.

## Structure
Inherits: `LayoutComponentBase`  
Implements: `IDisposable`

**Injected services:**
- `ThemePreferenceService ThemePreference` — persists and resolves dark/light/system theme mode.
- `QuasarShutdownService ShutdownService` — orchestrates graceful multi-server shutdown.
- `BrandingService BrandingService` — supplies `AppName`, `AppSubtitle`, logo paths.

**Private state:**
- `_drawerOpen` (bool, default `true`)
- `_isDarkMode` (bool, default `true`)
- `_themeMode` (ThemeMode, default `System`)
- `_isShuttingDown` (bool)
- `_shutdownStatus` (string) — message shown in the overlay during shutdown.
- `_shutdownMessageBox` (MudMessageBox ref)

**MudBlazor providers (top of markup):** `MudThemeProvider`, `MudPopoverProvider`, `MudDialogProvider`, `MudSnackbarProvider`.

**App bar sections:**
- Hamburger `MudIconButton` → `ToggleDrawer`.
- Brand logo + name/subtitle from `BrandingService`; logo src switches between dark/light variants.
- Theme-mode `MudMenu` (System / Light / Dark) via `SetThemeModeAsync`.
- `<AuthorizeView>` — Logout icon button for authenticated users, Login button for guests.
- `<AuthorizeView Policy="CanShutdownQuasar">` — red power icon, triggers `HandleShutdownClickAsync`.

**Shutdown flow:** `HandleShutdownClickAsync` shows `_shutdownMessageBox`; on confirm sets `_isShuttingDown = true`, shows `MudOverlay`, calls `ShutdownService.ShutdownAsync(Progress<string>)` streaming status messages.

**Drawer:** `MudDrawer` (responsive, breakpoint Md, 235 px) containing `<NavMenu />`.

**Content:** `MudMainContent > MudContainer (MaxWidth.False) > @Body`.

**Error UI:** `#blazor-error-ui` div (styled in `MainLayout.razor.css`).

**Lifecycle:**
- `OnInitialized` — subscribes to `BrandingService.Changed`.
- `OnAfterRenderAsync(firstRender)` — calls `ThemePreference.InitializeAsync()` to hydrate theme mode from browser storage.
- `Dispose` — unsubscribes from `BrandingService.Changed`.

## Dependencies
- [`Quasar/Components/Layout/BrandingHeadContent.razor`](BrandingHeadContent.razor.md)
- [`Quasar/Components/Layout/NavMenu.razor`](NavMenu.razor.md)
- [`Quasar/Services/ThemePreferenceService.cs`](../../Services/ThemePreferenceService.cs.md)
- [`Quasar/Services/QuasarShutdownService.cs`](../../Services/QuasarShutdownService.cs.md)
- [`Quasar/Services/BrandingService.cs`](../../Services/BrandingService.cs.md)
- `Quasar/Services/Auth/QuasarPolicyNames.cs` (policy constant `CanShutdownQuasar`)
- MudBlazor

## Notes
Theme initialization happens in `OnAfterRenderAsync` (first render only) to avoid SSR/prerender mismatch — the browser storage read requires JS interop which is unavailable during prerender.
