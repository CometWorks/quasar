# Quasar/Quasar.csproj

**Module:** Quasar.Host  **Kind:** project file  **Tier:** 3

## Summary
MSBuild project file for the Quasar Blazor Server host. Targets `net10.0` using the `Microsoft.NET.Sdk.Web` SDK, references the shared `Magnetar.Protocol` project, and declares NuGet packages for Steam auth, local storage, Discord, MudBlazor, NLog, and SharpCompress. Includes custom build targets to compile `Quasar.Agent` and stage its DLLs alongside the host output.

## Structure

**PropertyGroup:**
- `TargetFramework`: `net10.0`
- `Nullable`, `ImplicitUsings`: enabled
- `AssemblyName` / `RootNamespace` / `PackageId` / `Product`: `Quasar`
- `BlazorDisableThrowNavigationException`: `true` — suppresses Blazor navigation exception propagation
- `DeployDir`: `$(HOME)/Documents/Quasar` — default local deploy target

**ProjectReference:**
- `../Magnetar.Protocol/Magnetar.Protocol.csproj`

**PackageReferences:**
| Package | Version |
|---|---|
| `AspNet.Security.OpenId.Steam` | 10.0.0 |
| `Blazor.LocalStorage` | 10.0.0 |
| `Discord.Net` | 3.16.0 |
| `MudBlazor` | 9.4.0 |
| `NLog.Web.AspNetCore` | 6.1.3 |
| `SharpCompress` | 0.49.1 |

**Custom MSBuild targets:**
- `CopyToDeployDir` (AfterTargets=Build, conditional on `CopyToDeployDir != false`) — copies output (excluding `.pdb`, `.xml`, and the main executable) to `$(DeployDir)`.
- `BuildQuasarAgent` (BeforeTargets=Build;Publish) — builds `../Quasar.Agent/Quasar.Agent.csproj` for `netstandard2.0` / `x64`, explicitly clearing `RuntimeIdentifier`, `SelfContained`, and `PublishSingleFile` to prevent property bleed-in from publish.
- `StageQuasarAgent` (AfterTargets=Build) — copies `Quasar.Agent.dll` and `Magnetar.Protocol.dll` from agent output into `$(OutputPath)Agent\`.
- `StageQuasarAgentForPublish` (AfterTargets=Publish) — same copy but to `$(PublishDir)Agent\`.

## Dependencies
- `Magnetar.Protocol/Magnetar.Protocol.csproj`
- `Quasar.Agent/Quasar.Agent.csproj` (built as a side-effect, not a `<ProjectReference>`)

## Notes
`Quasar.Agent` is intentionally a build-time side-effect rather than a `<ProjectReference>` because the agent targets `netstandard2.0` (to load inside Space Engineers) while the host targets `net10.0`. The agent DLLs are staged into `Agent/` and deployed at runtime by `DedicatedServerRuntimePreparer`.
