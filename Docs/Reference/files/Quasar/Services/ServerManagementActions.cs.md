# Quasar/Services/ServerManagementActions.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
Scoped UI action coordinator for dedicated-server management commands that are launched from reusable UI surfaces such as dashboard `ServerCard`s. It centralizes the existing clone, edit, delete, console, and world-template dialog flows so card actions behave like the full server-management table without forcing the dashboard to render the whole `Servers` component.

## Structure
- **Namespace:** `Quasar.Services`
- **Type:** `public sealed class ServerManagementActions`
- **Constructor dependencies:** `DedicatedServerCatalog`, `DedicatedServerSupervisor`, `AgentRegistry`, `QuasarWorldTemplateCatalog`, `IDialogService`, `ISnackbar`.

**Public methods**
- `OpenConsoleDialogAsync(uniqueName)` — opens `ServerConsoleDialog` for the server.
- `OpenEditDialogAsync(definition)` — opens `ServerEditorDialog` in edit mode and saves via `DedicatedServerCatalog.UpsertAsync`.
- `OpenCloneDialogAsync(definition)` — opens `ServerEditorDialog` in clone mode, asks whether to copy world state or leave the clone empty, saves the new definition, and prepares clone world storage.
- `CreateWorldTemplateAsync(definition)` — validates that the server is stopped and the world folder contains `Sandbox.sbc`, opens `WorldTemplateFromServerDialog`, then imports through `QuasarWorldTemplateCatalog`.
- `DeleteAsync(uniqueName)` — blocks running servers, confirms with `ServerDeleteDialog`, deletes the catalog entry, prunes disconnected agents, and shows the "folder left on disk" acknowledgement dialog.
- `CanCreateWorldTemplate(definition)` — true only while no template import is already running, the server goal is Off, and the process state is Stopped.

**Clone support**
- `ChooseCloneWorldModeAsync` asks for `Copy World` vs `No World`.
- `CopyCloneWorldStateAsync` copies a stopped source world directly or a running source's newest Space Engineers `Backup/` snapshot.
- `EnsureCloneHasNoWorldAsync` deletes any pre-existing target world path so first start seeds from the selected template.
- Path guards reject clone definitions that reuse source DS app-data, world, or rendered config paths.

## Dependencies
- [`Quasar/Services/DedicatedServerCatalog.cs`](DedicatedServerCatalog.cs.md)
- [`Quasar/Services/DedicatedServerSupervisor.cs`](DedicatedServerSupervisor.cs.md)
- [`Quasar/Services/AgentRegistry.cs`](AgentRegistry.cs.md)
- `Quasar/Services/QuasarWorldTemplateCatalog.cs`
- [`Quasar/Components/Pages/ServerConsoleDialog.razor`](../Components/Pages/ServerConsoleDialog.razor.md)
- [`Quasar/Components/Pages/ServerEditorDialog.razor`](../Components/Pages/ServerEditorDialog.razor.md)
- [`Quasar/Components/Pages/ServerDeleteDialog.razor`](../Components/Pages/ServerDeleteDialog.razor.md)
- [`Quasar/Components/Pages/WorldTemplateFromServerDialog.razor`](../Components/Pages/WorldTemplateFromServerDialog.razor.md)
- [`Magnetar.Protocol/Runtime/MagnetarPaths.cs`](../../Magnetar.Protocol/Runtime/MagnetarPaths.cs.md)
- MudBlazor (`IDialogService`, `ISnackbar`, `DialogParameters`, `DialogOptions`)

## Notes
- This class is scoped so `_creatingWorldTemplate` is per Blazor circuit and because MudBlazor dialog/snackbar services are scoped UI services.
- World-copy work runs via `Task.Run`; live-source clone deliberately requires a Space Engineers backup snapshot instead of copying the active world directory.
