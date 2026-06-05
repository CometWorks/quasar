# Quasar/Services/Discord/DeathMessagesConfig.cs

**Module:** Quasar.Services.Discord  **Kind:** class  **Tier:** 2

## Summary
Data model holding per-death-type message template lists for the Discord death-relay feature. Provides a `GetRandomMessage` picker and factory/clone helpers used by `DeathMessagesCatalog`.

## Structure
Namespace: `Quasar.Services.Discord`

`sealed class DeathMessagesConfig`

Properties (all `List<string>` with built-in defaults):
- `SuicideMessages` — 6 templates using `{victim}`
- `PvPMessages` — 6 templates using `{victim}`, `{killer}`, `{weapon}`
- `TurretMessages` — 6 templates using `{victim}`, `{killer}`, `{weapon}`
- `GridMessages` — 6 templates using `{victim}`
- `OxygenMessages` — 5 templates using `{victim}`
- `PressureMessages` — 5 templates using `{victim}`
- `CollisionMessages` — 6 templates using `{victim}`
- `AccidentMessages` — 6 templates using `{victim}`

Methods:
- `GetRandomMessage(string deathType) : string` — maps death type string (`"Suicide"`, `"PvP"`, `"Turret"`, `"Grid"`, `"Oxygen"`, `"Pressure"`, `"Collision"`, `"Accident"`) to the appropriate list, picks at random; unknown types fall back to `AccidentMessages`; empty list returns `"{victim} died"`
- `static CreateDefault() : DeathMessagesConfig` — returns a new instance with all default lists
- `Clone() : DeathMessagesConfig` — shallow copy of each list

## Dependencies
None (no project file references; BCL only).

## Notes
Templates use named `{victim}`, `{killer}`, and `{weapon}` placeholders replaced by `DiscordDeathRelayService.BuildMessage`. `Random.Shared` is used for selection.
