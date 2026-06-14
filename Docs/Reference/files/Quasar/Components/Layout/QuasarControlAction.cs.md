# Quasar/Components/Layout/QuasarControlAction.cs

**Module:** Quasar.Components  **Kind:** enum  **Tier:** 3

## Summary
Enum returned by `QuasarControlDialog` to tell `MainLayout` which confirmed power action the operator selected: restart Quasar, shut down Quasar while keeping servers running, or shut down all servers while Quasar stays online.

## Structure
Namespace: `Quasar.Components.Layout`

| Value | Meaning |
|---|---|
| `RestartQuasar` | Restart the Quasar worker; managed servers continue running and are re-adopted. |
| `ShutdownQuasar` | Stop the Quasar web UI/supervisor while leaving managed servers detached and running. |
| `ShutdownAllServers` | Gracefully stop all running managed servers; Quasar remains online. |

## Dependencies
None.
