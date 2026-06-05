# Quasar/Services/ManagedRuntimeWarmupService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`ManagedRuntimeWarmupService` is a `BackgroundService` that proactively resolves the managed runtime (Magnetar + DS) shortly after Quasar starts, so the first real server launch does not block on slow downloads. It exposes a `ManagedRuntimeWarmupSnapshot` for UI status display and fires a `Changed` event on transitions.

## Structure

Namespace: `Quasar.Services`

**`ManagedRuntimeWarmupService`** — sealed class extending `BackgroundService`.

| Member | Description |
|---|---|
| `event Action? Changed` | Raised on state transitions. |
| `GetSnapshot()` | Returns a copy of the current `ManagedRuntimeWarmupSnapshot`. |
| `ExecuteAsync(ct)` | Waits 2 s, calls `_runtimeResolver.ResolveAsync` with a dummy `DedicatedServerDefinition{UniqueName="warmup"}`, transitions `Pending → Running → Complete/Failed`. |

**`ManagedRuntimeWarmupState`** — enum `{Pending, Running, Complete, Failed}`.

**`ManagedRuntimeWarmupSnapshot`** — sealed record `{State, Message, UpdatedAtUtc}`.

## Dependencies

- [`Quasar/Services/ManagedDedicatedServerRuntimeResolver.cs`](ManagedDedicatedServerRuntimeResolver.cs.md) — `ResolveAsync`
- [`Quasar/Models/DedicatedServerDefinition.cs`](../Models/DedicatedServerDefinition.cs.md) — dummy definition for warmup call
