# Quasar/Models/DedicatedServerGoalState.cs

**Module:** Quasar.Models  **Kind:** enum  **Tier:** 3

## Summary
Two-value enum expressing the operator's intent for a managed dedicated server: `Off` (should not be running) or `On` (should be running). The supervisor reconciles actual process state against this goal.

## Structure
Namespace: `Quasar.Models`  
`public enum DedicatedServerGoalState`

| Value | Int | Meaning |
|---|---|---|
| `Off` | 0 | Server should be stopped. |
| `On` | 1 | Server should be started and kept running. |

## Dependencies
- [`Quasar/Models/DedicatedServerDefinition.cs`](DedicatedServerDefinition.cs.md) (field `GoalState`)
- [`Quasar/Models/DedicatedServerRuntimeSnapshot.cs`](DedicatedServerRuntimeSnapshot.cs.md) (field `GoalState`)
