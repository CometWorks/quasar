# Quasar/Services/ManagedRuntimeWarmupService.cs

**Module:** Quasar.Services.Core  **Kind:** class  **Tier:** 1

## Summary

`ManagedRuntimeWarmupService` is a `BackgroundService` that immediately checks and prepares the managed SteamCMD and Space Engineers Dedicated Server installs at Quasar startup, so managed Magnetar launches are blocked until those prerequisites are ready. After the startup warmup, it checks the managed Magnetar install for updates every 15 minutes without changing the visible SteamCMD/Dedicated Server readiness snapshot. It exposes a component-level `ManagedRuntimeWarmupSnapshot` for dashboard progress display and fires a `Changed` event on every status update.

## Structure

Namespace: `Quasar.Services`

**`ManagedRuntimeWarmupService`** — sealed class extending `BackgroundService`.

| Member | Description |
|---|---|
| `event Action? Changed` | Raised on state transitions. |
| `GetSnapshot()` | Returns a copy of the current `ManagedRuntimeWarmupSnapshot`. |
| `bool IsReady` | True only after SteamCMD and Dedicated Server readiness completes. Used by `DedicatedServerSupervisor` to gate managed server launches. |
| `BlockLaunchMessage` | User-facing reason shown when a launch is requested before managed runtime readiness. |
| `RetryAsync(ct)` | Reruns the same readiness workflow for dashboard retry after a failed Dedicated Server download. A semaphore prevents concurrent warmups. |
| `ExecuteAsync(ct)` | Transitions `Pending → Running`, calls `_runtimeResolver.EnsureManagedRuntimeReadyAsync` with a progress reporter, then transitions to `Complete` or `Failed`; after that, runs a 15-minute `PeriodicTimer` for managed Magnetar update checks. |
| `RunMagnetarUpdateCheckAsync(ct)` | Uses the same semaphore as warmup/retry, calls `_runtimeResolver.EnsureManagedMagnetarCurrentAsync`, and logs update-check failures without faulting the readiness snapshot. |
| `ApplyProgress(...)` | Maps resolver progress events into per-component snapshot rows and raises `Changed` for live UI refresh. |

**`ManagedRuntimeWarmupState`** — enum `{Pending, Running, Complete, Failed}`.

**`ManagedRuntimeWarmupSnapshot`** — sealed record `{State, Message, Components, UpdatedAtUtc}`. `CreateInitial()` seeds SteamCMD and Dedicated Server rows; `Copy()` returns a detached component list; `WithComponent` / `WithComponents` update immutable row data.

**`ManagedRuntimeComponentState`** — enum `{Pending, Checking, Downloading, Installing, Ready, Failed}`.

**`ManagedRuntimeComponentSnapshot`** — sealed record carrying component id, display name, state, message, optional percent, and path.

## Dependencies

- [`Quasar/Services/ManagedDedicatedServerRuntimeResolver.cs`](ManagedDedicatedServerRuntimeResolver.cs.md) — `EnsureManagedRuntimeReadyAsync`, `EnsureManagedMagnetarCurrentAsync`, progress DTOs
