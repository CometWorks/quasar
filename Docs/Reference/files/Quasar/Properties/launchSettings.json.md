# Quasar/Properties/launchSettings.json

**Module:** Quasar.Host  **Kind:** JSON config  **Tier:** 3

## Summary
Visual Studio / `dotnet run` launch profile configuration. Defines a single `http` profile for local development: runs the project directly (no IIS), binds to `http://0.0.0.0:5022`, and sets `ASPNETCORE_ENVIRONMENT=Development`. Browser auto-launch is disabled.

## Structure
Single profile `http`:
- `commandName`: `"Project"`
- `dotnetRunMessages`: `false`
- `launchBrowser`: `false`
- `applicationUrl`: `"http://0.0.0.0:5022"` — overrides Kestrel binding for `dotnet run` sessions
- `environmentVariables.ASPNETCORE_ENVIRONMENT`: `"Development"` — activates `appsettings.Development.json` and dev exception pages

## Notes
Port 5022 is used only when launching via this profile. Production deployments bind on port 58631 (from `appsettings.json`) or whatever `ASPNETCORE_URLS` specifies. The `ASPNETCORE_URLS` environment variable set by this profile via `applicationUrl` takes precedence over Kestrel configuration in `Program.cs`.
