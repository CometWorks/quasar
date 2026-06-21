# Quasar/Components/Layout/MainLayout.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Top-level application shell layout. Provides the MudBlazor theme/provider setup, theme-configured hover-list CSS variables, a responsive app bar with branding, update notification bell, theme-mode switcher, auth (login/logout) controls, and a Quasar power dialog trigger. It hosts a collapsible side drawer with `NavMenu`, the main content area that renders `@Body`, and a full-screen overlay while Quasar is restarting or shutting down.

## Structure
Inherits: `LayoutComponentBase`  
Implements: `IDisposable`

**Injected services:**
- `ThemePreferenceService ThemePreference` — persists and resolves dark/light/system theme mode; exposes `Theme`, `IsDarkMode`, `InitializeAsync`, `SetModeAsync`, `SyncSystemDarkMode`.
- `QuasarShutdownService ShutdownService` — orchestrates graceful multi-server shutdown, Quasar shutdown while preserving servers, and worker restart.
- `WebServiceOptions WebServiceOptions` — read for `LauncherToken` and `AgentOfflineShutdownSeconds`.
- `BrandingService BrandingService` — supplies `AppName`, `AppSubtitle`, logo paths.
- `QuasarUpdateService UpdateService` — update snapshot source for the app-bar bell and snackbars.
- `ISnackbar Snackbar` — app-wide update notifications.
- `IJSRuntime JS` — invokes `quasarConfigs.reloadWhenHealthy` during worker restart.
- `IDialogService DialogService` — opens `QuasarControlDialog`.

**Private state:**
- `_drawerOpen` (bool, default `true`)
- `_isDarkMode` (bool, default `true`)
- `_themeMode` (ThemeMode, default `System`)
- `_isShuttingDown` (bool) — drives the blocking overlay during Quasar restart/shutdown.
- `_shutdownStatus` (string) — message shown in the overlay.
- `_updateSnapshot` (`QuasarUpdateSnapshot`) — latest update state used by the bell badge/tooltip.
- `IsUnderBootstrap` (computed) — true when `WebServiceOptions.LauncherToken` is set (worker was spawned by the Quasar launcher).
- `LogoSrc` (computed) — picks dark vs light branding logo by `_isDarkMode`.
- `HoverPaletteStyle` (computed) — emits `--quasar-hover-list-background` and `--quasar-hover-list-text` from the active theme palette's `HoverBackground` / `HoverContrastText` fields, using sanitized alpha-capable hex output and Quasar fallback colours.

**MudBlazor providers (top of markup):** `MudThemeProvider` (with `ObserveSystemDarkModeChange` when in System mode), `MudPopoverProvider`, `MudDialogProvider`, `MudSnackbarProvider`. `<BrandingHeadContent />` injects favicon/title head markup.

**App bar sections:**
- Hamburger `MudIconButton` → `ToggleDrawer`.
- Brand logo + name/subtitle from `BrandingService`.
- Notification bell links to `/settings/updates`; it shows a warning badge when a newer UI release is ready to download/staged for activation or when a launcher update is available.
- Theme-mode `MudMenu` (System / Light / Dark) via `SetThemeModeAsync`; icon from `GetThemeModeIcon`.
- `<AuthorizeView>` — Logout icon button (tooltip from `GetAuthTooltip`, uses `ClaimsPrincipal.GetQuasarDisplayName`) for authenticated users; Login button for guests.
- `<AuthorizeView Policy="CanShutdownQuasar">` — red power icon opens `QuasarControlDialog` through `HandlePowerClickAsync`; disabled while `_isShuttingDown`.

**Power dialog flow:** `HandlePowerClickAsync` opens `QuasarControlDialog` with restart availability and the agent offline grace period. The confirmed result dispatches to restart Quasar, shut down Quasar while preserving servers, or shut down all servers normally.

**Restart Quasar flow:** `RestartQuasarAsync` sets `_isShuttingDown`, invokes JS `quasarConfigs.reloadWhenHealthy("/")` so the browser reloads to the Dashboard once the new worker is healthy, then calls `ShutdownService.RestartWorker()` (the circuit drops as the worker stops). Only available under Bootstrap.

**Shutdown Quasar flow:** `ShutdownQuasarAsync` sets the blocking overlay and calls `ShutdownService.ShutdownQuasarPreservingServers()`, leaving running servers detached. On Linux service installs the shutdown service tries to stop the systemd unit first; otherwise it falls back to the Bootstrap shutdown request.

**Shutdown-all-servers flow:** `ShutdownAllServers` starts `ShutdownService.StopAllServersAsync(setGoalStateOff: true)` without awaiting it. This matches pressing Stop on every server card: servers wind down in the background, goal state is set to Off, Quasar itself stays up, and Dashboard / Servers reflect progress.

**Dark-mode sync:** `OnSystemDarkModeChangedAsync` updates `_isDarkMode` and calls `ThemePreference.SyncSystemDarkMode` when the OS theme changes (System mode).

**Drawer:** `MudDrawer` (responsive, breakpoint Md, 180 px, `ClipMode.Always`) containing `<NavMenu />`.

**Content:** `MudLayout` carries the hover-list CSS variables used by global table hover styling, then `MudMainContent > MudContainer (MaxWidth.False) > @Body`.

**Error UI:** `#blazor-error-ui` div (styled in `MainLayout.razor.css`).

**Lifecycle:**
- `OnInitialized` — subscribes to `BrandingService.Changed` and `UpdateService.Changed`, then hydrates `_updateSnapshot`.
- `OnAfterRenderAsync(firstRender)` — calls `ThemePreference.InitializeAsync()` to hydrate `_themeMode`/`_isDarkMode` from browser storage.
- `Dispose` — unsubscribes from `BrandingService.Changed` and `UpdateService.Changed`.

## Dependencies
- [`Quasar/Components/Layout/BrandingHeadContent.razor`](BrandingHeadContent.razor.md)
- [`Quasar/Components/Layout/NavMenu.razor`](NavMenu.razor.md)
- [`Quasar/Components/Layout/QuasarControlDialog.razor`](QuasarControlDialog.razor.md)
- [`Quasar/Components/Layout/QuasarControlAction.cs`](QuasarControlAction.cs.md)
- [`Quasar/Services/ThemePreferenceService.cs`](../../Services/ThemePreferenceService.cs.md)
- [`Quasar/Services/QuasarShutdownService.cs`](../../Services/QuasarShutdownService.cs.md)
- [`Quasar/Services/BrandingService.cs`](../../Services/BrandingService.cs.md)
- `Quasar/Services/WebServiceOptions.cs` (`LauncherToken`, `AgentOfflineShutdownSeconds`)
- `Quasar/Auth/QuasarPolicyNames.cs` (policy constant `CanShutdownQuasar`)
- `Quasar/Auth/*` — `ClaimsPrincipal.GetQuasarDisplayName()` extension
- MudBlazor, `Microsoft.JSInterop.IJSRuntime`

## Notes
- Theme initialization happens in `OnAfterRenderAsync` (first render only) to avoid SSR/prerender mismatch — the browser-storage read requires JS interop, unavailable during prerender.
- Hover list colours come from Appearance-managed palette fields. The Quasar default dark theme hover background is `#bfbfbfff`.
- Restart Quasar is disabled in the dialog when not launched by the Quasar launcher (`LauncherToken` absent): without a launcher to respawn it, `RestartWorker()` would only stop the worker. The JS `reloadWhenHealthy` poller is invoked *before* `RestartWorker()` so the call reaches the client before the circuit drops.
