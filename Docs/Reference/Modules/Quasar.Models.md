# Quasar.Models — Domain Models

*Module `Quasar.Models` — 10 files.* See the [handbook TOC](../TOC.md) and the [file Index](../Index.md).

Plain domain-model layer with no behaviour or DI. It centres on the managed DS instance: the persisted `DedicatedServerDefinition`, the volatile `DedicatedServerRuntimeSnapshot`, and the lifecycle enums (goal / process / health state and process priority). It also defines the config-profile model (`QuasarConfigProfile`, covering world-root and ~90 session settings plus plugins/mods), world templates, branding settings and palette, and known-player records. These types are produced and consumed throughout [Quasar.Services.Core](Quasar.Services.Core.md) and the [UI](Quasar.Components.md).

## Files

| File | Kind | Summary |
| --- | --- | --- |
| [Quasar/Models/BrandingSettings.cs](../files/Quasar/Models/BrandingSettings.cs.md) | class | Defines the persistent branding and theme configuration serialized to `branding.json`. Contains two sealed classes: `BrandingSettings` (app identity + palette references) and `ThemePalette` (a flat string-valued mirror of MudBlazor's `PaletteLight`/`PaletteDark`). Both classes support cloning and normalization with fallback to the built-in Quasar defaults. |
| [Quasar/Models/DedicatedServerDefinition.cs](../files/Quasar/Models/DedicatedServerDefinition.cs.md) | class | Persistent configuration record for a single managed Space Engineers dedicated server. Contains all fields needed to launch, supervise, health-monitor, and schedule restarts for a server, as well as process-priority settings. Serialized to disk as part of the server catalog. |
| [Quasar/Models/DedicatedServerGoalState.cs](../files/Quasar/Models/DedicatedServerGoalState.cs.md) | enum | Two-value enum expressing the operator's intent for a managed dedicated server: `Off` (should not be running) or `On` (should be running). The supervisor reconciles actual process state against this goal. |
| [Quasar/Models/DedicatedServerHealthState.cs](../files/Quasar/Models/DedicatedServerHealthState.cs.md) | enum | Four-level health classification for a running dedicated server, set by the health-monitoring subsystem and surfaced in the dashboard. Drives automatic restart decisions when combined with `AutoRestartOnUnhealthy`. |
| [Quasar/Models/DedicatedServerProcessPriority.cs](../files/Quasar/Models/DedicatedServerProcessPriority.cs.md) | enum | Five-level OS process priority enum used to configure the Windows/Linux process priority of a managed DS instance at startup and after it has fully loaded. Maps to the standard `ProcessPriorityClass` levels. |
| [Quasar/Models/DedicatedServerProcessState.cs](../files/Quasar/Models/DedicatedServerProcessState.cs.md) | enum | State machine enum representing the actual OS-process lifecycle of a managed dedicated server, as tracked by `DedicatedServerSupervisor`. Exposed in `DedicatedServerRuntimeSnapshot` and used by the `/api/health` endpoint to count running servers. |
| [Quasar/Models/DedicatedServerRuntimeSnapshot.cs](../files/Quasar/Models/DedicatedServerRuntimeSnapshot.cs.md) | class | Immutable-by-convention snapshot of a server's live runtime state as maintained by `DedicatedServerSupervisor`. Combines process-lifecycle state, health classification, simulation-performance metrics, agent connectivity, and log paths into a single transferable object used by the dashboard and API. |
| [Quasar/Models/KnownPlayerRecord.cs](../files/Quasar/Models/KnownPlayerRecord.cs.md) | class | Persistent record for a player ever observed on any managed server, stored in `KnownPlayerCatalog`. Tracks identity across multiple servers via a composite `PlayerKey`, records Steam and platform metadata, faction/admin/ban status, last ping, and first/last-seen timestamps. |
| [Quasar/Models/QuasarConfigProfile.cs](../files/Quasar/Models/QuasarConfigProfile.cs.md) | class | Defines the configuration profile model for Space Engineers dedicated server instances managed by Quasar. A profile bundles world root settings, session settings, plugin selections, and mod selections that can be applied to one or more instances. Also defines the `QuasarNetworkType` enum with its custom JSON converter, and supporting sub-models for plugins, mods, catalog entries, and dev-folder selections. |
| [Quasar/Models/QuasarWorldTemplate.cs](../files/Quasar/Models/QuasarWorldTemplate.cs.md) | class | Minimal DTO representing a world template entry in the Quasar catalog. A world template is a named reference (with optional description) to a pre-configured world that can be assigned to one or more server instances via `WorldTemplateId`. |

## Depends on

- [Quasar.Host](Quasar.Host.md)
- [Quasar.Services.Core](Quasar.Services.Core.md)
