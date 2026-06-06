# Quasar/Services/Analytics/AnalyticsStoreOptions.cs

**Module:** Quasar.Services.Analytics  **Kind:** class  **Tier:** 2

## Summary

Configuration object holding the analytics persistence policy shared by `MetricsStoreService` and `ServerMetricsStore`. It resolves a retention window (in days) from environment/appsettings, constrains it to a fixed allowed set, and derives the rollup buffer capacities from it.

## Structure

Namespace: `Quasar.Services.Analytics`

**`AnalyticsStoreOptions`** (sealed class)

Constants/static:
- `DefaultRetentionDays = 30`
- `AllowedRetentionDays = [30, 45, 60, 90]` (private)

Members:
- `RetentionDays : int { get; init; }` — retention window; default 30, restricted to the allowed set
- `RawCapacity => 3600` — fixed raw ring size
- `OneMinuteCapacity => RetentionDays * 24 * 60`
- `OneHourCapacity => RetentionDays * 24`

Static helpers:
- `Create(IConfiguration) : AnalyticsStoreOptions` — resolves retention in precedence order: env var `QUASAR_ANALYTICS_RETENTION_DAYS`, then `Quasar:AnalyticsRetentionDays`, then `Quasar:Analytics:RetentionDays`; falls back to `DefaultRetentionDays` when unset, unparseable, or not allowed
- `IsAllowedRetentionDays(int) : bool` — membership check against the allowed set

## Dependencies

- External: `Microsoft.Extensions.Configuration` (`IConfiguration`)

## Notes

- Immutable after construction (`init`-only `RetentionDays`); intended to be built once via `Create` at startup and injected into `MetricsStoreService`/`ServerMetricsStore`.
- `RawCapacity` is intentionally retention-independent (raw buffer is the fixed 1-hour per-second ring); only the minute/hour rollup capacities scale with retention.
