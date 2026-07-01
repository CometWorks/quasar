# Backup Jobs

Quasar takes versioned ZIP backups of configuration, per-server runtime state,
and world-only data. Automatic backups are queued by a scheduler (or manually)
and run one at a time, emitting a completion event and pruning old archives by
retention policy.

Relevant source:
[`AutomaticBackupService.cs`](../../Quasar/Services/Backup/AutomaticBackupService.cs),
[`QuasarBackupManifest.cs`](../../Quasar/Models/QuasarBackupManifest.cs),
[`BackupCompatibility.cs`](../../Quasar/Services/Backup/BackupCompatibility.cs),
[`BackupFormatMigrations.cs`](../../Quasar/Services/Backup/BackupFormatMigrations.cs).

There are three [`QuasarBackupKind`](../../Quasar/Models/QuasarBackupManifest.cs)
values — `Configuration`, `Server`, `World` — corresponding to the queued job
kinds `EnabledRules`, `Server`, and `World`.

---

## Queued job lifecycle

```mermaid
stateDiagram-v2
    [*] --> Queued
    Queued --> Running: scheduler tick or manual dequeue
    Running --> Succeeded: archive written
    Running --> Failed: error / nothing to back up
    Succeeded --> [*]: QueuedBackupCompleted event
    Failed --> [*]: QueuedBackupCompleted event
    note right of Succeeded
        Retention prune removes the oldest
        archives beyond RetentionCount
    end note
```

![Backup job lifecycle](diagrams/backup-job.png)

| State | Meaning |
| --- | --- |
| `Queued` | A `QueuedBackupJob` (unique id + kind) is on the unbounded channel — enqueued by the per-minute scheduler tick for due rules, or by a manual request. |
| `Running` | `RunQueuedBackupAsync` dequeues and runs the kind-specific backup. |
| `Succeeded` / `Failed` | Result carries `Success`, `CreatedCount`, `Message`, and an optional exception; raised as `QueuedBackupCompleted`. |

After a successful backup, `PruneAutomaticBackups` removes the oldest archives
beyond the rule's `RetentionCount`. Each automatic backup kind (config / server /
world) has its own schedule and retention.

---

## Backup scopes and restore behavior

- `Configuration` backups contain Quasar-managed catalog and settings files:
  server definitions, config profiles, world-template definitions, branding,
  Discord, players, security/RBAC and other singleton settings. They do not
  include Dedicated Server app data, Magnetar app data or world save files.
- `Server` backups contain one server definition plus that server's non-cache
  Dedicated Server and Magnetar app data. They do not include world save files.
- `World` backups contain world save files for one server. They use the latest
  valid Space Engineers `Backup` snapshot when one exists and leave the current
  server/world config in place on restore.

Restored server definitions are always written with `GoalState = Off` and
`AutoStart = false`, both for server backups and configuration backups that
contain server definitions. This prevents restore from re-starting a server
before its matching world files have been restored.

---

## Restore compatibility

Restore is gated by a semantic-version check
([`BackupCompatibility.Evaluate`](../../Quasar/Services/Backup/BackupCompatibility.cs)):

- same `Major.Minor` → allowed;
- older backup → allowed only if [`BackupFormatMigrations.CanMigrate`](../../Quasar/Services/Backup/BackupFormatMigrations.cs)
  finds a contiguous upgrade path through the registered migration steps;
- newer backup than the running build → rejected (no downgrade).

The archive's `quasar-backup.json` manifest records `FormatVersion` (archive
layout) and `QuasarVersion` (the build it was taken from) to drive these rules.

---

## Related

- [Architecture › Configuration Management](../QuasarArchitecture.md#configuration-management)
- Back to the [State Machine Index](Index.md).
