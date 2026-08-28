---
type: "query"
date: "2026-08-28T21:34:46.276983+00:00"
question: "Introduce a generic retry system for any requests/downloads that go to GitHub for updates after a proxy outage left Quasar unresponsive."
contributor: "graphify"
outcome: "useful"
source_nodes: ["QuasarUpdateService", "LauncherCoordinator", "GitHubRelease"]
---

# Q: Introduce a generic retry system for any requests/downloads that go to GitHub for updates after a proxy outage left Quasar unresponsive.

## Answer

Expanded from original query via graph vocab: [github, update, download, request, retry, release, asset, bootstrap, magnetar, manifest, network, proxy]. QuasarUpdateService and other worker-side GitHub callers use the default IHttpClientFactory client; LauncherCoordinator owns a separate Bootstrap download client. Added one GitHubRetryHandler shared by both paths, restricted to GitHub GET/HEAD traffic, retrying HttpRequestException, non-caller TaskCanceledException, HTTP 408/429/5xx four times with bounded exponential or Retry-After delays. Existing update monitors catch final failure and remain available.

## Outcome

- Signal: useful

## Source Nodes

- QuasarUpdateService
- LauncherCoordinator
- GitHubRelease