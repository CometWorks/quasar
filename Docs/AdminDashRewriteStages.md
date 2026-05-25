# Admin Dash Rewrite Stages

This file remains as a short tracker only.

The implementation reference now lives in:

- [QuasarArchitecture.md](./QuasarArchitecture.md)

## Current status

Complete:

- shared `Magnetar.Protocol` contracts
- renamed on-disk supervisor host project to `Quasar`
- renamed on-disk DS plugin project to `Quasar.Agent`
- basic discovery manifest, health endpoint, and agent attach flow
- NLog-based supervisor logging with minimal console behavior
- `Quasar.Bootstrap` ensure-running helper
- per-instance JSON-backed instance definitions with atomic history groundwork
- first goal-state reconciliation and DS process supervision
- first health-monitoring and auto-recovery pass in the supervisor
- initial runtime launch preparation with isolated app-data roots, runtime config sync, `LastSession.sbl`, and enforced headless launch shaping
- neutral MudBlazor light/dark theming with local-storage persistence

Pending:

- file watching/reload for manual config edits
- config editing migration from Python `webui/`
- Quasar self-update staging and seamless cutover
- cleanup of stale legacy docs and obsolete `webui/`

## Final naming

- `Quasar` = supervisor
- `Quasar.Bootstrap` = ensure-running / setup helper
- `Quasar.Agent` = DS plugin
- `Magnetar.Protocol` = shared transport contracts
