---
type: "query"
date: "2026-08-25T20:59:04.442139+00:00"
question: "Also, lets make sure that PRs in any form do not trigger the docker publish pipeline. Only a push on main."
contributor: "graphify"
outcome: "useful"
source_nodes: ["Unified Release Workflow"]
---

# Q: Also, lets make sure that PRs in any form do not trigger the docker publish pipeline. Only a push on main.

## Answer

Expanded from graph vocabulary: [container, release, workflow, publish, build, platform, manifest, version]. Unified Release Workflow now hard-gates publish-container on github.event_name == push and github.ref == refs/heads/main, while retaining full-release metadata checks. Pull request events of every action, tag pushes, and workflow_dispatch runs skip the entire container job before checkout, Buildx setup, GHCR login, build, or push.

## Outcome

- Signal: useful

## Source Nodes

- Unified Release Workflow