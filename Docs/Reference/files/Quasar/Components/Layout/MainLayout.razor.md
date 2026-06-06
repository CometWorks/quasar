# Quasar/Components/Layout/MainLayout.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Top-level application shell layout. Provides the MudBlazor theme/provider setup, a responsive app bar with branding, a theme-mode switcher, auth (login/logout) controls, and Quasar shutdown / worker-restart controls. It hosts a collapsible side drawer with `NavMenu`, the main content area that renders `@Body`, a full-screen shutdown/restart overlay, and two confirmation `MudMessageBox` dialogs (shut-down-all-servers and restart-worker).

## Structure
Inherits: `LayoutComponentBase`  
Implements: `IDisposable`

**Injected services:**
- `ThemePreferenceService ThemePreference` — persists and resolves dark/light/system theme mode; exposes `Theme`, `IsDarkMode`, `InitializeAsync`, `SetModeAsync`, `SyncSystemDarkMode`.
- `QuasarShutdownService ShutdownService` — orchestrates graceful multi-server shutdown (`StopAllServersAsync`) and worker restart (`RestartWorker`).
- `WebServiceOptions WebServiceOptions` — read for `LauncherToken` to detect Bootstrap-managed runs.
- `BrandingService BrandingService` — supplies `AppName`, `AppSubtitle`, logo paths.
- `IJSRuntime JS` — invokes `quasarConfigs.reloadWhenHealthy` during worker restart.

**Private state:**
- `_drawerOpen` (bool, default `true`)
- `_isDarkMode` (bool, default `true`)
- `_themeMode` (ThemeMode, default `System`)
- `_isShuttingDown` (bool) — drives the blocking overlay; also used during worker restart.
- `_shutdownStatus` (string) — message shown in the overlay.
- `_shutdownMessageBox`, `_restartWorkerMessageBox` (MudMessageBox refs)
- `IsUnderBootstrap` (computed) — true when `WebServiceOptions.LauncherToken` is set (worker was spawned by Quasar Bootstrap).
- `LogoSrc` (computed) — picks dark vs light branding logo by `_isDarkMode`.

**MudBlazor providers (top of markup):** `MudThemeProvider` (with `ObserveSystemDarkModeChange` when in System mode), `MudPopoverProvider`, `MudDialogProvider`, `MudSnackbarProvider`. `<BrandingHeadContent />` injects favicon/title head markup.

**App bar sections:**
- Hamburger `MudIconButton` → `ToggleDrawer`.
- Brand logo + name/subtitle from `BrandingService`.
- Theme-mode `MudMenu` (System / Light / Dark) via `SetThemeModeAsync`; icon from `GetThemeModeIcon`.
- `<AuthorizeView>` — Logout icon button (tooltip from `GetAuthTooltip`, uses `ClaimsPrincipal.GetQuasarDisplayName`) for authenticated users; Login button for guests.
- `<AuthorizeView Policy="CanShutdownQuasar">` — when `IsUnderBootstrap`, a restart-worker icon (`HandleRestartWorkerClickAsync`); always a red power-off icon (`HandleShutdownClickAsync`). Both disabled while `_isShuttingDown`.

**Shutdown flow:** `HandleShutdownClickAsync` shows `_shutdownMessageBox`; on confirm sets `_isShuttingDown = true`, shows `MudOverlay`, and calls `ShutdownService.StopAllServersAsync(Progress<string>)` streaming status. Quasar itself stays up, so the toolbar is re-enabled in a `finally`.

**Restart-worker flow:** `HandleRestartWorkerClickAsync` shows `_restartWorkerMessageBox`; on confirm sets `_isShuttingDown`, invokes JS `quasarConfigs.reloadWhenHealthy("/")` so the browser reloads to the Dashboard once the new worker is healthy, then calls `ShutdownService.RestartWorker()` (the circuit drops as the worker stops). Only meaningful under Bootstrap.

**Dark-mode sync:** `OnSystemDarkModeChangedAsync` updates `_isDarkMode` and calls `ThemePreference.SyncSystemDarkMode` when the OS theme changes (System mode).

**Drawer:** `MudDrawer` (responsive, breakpoint Md, 180 px, `ClipMode.Always`) containing `<NavMenu />`.

**Content:** `MudMainContent > MudContainer (MaxWidth.False) > @Body`.

**Error UI:** `#blazor-error-ui` div (styled in `MainLayout.razor.css`).

**Lifecycle:**
- `OnInitialized` — subscribes to `BrandingService.Changed`.
- `OnAfterRenderAsync(firstRender)` — calls `ThemePreference.InitializeAsync()` to hydrate `_themeMode`/`_isDarkMode` from browser storage.
- `Dispose` — unsubscribes from `BrandingService.Changed`.

## Dependencies
- [`Quasar/Components/Layout/BrandingHeadContent.razor`](BrandingHeadContent.razor.md)
- [`Quasar/Components/Layout/NavMenu.razor`](NavMenu.razor.md)
- [`Quasar/Services/ThemePreferenceService.cs`](../../Services/ThemePreferenceService.cs.md)
- [`Quasar/Services/QuasarShutdownService.cs`](../../Services/QuasarShutdownService.cs.md)
- [`Quasar/Services/BrandingService.cs`](../../Services/BrandingService.cs.md)
- `Quasar/Services/WebServiceOptions.cs` (`LauncherToken`)
- `Quasar/Auth/QuasarPolicyNames.cs` (policy constant `CanShutdownQuasar`)
- `Quasar/Auth/*` — `ClaimsPrincipal.GetQuasarDisplayName()` extension
- MudBlazor, `Microsoft.JSInterop.IJSRuntime`

## Notes
- Theme initialization happens in `OnAfterRenderAsync` (first render only) to avoid SSR/prerender mismatch — the browser-storage read requires JS interop, unavailable during prerender.
- The restart-worker button is hidden when not launched by Quasar Bootstrap (`LauncherToken` absent): without a launcher to respawn it, `RestartWorker()` would only stop the worker. The JS `reloadWhenHealthy` poller is invoked *before* `RestartWorker()` so the call reaches the client before the circuit drops.
