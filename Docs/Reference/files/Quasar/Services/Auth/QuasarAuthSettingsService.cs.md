# Quasar/Services/Auth/QuasarAuthSettingsService.cs

**Module:** Quasar.Services.Auth  **Kind:** class + DTO  **Tier:** 2

## Summary
Persists trusted-network and reverse-proxy authentication settings edited from the Security page. It writes the `Quasar:Auth:TrustedNetworkBypass` block into the data-directory `appsettings.json`, validates trusted proxy entries as IP addresses or CIDR ranges, and updates the in-memory `QuasarAuthOptions` singleton so loopback/same-subnet changes take effect immediately.

## Structure
Namespace: `Quasar.Services.Auth`

Types:
- `sealed class QuasarAuthSettingsService`
- `sealed class QuasarTrustedNetworkSettings`

`QuasarAuthSettingsService` constructor: `(QuasarAuthOptions options)`

Public members:
- `SettingsPath : string` — data-directory `appsettings.json` path.
- `GetTrustedNetworkSettings() : QuasarTrustedNetworkSettings` — clones the live trusted-network bypass settings for editing.
- `SaveTrustedNetworkSettingsAsync(QuasarTrustedNetworkSettings, CancellationToken) : Task` — validates/normalizes proxy entries, patches `Quasar:Auth:TrustedNetworkBypass` in `appsettings.json`, atomically writes the file, then mutates the live options.
- `static ParseTrustedProxiesText(string) : List<string>` — splits newline/comma/semicolon/space separated proxy entries and validates them.

Private helpers:
- `ReadAppSettingsAsync` — reads or creates a root `JsonObject` for the data-directory settings file.
- `NormalizeTrustedProxies` / `IsValidProxyEntry` — validate exact IPs and CIDR prefix lengths.
- `GetOrCreateObject` — creates nested JSON objects while preserving unrelated settings.

`QuasarTrustedNetworkSettings`:
- `AllowLoopback`
- `AllowSameSubnet`
- `TrustedProxies`

## Dependencies
- [`Quasar/Services/Auth/QuasarAuthOptions.cs`](QuasarAuthOptions.cs.md)
- [`Quasar/Services/AtomicFileWriter.cs`](../AtomicFileWriter.cs.md)
- [`Magnetar.Protocol/Runtime/MagnetarPaths.cs`](../../../Magnetar.Protocol/Runtime/MagnetarPaths.cs.md)
- `System.Text.Json.Nodes`, `System.Net`

## Notes
Changing trusted proxy IP/CIDR entries requires a Quasar restart because ASP.NET Core forwarded-header middleware is configured at startup. The loopback and same-subnet toggles affect `TrustedNetworkEvaluator` immediately because it reads the mutated singleton options.
