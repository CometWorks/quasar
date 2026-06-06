# Quasar/Models/CpuAffinitySpec.cs

**Module:** Quasar.Models  **Kind:** class (static)  **Tier:** 2

## Summary
Parsing, validation, and formatting helpers for the per-server CPU affinity setting. Affinity is stored as a cpuset string in `taskset` syntax such as `"0-7"` or `"0-7,16-23"`. An empty string means no affinity (all cores); a non-empty value must resolve to at least `MinimumCores` distinct in-range logical cores.

## Structure
Namespace: `Quasar.Models`
`public static class CpuAffinitySpec`

| Member | Description |
|---|---|
| `MinimumCores` | `const int = 2`. Minimum distinct cores a non-empty spec must resolve to. |
| `TryParse(string? text, int processorCount, out IReadOnlyList<int> cores, out string? error)` | Parses a cpuset string into sorted distinct indices in `[0, processorCount)`. Empty input is valid → empty list. Accepts `"a-b"` ranges; rejects start>end, out-of-range, and fewer than `MinimumCores`. |
| `Format(IEnumerable<int> cores)` | Canonical compact form collapsing runs into ranges (`{0,1,2,3,8}` → `"0-3,8"`); empty → `""`. |
| `ToWindowsMask(IEnumerable<int> cores)` | `long` bitmask for `Process.ProcessorAffinity`; cores `>= 64` are ignored. |

## Dependencies
- External: `System.Globalization`, `System.Text`.
- Consumed by `Quasar/Services/DedicatedServerSupervisor.cs`, `Quasar/Services/DedicatedServerCatalog.cs`, [`DedicatedServerDefinition.cs`](DedicatedServerDefinition.cs.md) (`CpuAffinity` field), `Quasar/Components/Pages/ServerEditorDialog.razor`.

## Notes
Windows `Process.ProcessorAffinity` only addresses logical processors 0-63 within a single processor group, so `ToWindowsMask` cannot target more than 64 logical CPUs on Windows. On Linux the supervisor applies affinity via `taskset` (see `DedicatedServerSupervisor`).
