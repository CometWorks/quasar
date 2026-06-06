# Quasar/Models/DedicatedServerProcessPriority.cs

**Module:** Quasar.Models  **Kind:** enum  **Tier:** 1

## Summary
Five-level OS process priority enum used to configure the Windows/Linux process priority of a managed DS instance at startup and after it has fully loaded. Maps to the standard `ProcessPriorityClass` levels.

## Structure
Namespace: `Quasar.Models`

| Value | Int | Equivalent |
|---|---|---|
| `Low` | 0 | `ProcessPriorityClass.Idle` |
| `BelowNormal` | 1 | `ProcessPriorityClass.BelowNormal` |
| `Normal` | 2 | `ProcessPriorityClass.Normal` (default at both startup and ready) |
| `AboveNormal` | 3 | `ProcessPriorityClass.AboveNormal` |
| `High` | 4 | `ProcessPriorityClass.High` |

## Dependencies
- [`Quasar/Models/DedicatedServerDefinition.cs`](DedicatedServerDefinition.cs.md) (properties `StartupProcessPriority`, `ReadyProcessPriority`)
