# Quasar/Services/Analytics/MetricSample.cs

**Module:** Quasar.Services.Analytics  **Kind:** struct  **Tier:** 2

## Summary

Immutable value type representing one point-in-time snapshot of a dedicated server's performance counters. Used as the storage unit inside `RrdCircularBuffer` and `RrdRollupBuffer`. All fields are readonly to keep the struct safe for `in` parameter passing.

## Structure

Namespace: `Quasar.Services.Analytics`

**`MetricSample`** (`readonly struct`)

Fields (all `public readonly`):
| Field | Type | Description |
|---|---|---|
| `TimestampUnixSeconds` | `long` | Unix epoch time of the sample |
| `SimSpeed` | `float` | Simulation speed (1.0 = real-time) |
| `CpuPercent` | `float` | Server CPU load % |
| `MemoryMb` | `float` | Working-set memory in MB |
| `FrameTimeMs` | `float` | Frame duration derived from sim speed |
| `PlayersOnline` | `int` | Player count at sample time |
| `UsedPcu` | `int` | PCU consumed |
| `ActiveGridCount` | `int` | Active grids (-1 = unavailable) |
| `ActiveEntityCount` | `int` | Active entities (-1 = unavailable) |

Single all-fields constructor; no methods.

## Dependencies

None (no external types referenced).
