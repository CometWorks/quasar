# Quasar.Agent/Quasar.Agent.csproj

**Module:** Quasar.Agent  **Kind:** project file  **Tier:** 3

## Summary
MSBuild project file for `Quasar.Agent`, a `netstandard2.0` class library (x64-only) that produces `Quasar.Agent.dll` — the Magnetar/Space Engineers plugin assembly. All game and PluginSdk references are `Private="False"` (provided by the host at runtime), including `Sandbox.Common` for game interfaces used by the grid viewer LCD surface inspection path. Harmony is a package dependency because the agent applies profiler patches in-process.

## Structure
Key settings:
- `OutputType`: Library
- `TargetFramework`: netstandard2.0
- `Platforms` / `PlatformTarget`: x64
- `Nullable`: disable; `LangVersion`: latest

**Reference groups:**

| Reference | Source | Private |
|---|---|---|
| `Sandbox.Game` | `$(DS64)\Sandbox.Game.dll` | False |
| `Sandbox.Common` | `$(DS64)\Sandbox.Common.dll` | False |
| `SpaceEngineersDedicated` | `$(DS64)\SpaceEngineersDedicated.exe` | False |
| `SpaceEngineers.Game` | `$(DS64)\SpaceEngineers.Game.dll` | False |
| `VRage` / `VRage.Dedicated` / `VRage.Game` / `VRage.Library` / `VRage.Math` | `$(DS64)\*.dll` | False |
| `PluginSdk` | `$(MagnetarBin)\PluginSdk.dll` | False |

**Package references:** `Newtonsoft.Json 13.0.3`, `Lib.Harmony 2.4.2`

**Project references:** `Magnetar.Protocol/Magnetar.Protocol.csproj`

## Dependencies
- `Magnetar.Protocol` project
- `Lib.Harmony` package, staged by the host for the selected Magnetar runtime
- SE dedicated server assemblies resolved via `$(DS64)` MSBuild property
- Magnetar PluginSdk resolved via `$(MagnetarBin)` MSBuild property
