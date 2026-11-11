---
type: "query"
date: "2026-11-10T16:37:15.065789+00:00"
question: "Investigate the local Nebula connection to dash.se-servers.net/owen"
contributor: "graphify"
outcome: "dead_end"
---

# Q: Investigate the local Nebula connection to dash.se-servers.net/owen

## Answer

Expanded via graph vocabulary: hosted panel state installation. Existing graph lacks the current enrollment and connector implementation and was not sufficient for diagnosis. Direct runtime checks found live HostedPanel/enrollment.json replaced with AccessSetupDialogTests fixture data (test-panel, existing, dummy credential, Choice dismissed), last modified 2026-11-10 17:26 Europe/Amsterdam. Local readiness returns 200; connector absent; remote frps reports no route for /owen/. Original credential retained in frpc.toml authenticates via private Nebula manager read endpoint and panel remains active. No configuration or identity was changed.

## Outcome

- Signal: dead_end