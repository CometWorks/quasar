# Quasar/Quasar.csproj

**Module:** Quasar.Host  **Kind:** project file  **Tier:** 3

## Summary
MSBuild project file for the Quasar Blazor Server host. Targets `net10.0` using the `Microsoft.NET.Sdk.Web` SDK, references the shared `Magnetar.Protocol` project, and declares NuGet packages for Steam auth, local storage, Discord, MudBlazor, NLog, SharpCompress, and a private build-only Harmony path reference. Includes custom build targets to restore exact npm packages for the standalone grid viewer, stage their full package contents under `wwwroot/vendor`, compile `Quasar.Agent`, and stage its DLLs plus runtime-specific Harmony DLLs alongside the host output.

## Structure

**PropertyGroup:**
- `TargetFramework`: `net10.0`
- `Nullable`, `ImplicitUsings`: enabled
- `AssemblyName` / `RootNamespace` / `PackageId` / `Product`: `Quasar`
- `Version`, `AssemblyVersion`, `FileVersion`: `0.1.0` defaults; Linux release packaging overrides `Version`/`InformationalVersion` with the release identity
- `BlazorDisableThrowNavigationException`: `true` — suppresses Blazor navigation exception propagation

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
- `RestoreViewerNpmPackages` — runs `npm ci --ignore-scripts` from `Quasar/package-lock.json` when the npm manifest or lock file changes.
- `StageViewerNpmPackages` (BeforeTargets=AssignTargetPaths) — copies the full `three` and `@zip.js/zip.js` npm package directories from `node_modules` into `wwwroot/vendor` and adds those generated files to the static web asset content set for build/publish.
- `BuildQuasarAgent` (BeforeTargets=Build;Publish) — builds `../Quasar.Agent/Quasar.Agent.csproj` for `netstandard2.0` / `x64` only when the staged DLLs are missing. It invokes `dotnet build` directly with RID and single-file publish properties cleared so parent publish globals do not leak into the agent restore/build.
- `ResolveHarmonyPackageFiles` — resolves the restored `Lib.Harmony` package root from NuGet-generated properties, explicit NuGet package roots, or the default user package cache, then selects the best available .NET and .NET Framework `0Harmony.dll` assets.
- `StageQuasarAgent` (AfterTargets=Build) — copies `Quasar.Agent.dll` and `Magnetar.Protocol.dll` from agent output into `$(OutputPath)Agent\`, then stages the resolved `0Harmony.dll` files under `Agent\DotNet10\` and `Agent\NetFramework48\`.
- `StageQuasarAgentForPublish` (AfterTargets=Publish) — same verification and copy but to `$(PublishDir)Agent\`.

## Dependencies
- [`Magnetar.Protocol/Magnetar.Protocol.csproj`](../Magnetar.Protocol/Magnetar.Protocol.csproj.md)
- [`Quasar/package.json`](package.json.md)
- [`Quasar/package-lock.json`](package-lock.json.md)
- [`Quasar.Agent/Quasar.Agent.csproj`](../Quasar.Agent/Quasar.Agent.csproj.md) (built as a side-effect, not a `<ProjectReference>`)

## Notes
The grid viewer browser dependencies are restored through npm rather than copied by hand so the served `/vendor` tree matches the published packages and lock-file integrity hashes. `Quasar.Agent` is intentionally a build-time side-effect rather than a `<ProjectReference>` because the agent targets `netstandard2.0` (to load inside Space Engineers) while the host targets `net10.0`. The agent DLLs are staged into `Agent/` and deployed at runtime by `DedicatedServerRuntimePreparer`; Harmony is staged in runtime subfolders because Magnetar can launch either .NET 10 or .NET Framework 4.8 on Windows. The host project prefers NuGet's generated `$(PkgLib_Harmony)` property, but also falls back to standard NuGet package roots so clean CI `Restore;Publish` paths can still find the package after restore.
