# Quasar/Components/Shared/CopyablePath.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 3

## Summary
Reusable MudBlazor path display that renders a monospace, wrapping path with a copy-to-clipboard icon. Used for diagnostic paths such as log files, managed runtime install locations, appsettings conflict files, and read-only world/server folders.

## Structure
- **Injected services:** `IJSRuntime`, `ISnackbar`
- **Parameters**
  - `Value` — path text to show and copy; the component renders nothing when blank.
  - `Typo` — MudBlazor text style for non-inline mode; defaults to `Typo.caption`.
  - `Class`, `TextClass` — optional CSS classes for the wrapper and text.
  - `Inline` — renders as inline `span` content instead of a row stack, for sentence-embedded paths.
  - `Tooltip`, `SuccessMessage`, `FailureMessage` — copy button tooltip and snackbar text.
- **`CopyAsync()`** — calls `quasarConfigs.copyText(Value)` and reports success/failure through snackbar.
- **`JoinCss(...)`** — joins non-empty CSS class fragments for wrapper/text classes.

## Dependencies
- [`Quasar/wwwroot/quasar-configs.js`](../../wwwroot/quasar-configs.js.md) — clipboard helper with secure-context and legacy fallbacks.
- [`Quasar/wwwroot/app.css`](../../wwwroot/app.css.md) — global `.copyable-path*` styling.
- MudBlazor — `MudStack`, `MudText`, `MudTooltip`, `MudIconButton`, `ISnackbar`.
- `Microsoft.JSInterop`.
