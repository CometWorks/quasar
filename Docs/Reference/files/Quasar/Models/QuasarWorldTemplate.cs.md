# Quasar/Models/QuasarWorldTemplate.cs

**Module:** Quasar.Models  **Kind:** class  **Tier:** 1

## Summary
Minimal DTO representing a world template entry in the Quasar catalog. A world template is a named reference (with optional description) to a pre-configured world that can be assigned to one or more server instances via `WorldTemplateId`.

## Structure
Namespace: `Quasar.Models`

**`QuasarWorldTemplate`** — sealed class

| Property | Description |
|---|---|
| `WorldTemplateId` | Auto-generated GUID string; catalog key |
| `Name` | Display name of the template |
| `Description` | Optional free-text description |
| `UpdatedAtUtc` | Last modification timestamp |

No methods beyond property accessors.

## Dependencies
- [`Quasar/Services/QuasarWorldTemplateCatalog.cs`](../Services/QuasarWorldTemplateCatalog.cs.md) (owns and persists these records)
- [`Quasar/Models/DedicatedServerDefinition.cs`](DedicatedServerDefinition.cs.md) (references `WorldTemplateId`)
