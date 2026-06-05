# Quasar/Components/PluginConfigEditor.razor

**Module:** Quasar.Components  **Kind:** Blazor component  **Tier:** 2

## Summary
Reusable editor that renders a plugin's configuration schema as a fully interactive MudBlazor form. Parses `PluginConfigData.ConfigJson` into a typed layout (tabs, sections, columns, fields) and generates field controls for every supported type. Tracks dirty state and submits changes via `PluginConfigService`.

## Structure
No `@page` route — used as a child component on plugin management pages.

**Parameters:**
| Parameter | Type | Notes |
|---|---|---|
| `AgentId` | `string` | Required. Target agent for config update. |
| `Plugin` | `PluginConfigData` | Required. Plugin metadata + raw config JSON. |

**Injected services:**
- `PluginConfigService Service` — sends the updated JSON to the agent.
- `ISnackbar Snackbar` — validation/error/success toasts.

**Private state:**
- `_envelope` (`PluginConfigEnvelope?`) — parsed schema + default values.
- `_values` (`JsonObject`) — live editable clone of current values.
- `_signature` — change detection key (pluginId + configJson); prevents layout rebuild if data unchanged or dirty.
- `_dirty`, `_applying` — unsaved-change and in-flight flags.
- `_rawErrors` — per-path JSON parse errors for raw fields.
- `_newDictKeys` — pending new-key input for dict fields.
- `_containersById`, `_containers`, `_tabs` — built layout model.

**Layout model (private inner types):**
- `ContainerModel` — tree node with `Kind` (tab / section / column / root), `Caption`, `Children`, `Fields`.
- `FieldModel` — field descriptor with `Type`, `JsonKey`, constraints; factory methods `FromProperty`, `FromMember`, `FromListElement`, `FromDictValue`, `Raw`, `WithName`.
- `TreeContext` — parent-selector context for tree-ordered lists (`ParentJsonKey`, `IdJsonKey`, `Options`, `CurrentIndex`).
- `TreeOption` — a single parent-selector option (record).
- `ListEntry` — index + depth for tree-ordered rendering (record).

**Supported field types rendered:** `bool` (`MudCheckBox`), `int`/`long`/`float`/`double` (`MudNumericField`), `string` (`MudTextField`), `enum`/`Direction` (`MudSelect`), `Color` (`MudColorPicker`), `Vector2D`/`Vector3D`/`Vector2I`/`Vector3I` (component grids), `MyPositionAndOrientation` (position+forward+up sub-grids), `struct` (nested member rendering), `list` (expandable array with add/remove/reorder; tree-ordered for structs with tree parent fields), `dict` (key-value pairs with add/rename/remove), raw fallback (`MudTextField` multi-line JSON).

**Key render fragments:**
- `RenderContainerBody` — renders columns as `MudGrid`, sections as `MudExpansionPanel`, then fields.
- `RenderContainerContent` — wraps non-tab containers in expansion panels or plain stacks.
- `RenderField` — dispatches to a type-specific fragment.
- `RenderListField` / `RenderDictField` — collection editors with full CRUD.
- `RenderTreeParentSelector` — `MudSelect` dropdown populated from sibling list items when the field is the tree-parent reference.

**Key code methods:**
- `OnParametersSet` — signature-based change detection; calls `BuildLayout()`.
- `BuildLayout()` — constructs the container/field tree from `_envelope.Schema`.
- `ApplyAsync()` — serializes `_values` to JSON and calls `Service.UpdatePluginConfigAsync`.
- `ResetToDefaults()` — replaces `_values` with `_envelope.CloneDefaults()`.
- `GetListOrder` — DFS tree-ordering of list items when a `TreeContext` is present.

**MudBlazor components used:** `MudTabs`, `MudTabPanel`, `MudExpansionPanels`, `MudExpansionPanel`, `MudGrid`, `MudItem`, `MudPaper`, `MudStack`, `MudText`, `MudCheckBox`, `MudNumericField`, `MudTextField`, `MudSelect`, `MudSelectItem`, `MudColorPicker`, `MudAlert`, `MudButton`, `MudIconButton`, `MudChip`.

## Dependencies
- `Quasar/Services/PluginSdk/PluginConfigService.cs` — async config update submission
- `Quasar/Services/PluginSdk/PluginConfigEnvelope.cs` — parses schema + clones values/defaults
- `Magnetar.Protocol.Model.PluginConfigData` — plugin parameter type
- `Magnetar.Protocol.Model` DTOs: `LayoutContainerDto`, `ConfigPropertyDto`, `StructDto`, `StructMemberDto`, `EnumValueDto`
- MudBlazor, `MudBlazor.Utilities.MudColor`
- `System.Text.Json`, `System.Text.Json.Nodes`

## Notes
The signature-based dirty guard in `OnParametersSet` means live agent snapshot ticks that push updated `Plugin` objects will not overwrite unsaved user edits — the component ignores new data while `_dirty` is true. Raw-field JSON parse errors are tracked per-path and block `ApplyAsync` until resolved.
