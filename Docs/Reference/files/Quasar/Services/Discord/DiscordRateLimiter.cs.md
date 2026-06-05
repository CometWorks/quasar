# Quasar/Services/Discord/DiscordRateLimiter.cs

**Module:** Quasar.Services.Discord  **Kind:** class  **Tier:** 2

## Summary
Per-channel Discord send rate limiter that enforces a minimum 300 ms interval between messages on any given channel, preventing Discord API rate-limit errors. Used by all relay and export services that post messages.

## Structure
Namespace: `Quasar.Services.Discord`

`sealed class DiscordRateLimiter`

No constructor parameters.

Public members:
- `RunAsync(ulong channelId, Func<Task> action, CancellationToken) : Task` — serialises all callers for a given channel via a `SemaphoreSlim(1,1)`; waits until `NextAllowedUtc` before executing `action`; updates `NextAllowedUtc = UtcNow + 300ms` after a successful call

Inner types:
- `ChannelRateState` — `SemaphoreSlim Gate` (1,1) + `DateTimeOffset NextAllowedUtc` per channel
- State is created lazily via `ConcurrentDictionary.GetOrAdd`

## Dependencies
- `System.Collections.Concurrent` (BCL)

## Notes
The 300 ms minimum matches Discord's documented per-channel burst threshold. Because the gate is a semaphore, concurrent callers queue up rather than racing. State is never evicted from `_states`; in practice the number of channels is small and bounded by the configured instances. `RunAsync` propagates `OperationCanceledException` from both the semaphore wait and the delay.
