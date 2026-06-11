# Quasar/Components/Pages/Discord.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Routable page (`/discord`) for configuring the Quasar Discord bot. Combines global bot settings (token, guild ID, enabled flag), per-server channel bindings (command, chat-relay, log, analytics, death-messages, simspeed-alert channels plus interval and feature toggles), configurable simspeed alert rules, editable death-message template text areas organised by death-type category, and a terminal icon button that opens the dedicated Discord integration log dialog.

## Structure
- **Route:** `@page "/discord"`
- **Implements:** `IDisposable`
- **Injected services:** `DiscordOptionsCatalog`, `DeathMessagesCatalog`, `DedicatedServerCatalog`, `DiscordBotService`, `IDialogService`, `ISnackbar`
- **Key UI sections:**
  - Header row with page title, short description, and terminal icon button (`OpenDiscordConsoleDialogAsync`) for `DiscordConsoleDialog`.
  - Bot Settings card: enabled checkbox, bot token password field, guild ID numeric field, status chip (color driven by `GetStatusColor()`), error alert if bot reports an error, informational setup link alert, Save Bot Settings button.
  - Per-Server Bindings card: `MudExpansionPanels MultiExpansion="true"` — one panel per server from `DedicatedServerCatalog`, each panel headed by `GetServerName(...)` (the configured display name) with the unique name shown as a caption beneath it. Each panel contains: command prefix text, six `ulong?` channel ID numeric fields (command, chat-relay, log, analytics, death, simspeed alert), two interval numeric fields, feature checkboxes (chat relay, log export, analytics export, death messages, simspeed alerts/rules), simspeed rule numeric fields, death emotes text field, non-command relay checkbox, and a per-server Save button.
  - Death Message Templates section: `MudExpansionPanels` with one panel showing a 2-column grid of `MudTextField` (6 lines each) for eight death categories — Suicide, PvP, Turret, Grid, Oxygen, Pressure, Collision, Accident. Template count chip per category.
- **Key state:** `_options` (`DiscordOptions`), `_deathMessageConfig` (`DeathMessagesConfig`).
- **Key methods:**
  - `SyncServerOptions()` — ensures `_options.Servers` contains an entry for every defined server, then normalises via `DiscordOptions.Normalize`.
  - `SaveBotSettingsAsync()` / `SaveServerAsync(string)` — both call the shared `SaveAsync(string)` which calls `DiscordOptionsCatalog.SaveAsync`.
  - `OpenDiscordConsoleDialogAsync()` — opens `DiscordConsoleDialog` at large width with Escape-close enabled.
  - `ResetDeathTemplatesAsync()` / `SaveDeathTemplatesAsync()` — delegate to `DeathMessagesCatalog`.
  - `GetDeathMessageCategories()` — returns a fixed list of `DeathMessageCategoryView` records bound to the config lists.
  - `SetDeathMessageEditorText(string, string)` — dispatches to the correct `_deathMessageConfig.*Messages` list by category key.
  - `GetServerName(string uniqueName)` — resolves a server panel's header to its configured `DisplayName`, falling back to the unique name when the definition is missing or its display name is blank.
- **Event subscriptions:** `DiscordOptionsCatalog.Changed`, `DeathMessagesCatalog.Changed`, `DedicatedServerCatalog.Changed`, `DiscordBotService.Changed`.
- **Private type:** `DeathMessageCategoryView` record (Key, Title, Description, EditorText).

## Dependencies
- `Quasar/Services/DiscordOptionsCatalog.cs`
- `Quasar/Services/DeathMessagesCatalog.cs`
- [`Quasar/Services/DedicatedServerCatalog.cs`](../../Services/DedicatedServerCatalog.cs.md)
- `Quasar/Services/DiscordBotService.cs`
- [`Quasar/Components/Pages/DiscordConsoleDialog.razor`](DiscordConsoleDialog.razor.md)
- `Quasar/Models/DiscordOptions.cs` / `DiscordServerOptions.cs`
- `Quasar/Models/DeathMessagesConfig.cs`
- `Quasar/Paths/MagnetarPaths.cs` (used to display the death messages file path)
- MudBlazor (`MudExpansionPanels`, `MudCheckBox`, `MudNumericField`, `MudTextField`, `MudChip`, `ISnackbar`)

## Notes
- Bot token is rendered as `InputType.Password` to avoid accidental exposure.
- `SyncServerOptions()` is called before save and on catalog changes to keep the per-server list in sync with defined servers without losing existing settings.
