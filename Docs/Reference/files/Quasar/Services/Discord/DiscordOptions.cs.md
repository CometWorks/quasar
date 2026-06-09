# Quasar/Services/Discord/DiscordOptions.cs

**Module:** Quasar.Services.Discord  **Kind:** class  **Tier:** 2

## Summary
Configuration POCO hierarchy for the Discord bot integration, covering the bot token, guild ID, and per-server channel bindings for commands, chat relay, log export, analytics export, death messages, and simspeed alerts. Provides clone and normalise helpers used by `DiscordOptionsCatalog`.

## Structure
Namespace: `Quasar.Services.Discord`

All types are `sealed`.

**`DiscordOptions`** — root options
- `BotToken : string`
- `GuildId : ulong`
- `Enabled : bool`
- `Servers : List<DiscordServerOptions>`
- `Clone() : DiscordOptions` — deep clone
- `static Normalize(DiscordOptions?) : DiscordOptions` — trims token, normalises each server entry, sorts by `UniqueName` then `CommandPrefix`

**`DiscordServerOptions`** — per-SE-server Discord bindings
- `UniqueName : string` — matches the SE server unique name
- `CommandPrefix : string` — e.g. `"!se"`
- `CommandChannelId : ulong?`, `ChatRelayChannelId : ulong?`, `LogChannelId : ulong?`, `AnalyticsChannelId : ulong?`, `DeathChannelId : ulong?`, `SimSpeedAlertChannelId : ulong?`
- `LogExportIntervalMinutes : int` (default 30), `AnalyticsExportIntervalMinutes : int` (default 60)
- `EnableChatRelay : bool` (default true), `EnableLogExport : bool` (default true), `EnableAnalyticsExport : bool` (default true), `EnableDeathMessages : bool` (default false)
- `EnableSimSpeedAlerts`, `EnableSimSpeedSharpDropAlerts`, `EnableSimSpeedSustainedAlerts` — per-server alert toggles, default true
- `SimSpeedSharpDropPreviousMinimum`, `SimSpeedSharpDropThreshold`, `SimSpeedSharpDropDelta`, `SimSpeedSharpDropCooldownSeconds` — baseline sharp-drop rule fields
- `SimSpeedSustainedThreshold`, `SimSpeedSustainedSeconds`, `SimSpeedSustainedCooldownSeconds` — baseline sustained-loss rule fields
- `DeathMessageEmotes : string` (default `"💀,⚔️,🔥,💥,☠️"`)
- `RelayNonCommandMessages : bool` (default false)
- `[JsonIgnore] HasCommandBinding : bool` — `CommandChannelId.HasValue && !string.IsNullOrWhiteSpace(CommandPrefix)`
- `Clone()`, `static Normalize(DiscordServerOptions?)`
- `private static NormalizeChannelId(ulong?)` — returns null for zero values
- `private static NormalizeRatio(float, float)` / `NormalizeSeconds(int, int, int, int)` — clamp alert rule values into sane ranges

## Dependencies
- `System.Text.Json.Serialization` — `[JsonIgnore]`

## Notes
Channel IDs of 0 are normalised to `null` by `NormalizeChannelId`, preventing accidental routing to channel 0. Simspeed alert rules default to a sharp second-to-second drop and sustained-low-average detection and can be tuned per server. `HasCommandBinding` is a convenience check used by the UI to show whether a server entry is command-enabled.
