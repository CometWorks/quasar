# Quasar/appsettings.Development.json

**Module:** Quasar.Host  **Kind:** JSON config  **Tier:** 3

## Summary
Development-environment override for `appsettings.json`, loaded when `ASPNETCORE_ENVIRONMENT=Development`. Enables `DetailedErrors` for Blazor circuit diagnostics and carries a standard `Logging` block with the same log levels as the base file.

## Structure
- `DetailedErrors`: `true` — surfaces full server-side exception details to the browser during development
- `Logging.LogLevel.Default`: `Information`
- `Logging.LogLevel.Microsoft.AspNetCore`: `Warning`

No `Quasar` section overrides; all Quasar settings fall through to the base `appsettings.json` or deployment-specific files.

## Dependencies
- [`Quasar/appsettings.json`](appsettings.json.md) (overrides base values)
- [`Quasar/Properties/launchSettings.json`](Properties/launchSettings.json.md) (sets `ASPNETCORE_ENVIRONMENT=Development` for the dev profile)
