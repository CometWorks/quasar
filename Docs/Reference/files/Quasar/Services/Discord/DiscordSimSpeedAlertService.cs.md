# Quasar/Services/Discord/DiscordSimSpeedAlertService.cs

**Module:** Quasar.Services.Discord  **Kind:** class  **Tier:** 2

## Summary

Event-driven Discord alert evaluator for simspeed degradation. It runs on agent registry changes, reads fresh raw analytics samples for connected/running agents, scans unseen adjacent sample pairs for sharp drops, evaluates sustained-loss rules from `DiscordServerOptions`, applies per-rule cooldown state, and sends Discord embeds through the configured simspeed alert channel or the server's analytics channel.

## Structure

Namespace: `Quasar.Services.Discord`

**`DiscordSimSpeedAlertService`** (`public sealed class`)

Constructor: `(AgentRegistry, MetricsStoreService, DiscordRateLimiter, ILogger<DiscordSimSpeedAlertService>)`

Key members:
- `HandleChangedAsync(DiscordSocketClient, DiscordOptions, CancellationToken)` — filters enabled server rules, requires a connected/running agent, reads latest raw samples, skips stale data, evaluates rules, and sends alert embeds through `DiscordRateLimiter`.
- `Reset()` — clears per-server cooldown/evaluation state when the Discord bot restarts.
- `Evaluate(...)` — lock-guarded rule evaluation; remembers already-processed raw sample timestamps.
- `TryAddSharpDropAlert(...)` — scans every unseen adjacent raw sample pair for a drop from healthy simspeed to below the configured current threshold.
- `TryAddSustainedAlert(...)` — detects average simspeed below the configured threshold across the configured raw-sample window.
- `BuildEmbed(...)` — builds compact Discord embed payloads with timestamp and rule fields.

Private state:
- `SimSpeedRuleState` — last evaluated sample timestamp plus independent sharp-drop and sustained-loss alert cooldown timestamps.

## Dependencies

- [`Quasar/Services/AgentRegistry.cs`](../AgentRegistry.cs.md)
- [`Quasar/Services/Analytics/MetricsStoreService.cs`](../Analytics/MetricsStoreService.cs.md)
- [`Quasar/Services/Analytics/MetricSample.cs`](../Analytics/MetricSample.cs.md)
- [`Quasar/Services/Discord/DiscordOptions.cs`](DiscordOptions.cs.md)
- [`Quasar/Services/Discord/DiscordRateLimiter.cs`](DiscordRateLimiter.cs.md)
- Discord.Net — `DiscordSocketClient`, `IMessageChannel`, `EmbedBuilder`, `Color`

## Notes

The service uses raw samples because the sharp-drop rule needs sample-to-sample changes and the sustained rule needs short windows. It ignores samples older than 30 seconds, scans only fresh unseen pairs on first evaluation, and only evaluates servers whose current agent snapshot says the simulation is running, avoiding startup/offline false positives.
