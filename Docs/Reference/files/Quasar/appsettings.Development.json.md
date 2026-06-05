# Quasar/appsettings.Development.json

**Module:** Quasar.Host  **Kind:** JSON config  **Tier:** 3

## Summary
Development-environment override for `appsettings.json`. Currently contains only the standard `Logging` section with no effective changes from the base file (same log levels). Loaded when `ASPNETCORE_ENVIRONMENT=Development`.

## Structure
- `Logging.LogLevel.Default`: `Information`
- `Logging.LogLevel.Microsoft.AspNetCore`: `Warning`

No `Quasar` section overrides; all Quasar settings fall through to the base `appsettings.json` or deployment-specific files.

## Dependencies
- [`Quasar/appsettings.json`](appsettings.json.md) (overrides base values)
- [`Quasar/Properties/launchSettings.json`](Properties/launchSettings.json.md) (sets `ASPNETCORE_ENVIRONMENT=Development` for the dev profile)
