# Quasar.Host — Application Host & Wiring

*Module `Quasar.Host` — 7 files.* See the [handbook TOC](../TOC.md) and the [file Index](../Index.md).

The Blazor Server application host and composition root. `Program.cs` builds the dependency-injection graph (the singletons and hosted services that make up the supervisor), configures Steam OpenID authentication with role-based authorization policies and a trusted-network bypass, registers the Razor components and MudBlazor, and maps the HTTP/WebSocket endpoints — `/ws/agent` for agents, `/api/health` and `/api/discovery` for discovery/health, `/api/internal/drain` for graceful handoff, and the login/logout flow. This module also holds the project file, `appsettings`, launch profile, and the `wwwroot` static assets (global CSS and the JS-interop helpers).

## Files

| File | Kind | Summary |
| --- | --- | --- |
| [Quasar/Program.cs](../files/Quasar/Program.cs.md) | class | The ASP.NET Core / Blazor Server entry point for the Quasar supervisor host. `Program.Main` builds the `WebApplication`, registers all DI services, configures authentication and authorization, wires middleware, maps HTTP/WebSocket endpoints, and runs the application. It is the system wiring hub — every significant service in the process is registered here. |
| [Quasar/Properties/launchSettings.json](../files/Quasar/Properties/launchSettings.json.md) | JSON config | Visual Studio / `dotnet run` launch profile configuration. Defines a single `http` profile for local development: runs the project directly (no IIS), binds to `http://0.0.0.0:5022`, and sets `ASPNETCORE_ENVIRONMENT=Development`. Browser auto-launch is disabled. |
| [Quasar/Quasar.csproj](../files/Quasar/Quasar.csproj.md) | project file | MSBuild project file for the Quasar Blazor Server host. Targets `net10.0` using the `Microsoft.NET.Sdk.Web` SDK, references the shared `Magnetar.Protocol` project, and declares NuGet packages for Steam auth, local storage, Discord, MudBlazor, NLog, and SharpCompress. Includes custom build targets to compile `Quasar.Agent` and stage its DLLs alongside the host output. |
| [Quasar/appsettings.Development.json](../files/Quasar/appsettings.Development.json.md) | JSON config | Development-environment override for `appsettings.json`. Currently contains only the standard `Logging` section with no effective changes from the base file (same log levels). Loaded when `ASPNETCORE_ENVIRONMENT=Development`. |
| [Quasar/appsettings.json](../files/Quasar/appsettings.json.md) | JSON config | Default application configuration file for the Quasar host. Provides baseline values for the `Quasar` options section (network, managed runtime paths, logging, auth) and ASP.NET Core logging. All keys are overridable via environment-specific `appsettings.{env}.json`, environment variables, or command-line arguments as resolved by `Program.AddDeploymentConfigurationSources`. |
| [Quasar/wwwroot/app.css](../files/Quasar/wwwroot/app.css.md) | CSS | Global stylesheet for the Quasar Blazor Server UI. Overrides MudBlazor's elevation shadows with a flatter, lower-opacity variant; establishes base layout styles; and defines application-specific utility and component classes that complement the MudBlazor theme. |
| [Quasar/wwwroot/quasar-configs.js](../files/Quasar/wwwroot/quasar-configs.js.md) | JS | Small JavaScript interop module registered as `window.quasarConfigs`. Provides three utility functions called from Blazor components via `IJSRuntime.InvokeAsync`. The object is initialised lazily with `\|\|=` to allow safe re-evaluation. |

## Depends on

- [Magnetar.Protocol](Magnetar.Protocol.md)
- [Quasar.Agent](Quasar.Agent.md)
- [Quasar.Components](Quasar.Components.md)
- [Quasar.Models](Quasar.Models.md)
- [Quasar.Services.Auth](Quasar.Services.Auth.md)
- [Quasar.Services.Core](Quasar.Services.Core.md)
