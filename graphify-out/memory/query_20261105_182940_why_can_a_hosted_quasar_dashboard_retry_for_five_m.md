---
type: "query"
date: "2026-11-05T18:29:40.665175+00:00"
question: "Why can a hosted Quasar dashboard retry for five minutes after an update until manual refresh?"
contributor: "graphify"
outcome: "useful"
source_nodes: ["ReconnectModal.razor.js", "Blazor Circuit Reconnection"]
---

# Q: Why can a hosted Quasar dashboard retry for five minutes after an update until manual refresh?

## Answer

Expanded graph terms: reconnect reload circuit blazor readiness hosted. The graph located ReconnectModal.razor.js and Blazor Circuit Reconnection. Live-source tracing confirmed native update submission does not start the planned restart reload watcher, while ordinary reconnection only reloads rejected circuits or manual failed-resume actions. A replacement worker can therefore remain behind long transport retries. The fix records the initial worker ID, polls path-relative readiness and protected version metadata while disconnected, and reloads only for a different ready worker. Normal reconnection and planned restart ownership cancel stale probes.

## Outcome

- Signal: useful

## Source Nodes

- ReconnectModal.razor.js
- Blazor Circuit Reconnection