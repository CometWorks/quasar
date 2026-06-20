# Quasar/Services/WebServiceManifestHostedService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`IHostedService` that writes a `WebServiceDiscoveryManifest` JSON file on startup (once the Kestrel server is bound) and deletes it on shutdown, enabling Quasar.Bootstrap and other tools to discover the running supervisor instance by filesystem polling. It also writes an "active release pointer" file so the bootstrap knows which executable launched this instance, and optionally opens the browser on first start.

## Structure

Namespace: `Quasar.Services`

**`WebServiceManifestHostedService`** — sealed class implementing `IHostedService`.

| Member | Description |
|---|---|
| `StartAsync(ct)` | Registers `WriteManifest` on `ApplicationStarted` (deferred until Kestrel is bound). No-op if `OwnManifest` is false. |
| `StopAsync(ct)` | Deletes the manifest file if it exists. No-op if `OwnManifest` is false. |
| `WriteManifest()` | Resolves effective base URL from `IServerAddressesFeature`, builds `WebServiceDiscoveryManifest`, writes it to disk, updates `WebServiceState.CurrentManifest`, writes active-release pointer, optionally opens browser. |
| `ResolveBaseUrl()` | Normalises wildcard bind addresses (`0.0.0.0`, `[::]`, `*`, `+`) to `127.0.0.1`. |
| `BuildActiveReleasePointer()` | Detects single-file vs DLL deployment; constructs `QuasarActiveReleasePointer` accordingly. |
| `PreserveExistingVersionIfSameRelease(pointer)` | Keeps the existing active-release version when the file/args/working directory match the current worker, preserving Bootstrap's staged release identity. |
| `WriteActiveReleasePointer()` | Writes the pointer JSON to the active-release path. |

## Dependencies

- [`Quasar/Services/WebServiceOptions.cs`](WebServiceOptions.cs.md) — `OwnManifest`, `HostId`, `HostName`, `WorkerId`, `Version`, `IsServiceMode`
- [`Quasar/Services/WebServiceState.cs`](WebServiceState.cs.md) — `CurrentManifest` is set here
- `Magnetar.Protocol.Discovery` — `WebServiceDiscoveryManifest`
- `Magnetar.Protocol.Runtime` — `MagnetarPaths`
- `Microsoft.AspNetCore.Hosting.Server.IServer` / `IServerAddressesFeature`
- `BrowserLauncher` (intra-project utility)

## Notes

The manifest write is registered via `ApplicationStarted` callback (not directly in `StartAsync`) to guarantee the port is bound before writing — avoids a race where Bootstrap reads the manifest before Kestrel is ready. The worker preserves the active-release pointer's version when it launched from that pointer, so a staged `1.0.0-main.N` release is not overwritten with a numeric assembly version. In non-service mode the supervisor name and base URL are also written to stdout so a parent process can capture them. The IL3000 warning for `Assembly.GetEntryAssembly()?.Location` is suppressed with an inline explanation of the single-file fallback.
