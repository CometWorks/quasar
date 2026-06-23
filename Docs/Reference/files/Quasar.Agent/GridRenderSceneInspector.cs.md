# Quasar.Agent/GridRenderSceneInspector.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
Game-thread helper that builds metadata-only grid viewer scene snapshots from live `MyCubeGrid` instances. It maps grid/block definitions and instances into `Magnetar.Protocol` viewer DTOs, registers logical model and texture names, includes generated cube-part and runtime subpart metadata, captures LCD material names, working state, `UseOnlineTexture`, built-in online/offline placeholder image paths, selected image paths, text settings, background colors, sprite metadata, and `MaterialNamesToHideWhenOffline` from standalone `MyTextPanel` blocks and `IMyMultiTextPanelComponentOwner` blocks such as cockpits/control seats, and intentionally avoids client-render-only dependencies such as `VRage.Render` texture-change payloads or offscreen LCD texture bytes.
