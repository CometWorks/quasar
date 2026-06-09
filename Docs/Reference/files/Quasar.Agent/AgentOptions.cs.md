# Quasar.Agent/AgentOptions.cs

**Module:** Quasar.Agent  **Kind:** class  **Tier:** 1

## Summary
`AgentOptions` is a sealed configuration DTO holding connection-resilience parameters and profiler mode for the agent's WebSocket link to Quasar. Values are read from environment variables set by Quasar when it launches a managed server (or by an operator for standalone servers), with sensible defaults applied when variables are absent or invalid.

## Structure
**Namespace:** `Quasar.Agent`  
**Modifiers:** public, sealed

| Member | Default | Env variable | Description |
|---|---|---|---|
| `OfflineShutdownSeconds` | 3600 | `QUASAR_AGENT_OFFLINE_SHUTDOWN_SECONDS` | How long (s) after losing Quasar before the server saves and stops; ≤0 means stop promptly; only arms after at least one successful connection |
| `ReconnectIntervalSeconds` | 10 | `QUASAR_AGENT_RECONNECT_INTERVAL_SECONDS` | Base delay between reconnect attempts (clamped to ≥1) |
| `ReconnectJitterSeconds` | 3 | `QUASAR_AGENT_RECONNECT_JITTER_SECONDS` | Random ±jitter added per reconnect delay (clamped to ≥0) |
| `ProfilerMode` | `SafeContinuous` | `QUASAR_AGENT_PROFILER_MODE` | Profiler patch depth: low-overhead high-level `SafeContinuous`, call-site `DeepContinuous`, or `Off` |
| `FromEnvironment()` (static) | — | — | Factory method; reads env vars, applies clamping, returns new instance |
| `TryParseProfilerMode(string, out AgentProfilerMode)` (static) | — | — | Parses canonical profiler mode values and aliases for live commands and environment reads |

## Dependencies
None (standard library only).

## Notes
`OfflineShutdownSeconds` accepts zero and negative values as meaningful ("stop promptly"), so its floor is not clamped; only `ReconnectIntervalSeconds` and `ReconnectJitterSeconds` are clamped. Profiler mode parsing also accepts aliases such as `deep`, `callsite`, `safe`, `method`, `off`, and `disabled`.
