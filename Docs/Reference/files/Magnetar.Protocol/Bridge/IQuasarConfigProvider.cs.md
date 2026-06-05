# Magnetar.Protocol/Bridge/IQuasarConfigProvider.cs

**Module:** Magnetar.Protocol  **Kind:** interface  **Tier:** 1

## Summary
Defines the contract a Space Engineers plugin must implement to expose its configuration for remote editing via Quasar. The interface deliberately exchanges raw JSON strings so that `Magnetar.Protocol` has no compile-time dependency on `Magnetar.PluginSdk`; all serialization is delegated to the implementing plugin.

## Structure
Namespace: `Magnetar.Protocol.Bridge`

Interface `IQuasarConfigProvider` (not sealed, no base type):

| Member | Description |
|---|---|
| `string PluginId { get; }` | Stable, unique identifier used to route update requests to the correct provider within a DS process. |
| `string GetConfigJson()` | Returns the full `ConfigStorage.SaveJson` envelope (`schema` + `defaults` + `values`); Quasar renders its editor from this JSON. |
| `void ApplyConfigJson(string json)` | Accepts an updated values document (full envelope or flat object) and applies it live; implementations typically delegate to `ConfigStorage.LoadJson`. |

## Dependencies
None (no intra-project or external references).

## Notes
The JSON-string boundary is intentional: it decouples `Magnetar.Protocol` from the `PluginSdk` assembly, allowing the protocol library to target `netstandard2.0` without pulling in SE/plugin dependencies.
