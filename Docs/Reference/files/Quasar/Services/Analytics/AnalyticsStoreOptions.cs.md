# Quasar/Services/Analytics/AnalyticsStoreOptions.cs

**Module:** Quasar.Services.Analytics  **Kind:** class  **Tier:** 2

## Summary

Holds analytics persistence policy shared by `MetricsStoreService` and `ServerMetricsStore`.
Default retention is 30 days. Accepted values are `30`, `45`, `60`, or `90` days via:

- environment variable `QUASAR_ANALYTICS_RETENTION_DAYS`
- `Quasar:AnalyticsRetentionDays`
- `Quasar:Analytics:RetentionDays`

## Structure

Namespace: `Quasar.Services.Analytics`

**`AnalyticsStoreOptions`** (sealed class)

- `RetentionDays` — allowed values: `30`, `45`, `60`, `90`; invalid values fall back to `30`
- `RawCapacity` — hardcoded raw ring size (`3600`)
- `OneMinuteCapacity` — `RetentionDays * 24 * 60`
- `OneHourCapacity` — `RetentionDays * 24`

## Helpers

- `Create(IConfiguration)` — resolves retention from config/env and applies fallback/defaults
- `IsAllowedRetentionDays(int)` — checks the fixed retention set
