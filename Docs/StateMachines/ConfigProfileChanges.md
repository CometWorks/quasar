# Config Profile Changes

Configuration in Quasar is file-backed JSON edited through profiles. While an
operator edits a profile, the page holds unsaved changes; attempting to switch
profiles with pending edits raises a decision dialog (Cancel / Discard / Save).
Saved changes are written atomically with a timestamped history snapshot.

Relevant source:
[`ConfigProfilePendingChangesDialog.razor`](../../Quasar/Components/Pages/ConfigProfilePendingChangesDialog.razor),
[`QuasarConfigProfileCatalog.cs`](../../Quasar/Services/QuasarConfigProfileCatalog.cs),
[`Configs.razor`](../../Quasar/Components/Pages/Configs.razor).

```mermaid
stateDiagram-v2
    [*] --> Clean
    Clean --> Edited: operator edits profile fields
    Edited --> Clean: Save (UpsertAsync + history snapshot)
    Edited --> PendingDecision: switch profile with unsaved edits
    PendingDecision --> Edited: Cancel
    PendingDecision --> Clean: Discard (then switch)
    PendingDecision --> Clean: Save (then switch)
    note right of Clean
        Applied changes go live unless marked
        restart-required, then applied on next start
    end note
```

![Config profile change lifecycle](diagrams/config-profile-changes.png)

| State | Meaning |
| --- | --- |
| `Clean` | The editor matches the persisted profile. |
| `Edited` | Unsaved edits exist in the editor. |
| `PendingDecision` | The operator tried to switch profiles with unsaved edits; the dialog offers `Cancel` (stay, keep edits), `Discard` (lose edits, switch), or `Save` (persist, then switch). |

**Persistence.** `QuasarConfigProfileCatalog.UpsertAsync` normalizes and writes
`{ProfilesDir}/{id}/profile.json` plus a `History/{timestamp}.json` snapshot
(atomic swap). External edits to the JSON are picked up by a debounced
file-watch reload (`ScheduleReload`).

**Live vs restart-required.** Changes that the running server/agent can apply
dynamically go live immediately; changes flagged restart-required are applied on
the next server start via reconciliation (see
[Dedicated Server Lifecycle](DedicatedServerLifecycle.md)).

---

## Related

- [Architecture › Configuration Management](../QuasarArchitecture.md#configuration-management)
- Back to the [State Machine Index](Index.md).
