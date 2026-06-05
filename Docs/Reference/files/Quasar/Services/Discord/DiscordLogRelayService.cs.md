# Quasar/Services/Discord/DiscordLogRelayService.cs

**Module:** Quasar.Services.Discord  **Kind:** class  **Tier:** 2

## Summary
Periodically tails the dedicated server's standard-output log file and posts new content as Discord code-block messages to a configured log channel. Tracks a per-server file offset to send only the delta since the last export, chunked to 1900 characters to respect Discord message limits.

## Structure
Namespace: `Quasar.Services.Discord`

`sealed class DiscordLogRelayService`

Constructor: `(DedicatedServerSupervisor supervisor, DiscordRateLimiter rateLimiter, ILogger<DiscordLogRelayService> logger)`

Public members:
- `StartAsync(DiscordSocketClient client, DiscordOptions options, CancellationToken) : Task` — spawns one loop task per enabled server where `EnableLogExport && LogChannelId.HasValue`; clears prior tasks first
- `Reset()` — clears offset cursors and task list; called on bot stop/restart

Private internals:
- `RunLoopAsync(client, serverOptions, ct)` — `PeriodicTimer` at `Max(1, LogExportIntervalMinutes)` minutes; calls `ExportAsync` each tick
- `ExportAsync(client, serverOptions, ct)` — reads `StandardOutputLogPath` from supervisor snapshot, calls `ReadDeltaAsync`, chunks and posts each chunk as a `` ```\n...\n``` `` code block via rate limiter
- `ReadDeltaAsync(uniqueName, filePath, ct)` — opens log file with `FileShare.ReadWrite`, seeks to stored offset (resets to 0 if file shrank), reads to end, updates offset; thread-safe via `_sync`
- `ChunkText(value, chunkSize)` — yields fixed-width substrings of 1900 chars
- `EscapeCodeBlock(value)` — replaces ```` ``` ```` with ```` ``​` ```` (zero-width space) to avoid Discord code-block termination

Inner types:
- `LogCursorState` — `FilePath : string`, `Offset : long` per server

## Dependencies
- [`Quasar/Services/Discord/DiscordOptions.cs`](DiscordOptions.cs.md) — `DiscordOptions`, `DiscordServerOptions`
- [`Quasar/Services/Discord/DiscordRateLimiter.cs`](DiscordRateLimiter.cs.md)
- `Quasar/Models/DedicatedServerSupervisor.cs` — `DedicatedServerSupervisor`, `DedicatedServerRuntimeSnapshot`
- Discord.Net — `DiscordSocketClient`, `IMessageChannel`

## Notes
The offset is reset to 0 if `offset > stream.Length`, handling log-file rotation or truncation. Opening with `FileShare.ReadWrite` allows reading while the SE server process holds the file open. Chunk size is 1900 (Discord limit is 2000; the code-block overhead accounts for the remainder).
