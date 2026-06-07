# Quasar/Properties/launchSettings.json

**Module:** Quasar.Host  **Kind:** JSON config  **Tier:** 3

## Summary
Visual Studio / `dotnet run` launch profile configuration. Defines a single `http` profile for local development: runs the project directly (no IIS), binds to `http://0.0.0.0:8080`, and sets `ASPNETCORE_ENVIRONMENT=Development`. Browser auto-launch is disabled.

## Structure
Single profile `http`:
- `commandName`: `"Project"`
- `dotnetRunMessages`: `false`
- `launchBrowser`: `false`
- `applicationUrl`: `"http://0.0.0.0:8080"` — overrides Kestrel binding for `dotnet run` sessions
- `environmentVariables.ASPNETCORE_ENVIRONMENT`: `"Development"` — activates `appsettings.Development.json` and dev exception pages

## Notes
Port 8080 is used both by this development profile and by production deployments (the default `Quasar:Port` in `appsettings.json`). The `ASPNETCORE_URLS` environment variable set by this profile via `applicationUrl` takes precedence over Kestrel configuration in `Program.cs`. Running via Bootstrap (installed service) ignores `applicationUrl` — use `appsettings.json` `Quasar:Port` for port changes in that case (see `Docs/Configuration.md`).
