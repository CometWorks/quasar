---
type: "architecture"
date: "2026-08-08T15:57:45.651352+00:00"
question: "Where should Quasar headless API mode split from UI startup?"
contributor: "graphify"
outcome: "useful"
source_nodes: ["Quasar/Program.cs", "Quasar.Bootstrap/Program.cs", "Quasar/Services/WebServiceOptions.cs"]
---

# Q: Where should Quasar headless API mode split from UI startup?

## Answer

Keep one API/supervisor worker. Parse --headless into Quasar:Headless before WebApplication builder configuration; skip source static assets, Razor/Mud/UI-plugin registration, antiforgery/UI error pages, branding/static/plugin/Razor endpoint mapping, and browser launch. Keep catalogs, supervisors, hosted jobs, HTTP APIs, /ws/agent, /api/health, and distinct /api/ready. Bootstrap propagates QUASAR_HEADLESS to every worker and replacement launcher.

## Outcome

- Signal: useful

## Source Nodes

- Quasar/Program.cs
- Quasar.Bootstrap/Program.cs
- Quasar/Services/WebServiceOptions.cs