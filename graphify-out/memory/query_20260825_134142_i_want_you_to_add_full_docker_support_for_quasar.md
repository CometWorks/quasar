---
type: "query"
date: "2026-08-25T13:41:42.950725+00:00"
question: "I want you to add full docker support for Quasar. This commit is a good starting point https://github.com/CometWorks/quasar/commit/62cbaf96cd36ad0b1405170564f0deb40b9d601b. We need to make sure env passthrough, initial admin ID setup and other relevant persistent settings can be controlled from the manifest. Make sure to add a GHCR image for quasar that is versioned in the same way as our full releases through the current CI/CD."
contributor: "graphify"
outcome: "useful"
source_nodes: ["Unified Release Workflow", "Program", "RbacConfigCatalog"]
---

# Q: I want you to add full docker support for Quasar. This commit is a good starting point https://github.com/CometWorks/quasar/commit/62cbaf96cd36ad0b1405170564f0deb40b9d601b. We need to make sure env passthrough, initial admin ID setup and other relevant persistent settings can be controlled from the manifest. Make sure to add a GHCR image for quasar that is versioned in the same way as our full releases through the current CI/CD.

## Answer

Expanded from original query via graph vocab: [config, admin, auth, persist, storage, environment, variables, release, workflow, publish, version, update]. Unified Release Workflow centralizes full-release version identity; Program loads environment configuration; RbacConfigCatalog owns persistent administrator mappings. Docker now consumes the checksummed Linux release artifact, Compose persists /data and passes .env, QUASAR_ADMIN_STEAM_ID seeds missing rbac.json, and the full-release workflow publishes matching GHCR version/latest tags.

## Outcome

- Signal: useful

## Source Nodes

- Unified Release Workflow
- Program
- RbacConfigCatalog