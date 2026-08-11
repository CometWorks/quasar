---
type: "query"
date: "2026-08-11T09:06:12.863775+00:00"
question: "I want you to do a pass on RBAC permissions and controls. We got a report yesterday that people with only viewing access were able to change their own permissions and then modify unwanted things."
contributor: "graphify"
outcome: "useful"
source_nodes: ["RbacConfigCatalog", "Program", "Security.razor", "QuasarRoleMapper"]
---

# Q: I want you to do a pass on RBAC permissions and controls. We got a report yesterday that people with only viewing access were able to change their own permissions and then modify unwanted things.

## Answer

Expanded from original query via graph vocab: [rbac, permission, role, access, auth, policy, claim, identity, admin, view, write]. Audit found global CanView was the only guard on several mutating Blazor pages, Steam cookie and Blazor circuit roles remained stale after RBAC changes, sensitive UI handlers lacked execution-time authorization, and same-subnet trusted-network access defaulted to admin. Fixed with policy-guarded routes/navigation, live role refresh, fresh execution-time checks, circuit reload on RBAC changes, last-admin save protection, and same-subnet bypass disabled by default.

## Outcome

- Signal: useful

## Source Nodes

- RbacConfigCatalog
- Program
- Security.razor
- QuasarRoleMapper