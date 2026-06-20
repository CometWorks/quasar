# Magnetar.Protocol/Runtime/QuasarReleaseVersion.cs

**Module:** Magnetar.Protocol  **Kind:** class (static)  **Tier:** 1

## Summary
Shared release-version helper used by the Quasar worker and Bootstrap launcher. It reads `AssemblyInformationalVersion`, normalizes GitHub tag names, and compares release versions with prerelease-aware ordering so update checks only report a candidate when it is actually newer.

## Structure
Namespace `Magnetar.Protocol.Runtime`; `public static class QuasarReleaseVersion`.

- `GetEntryAssemblyVersion()` returns normalized `AssemblyInformationalVersion` when present, falling back to assembly version only when no informational metadata exists.
- `Normalize(value)` extracts a version from tags or metadata, strips a leading `v`, treats `1.0.0.0` as `1.0.0`, keeps four-part build-number identities such as `1.0.0.37` canonical, and maps numeric prerelease aliases such as `1.0.0-37` to `1.0.0.37`.
- `IsNewer(candidate, current)` normalizes both inputs and compares numeric core components plus semver-style prerelease labels.
- Private parsing/comparison helpers handle two-, three-, and four-part numeric cores and dot-separated prerelease identifiers.

## Dependencies
- `System`, `System.Linq`, `System.Reflection`, `System.Text.RegularExpressions` (BCL only).

## Notes
This intentionally keeps `AssemblyVersion` available as a stable ABI value while moving update decisions to the release identity. A candidate with the same normalized prerelease/build label as the current version is not newer, preventing Bootstrap from repeatedly draining the worker after every GitHub check.
