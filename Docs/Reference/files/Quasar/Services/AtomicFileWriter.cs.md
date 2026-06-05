# Quasar/Services/AtomicFileWriter.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary
`AtomicFileWriter` is a static utility that writes text files atomically using the write-to-temp-then-rename pattern, ensuring the target file is never left in a partially written state. All supervisor, catalog, and branding persistence goes through this helper.

## Structure
Namespace: `Quasar.Services`

**`AtomicFileWriter`** — `static class`

| Member | Notes |
|--------|-------|
| `WriteTextAsync(string path, string content, CancellationToken)` | Creates parent directories if absent; writes to a randomly named `.{filename}.{guid}.tmp` sibling; flushes fully; then calls `File.Move(…, overwrite: true)` for an atomic rename |
| `TryDelete(string path)` | Private best-effort cleanup of the temp file on cancellation or I/O fault |

UTF-8 encoding without BOM (`encoderShouldEmitUTF8Identifier: false`).

## Dependencies
None (pure BCL).

## Notes
If the write is cancelled or throws before the rename, the target file is untouched. The temp file is cleaned up best-effort so orphaned `.tmp` files do not accumulate. `File.Move` with `overwrite: true` is atomic on POSIX (single-filesystem rename); on Windows it is a best-effort swap. The method creates the target directory with `Directory.CreateDirectory` so callers do not need to pre-create paths.
