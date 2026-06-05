# Magnetar.Protocol/Model/PluginConfigData.cs

**Module:** Magnetar.Protocol  **Kind:** class  **Tier:** 1

## Summary
DTO carrying one plugin's editable configuration as part of a `PluginConfigSnapshot`. The `ConfigJson` field contains the full `ConfigStorage.SaveJson` envelope (`schema` + `defaults` + `values`) that Quasar uses to render a live configuration editor.

## Structure
Namespace: `Magnetar.Protocol.Model`

Class `PluginConfigData` (concrete, no base type):

| Property | Type | Description |
|---|---|---|
| `PluginId` | `string` | Matches `IQuasarConfigProvider.PluginId`. |
| `DisplayName` | `string` | Human-readable plugin name for the editor UI. |
| `ConfigJson` | `string` | Full `SaveJson` envelope JSON. |

## Dependencies
- [`Magnetar.Protocol/Model/PluginConfigSnapshot.cs`](PluginConfigSnapshot.cs.md) — listed in `Plugins`.
- [`Magnetar.Protocol/Bridge/IQuasarConfigProvider.cs`](../Bridge/IQuasarConfigProvider.cs.md) — source of the JSON via `GetConfigJson()`.
