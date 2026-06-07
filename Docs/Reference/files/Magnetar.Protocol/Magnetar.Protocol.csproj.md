# Magnetar.Protocol/Magnetar.Protocol.csproj

**Module:** Magnetar.Protocol  **Kind:** project file  **Tier:** 3

## Summary
MSBuild project file for the `Magnetar.Protocol` shared contract library. Targets `netstandard2.0` so the assembly can be loaded by both the Quasar Blazor Server supervisor (net8+) and the in-DS `Quasar.Agent` plugin (which runs inside the Space Engineers process).

## Structure
- `TargetFramework`: `netstandard2.0`
- `Nullable`: `enable`
- `LangVersion`: `latest`
- `Version`, `AssemblyVersion`, `FileVersion`: `0.1.0` defaults, overridden by release packaging when needed
- No NuGet package references — the library is deliberately dependency-free.
- No project references — standalone contract assembly.

## Dependencies
None (no external packages, no project references).

## Notes
Keeping the project free of third-party dependencies is a deliberate design constraint: any consumer (DS plugin or supervisor) can reference this assembly without conflict.
