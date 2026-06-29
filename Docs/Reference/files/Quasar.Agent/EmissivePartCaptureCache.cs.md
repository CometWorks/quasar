# Quasar.Agent/EmissivePartCaptureCache.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
Thread-safe in-agent cache for status-emissive material states captured from Space Engineers named emissive updates. It stores the latest material name, color, emissivity, source, and timestamp per entity/material key, maps render-object IDs back to exported entity IDs during scene capture, filters decorative `EmissiveColorable` updates, and exposes parent or subpart entity snapshots for `GridRenderSceneInspector` to serialize into viewer DTOs.
