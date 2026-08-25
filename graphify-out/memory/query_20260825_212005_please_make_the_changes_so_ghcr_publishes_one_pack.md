---
type: "query"
date: "2026-08-25T21:20:05.121140+00:00"
question: "Please make the changes so GHCR publishes one package version instead of a tagged index plus two untagged manifests."
contributor: "graphify"
outcome: "useful"
source_nodes: ["Unified Release Workflow"]
---

# Q: Please make the changes so GHCR publishes one package version instead of a tagged index plus two untagged manifests.

## Answer

Expanded from graph vocabulary: [container, release, workflow, publish, build, platform, manifest, package, version]. Unified Release Workflow now sets provenance: false on docker/build-push-action. With one linux/amd64 target, Buildx will publish the runnable image manifest directly instead of a tagged OCI index containing both the image and a SLSA attestation. Documentation records why provenance is disabled for GHCR package-version clarity.

## Outcome

- Signal: useful

## Source Nodes

- Unified Release Workflow