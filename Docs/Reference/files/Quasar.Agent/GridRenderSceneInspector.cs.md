# Quasar.Agent/GridRenderSceneInspector.cs

Game-thread helper that builds metadata-only grid viewer scene snapshots from live `MyCubeGrid` instances. It maps grid/block definitions and instances into `Magnetar.Protocol` viewer DTOs, registers logical model and texture names, includes generated cube-part and runtime subpart metadata, captures LCD text-surface material names, selected image paths, text settings, and sprite metadata, and intentionally avoids client-render-only dependencies such as `VRage.Render` texture-change payloads or offscreen LCD texture bytes.
