# Quasar.Bootstrap/Quasar.Bootstrap.csproj

**Module:** Quasar.Bootstrap  **Kind:** project file  **Tier:** 3

## Summary
MSBuild project file for `Quasar.Bootstrap`, a `net10.0` console executable that targets `linux-x64` and `win-x64`. RID-targeted publish restores and publishes the `Quasar` worker as a single-file sub-app into a `WebService/` subfolder.

## Structure
**Key properties:**

| Property | Value |
|---|---|
| `OutputType` | Exe |
| `TargetFramework` | net10.0 |
| `RuntimeIdentifiers` | linux-x64;win-x64 |
| `Version` | inherited repo default, currently 1.0.0 |
| `ImplicitUsings` | enable |
| `Nullable` | enable |
| `SelfContained` (RID builds) | true |
| `PublishSingleFile` (RID builds) | true |
| `EnableCompressionInSingleFile` (RID builds) | true |

**Framework reference:** `Microsoft.AspNetCore.App`

**Project references:** `Magnetar.Protocol/Magnetar.Protocol.csproj`

**Custom build targets:**

| Target | Trigger | Action |
|---|---|---|
| `PublishQuasarWorker` | `BeforeTargets="Publish"` (RID) | Runs `dotnet restore` for `Quasar/Quasar.csproj`, then a fresh `dotnet publish --no-restore` as single-file into a temp obj dir, passing release metadata through to the worker |
| `PackPublishedQuasar` | `AfterTargets="Publish"` (RID) | Copies worker output to `WebService/` subfolder; renames `Quasar.Bootstrap[.exe]` → `Quasar[.exe]`; deletes `.pdb`/`.xml` files |

## Dependencies
- `Magnetar.Protocol` project
- [`Quasar/Quasar.csproj`](../Quasar/Quasar.csproj.md) (indirectly via `PublishQuasarWorker` MSBuild invocation)

## Notes
The two-binary layout (launcher as `Quasar` + worker under `WebService/Quasar`) is required so both processes can share the name without overwriting each other during hot-reload upgrades. The worker restore and publish deliberately run as separate `dotnet` processes so a clean CI checkout re-evaluates the worker project after NuGet and static-web-asset props have been generated.
