# Quasar/Components/Pages/WorldTemplateFromServerDialog.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Lightweight MudBlazor confirmation dialog opened by the Servers page when the user requests to create a world template from a stopped server's current world directory. Collects the template name and description, displays the source world path (read-only), and returns a `TemplateRequest` record to the caller.

## Structure
- **No `@page` route** — dialog only.
- **`[CascadingParameter]` `IMudDialogInstance MudDialog`**
- **`[Parameter]`s**
  - `string DefaultName` — pre-filled template name (caller sets to e.g. `"{ServerName} Snapshot {date}"`).
  - `string DefaultDescription` — pre-filled description.
  - `string WorldPath` — shown as read-only caption so the user can confirm the source.
- **Key UI**
  - `MudTextField` for name (required) and description (multi-line, optional).
  - Read-only caption displaying `WorldPath` in monospace.
  - Cancel / Create buttons; Create disabled while `_name` is blank.
- **`TemplateRequest`** — `public sealed record TemplateRequest(string Name, string Description)` returned in `DialogResult.Ok`.
- **`Create`** — trims name and description, closes with `Ok(new TemplateRequest(...))`.

## Dependencies
- [`Quasar/Components/Pages/Servers.razor`](Servers.razor.md) — sole caller.
- MudBlazor — `MudDialog`, `MudTextField`, `MudText`, `MudButton`.
