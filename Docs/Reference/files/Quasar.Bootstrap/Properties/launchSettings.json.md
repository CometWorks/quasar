# Quasar.Bootstrap/Properties/launchSettings.json

**Module:** Quasar.Bootstrap  **Kind:** JSON config  **Tier:** 3

## Summary
Visual Studio / `dotnet run` launch settings for the Bootstrap project. Defines a single `Dev` profile that invokes `ensure-running --open-browser` under `ASPNETCORE_ENVIRONMENT=Development`, so a developer can start the full supervisor stack (and have the browser open automatically) with a single run.

## Structure
| Key | Value |
|---|---|
| Profile name | `Dev` |
| `commandName` | `Project` |
| `commandLineArgs` | `ensure-running --open-browser` |
| `dotnetRunMessages` | `true` |
| `environmentVariables.ASPNETCORE_ENVIRONMENT` | `Development` |

## Dependencies
None.
