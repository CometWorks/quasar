# Quasar/Quasar.csproj

**Module:** Quasar.Host  **Kind:** project file  **Tier:** 3

## Summary
MSBuild project file for the Quasar Blazor Server host. Targets `net10.0` using the `Microsoft.NET.Sdk.Web` SDK, references the shared `Magnetar.Protocol` project, and declares NuGet packages for Steam auth, local storage, Discord, MudBlazor, NLog, SharpCompress, and a private build-only Harmony path reference. Includes custom build targets to compile `Quasar.Agent` and stage its DLLs plus runtime-specific Harmony DLLs alongside the host output.

## Structure

**PropertyGroup:**
- `TargetFramework`: `net10.0`
- `Nullable`, `ImplicitUsings`: enabled
- `AssemblyName` / `RootNamespace` / `PackageId` / `Product`: `Quasar`
- `Version`, `AssemblyVersion`, `FileVersion`: `0.1.0` defaults; Linux release packaging overrides `Version`/`InformationalVersion` with the release identity
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
| `Lib.Harmony` | 2.4.2 (`PrivateAssets=all`, `ExcludeAssets=all`, `GeneratePathProperty=true`) |
| `MudBlazor` | 9.4.0 |
| `NLog.Web.AspNetCore` | 6.1.3 |
| `SharpCompress` | 0.49.1 |

**Custom MSBuild targets:**
- `CopyToDeployDir` (AfterTargets=Build, conditional on `CopyToDeployDir != false`) — copies output (excluding `.pdb`, `.xml`, and the main executable) to `$(DeployDir)`.
- `BuildQuasarAgent` (BeforeTargets=Build;Publish) — builds `../Quasar.Agent/Quasar.Agent.csproj` for `netstandard2.0` / `x64` only when the staged DLLs are missing. It invokes `dotnet build` directly with RID and single-file publish properties cleared so parent publish globals do not leak into the agent restore/build.
- `StageQuasarAgent` (AfterTargets=Build) — verifies the restored `Lib.Harmony` package path, copies `Quasar.Agent.dll` and `Magnetar.Protocol.dll` from agent output into `$(OutputPath)Agent\`, then stages `0Harmony.dll` under `Agent\DotNet10\` and `Agent\NetFramework48\`.
- `StageQuasarAgentForPublish` (AfterTargets=Publish) — same verification and copy but to `$(PublishDir)Agent\`.

## Dependencies
- [`Magnetar.Protocol/Magnetar.Protocol.csproj`](../Magnetar.Protocol/Magnetar.Protocol.csproj.md)
- [`Quasar.Agent/Quasar.Agent.csproj`](../Quasar.Agent/Quasar.Agent.csproj.md) (built as a side-effect, not a `<ProjectReference>`)

## Notes
`Quasar.Agent` is intentionally a build-time side-effect rather than a `<ProjectReference>` because the agent targets `netstandard2.0` (to load inside Space Engineers) while the host targets `net10.0`. The agent DLLs are staged into `Agent/` and deployed at runtime by `DedicatedServerRuntimePreparer`; Harmony is staged in runtime subfolders because Magnetar can launch either .NET 10 or .NET Framework 4.8 on Windows. The host project uses NuGet's generated `$(PkgLib_Harmony)` property instead of `$(NuGetPackageRoot)` so CI and local restores resolve the package from the actual restore assets.
