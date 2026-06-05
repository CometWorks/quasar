# Clone Feature Plan

## Motivation

Quasar supports Create, Edit, and Delete for instances, world templates, and config profiles. There is no way to duplicate an existing entity. Clone accelerates server setup — operators frequently spin up near-identical instances that differ only by name, or want to experiment with a config profile without touching the original.

---

## Scope

| Entity | Clone UX | Result |
|---|---|---|
| Config Profile | Icon button on sidebar tile | New profile with all settings copied, name appended with ` (Copy)` |
| Instance | Button in action column | Editor dialog opens pre-filled in create mode; user must confirm a unique name |
| World Template | Button in table row | World files copied to new managed directory; auto-named `{Name} (Copy)` |

---

## UX Per Entity

### Config Profiles (`/configs`)

A copy icon button sits next to the existing delete icon on each profile tile in the left sidebar. Clicking it immediately duplicates the profile with a `(Copy)` suffix on the name and selects the new profile in the editor. No dialog is needed — the profile can be renamed inline via the editor.

Button is always enabled (profiles have no external dependencies that would block duplication).

### Instances (`/instances`)

A **Clone** button appears in the action column next to **Edit** and **Delete**, visible for both running and stopped instances. Cloning a running instance is allowed — it only duplicates the definition, not the process.

Clicking Clone opens the standard instance editor dialog in create mode (`IsEditing: false`) with all fields pre-populated from the source instance. The unique name is pre-filled as `{originalName}-copy`. The dialog validates the name for uniqueness before saving, so if `-copy` is already taken the user sees an inline error and can adjust.

This reuses the existing `InstanceEditorDialog` without modification.

### World Templates (`/world-profiles`)

A **Clone** button appears next to the **Delete** button in each table row. The button is disabled if the world files are missing or an import/clone is already in progress.

Clicking Clone copies all world files from the existing managed directory into a new managed directory, creating a new profile named `{Name} (Copy)` with the same description. This is a file copy operation and may take a moment for large saves.

---

## Service Layer

No new catalog methods are needed. Each entity type reuses existing APIs:

| Entity | Method reused |
|---|---|
| Config Profile | `GetProfile(id)` returns a deep clone; reassign `ConfigProfileId` and `Name`, then call `UpsertAsync()` |
| Instance | `definition.Clone()` (public method on model); pass to `ShowEditorDialogAsync` with `isEditing: false` |
| World Template | `GetWorldDirectory(id)` as source path, then `ImportAsync(newName, desc, sourceDir, ct)` |

---

## Edge Cases

| Case | Handling |
|---|---|
| Clone instance with name collision (`-copy` already exists) | Editor dialog shows validation error; user edits name before saving |
| Clone world template with missing files | Clone button is disabled when `WorldExists` is false |
| Clone world template while another import/clone is running | Clone button is disabled when `_importing` is true |
| Clone config profile assigned to instances | The clone starts with no assigned instances (new GUID); originals are unaffected |
| Clone running instance | Allowed — duplicates the definition only, does not affect the running process |

---

## Future Considerations

- **Custom name prompt for world clone** — Currently auto-names as `{Name} (Copy)`. A lightweight dialog (similar to `WorldProfileQuickImportDialog`) could let the user set a name before the file copy begins.
- **Clone-chain naming** — If `{Name} (Copy)` already exists, subsequent clones stack as `{Name} (Copy) (Copy)`. Auto-incrementing suffixes (`(Copy 2)`, `(Copy 3)`) could be added to the catalog service.
- **Clone instance with new world/config** — The editor dialog already allows changing the config profile and world template, so this works naturally.
