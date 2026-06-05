#!/usr/bin/env python3
"""Propagate the naming-sweep file/type renames into description files that were
NOT re-generated (the unchanged ones still referencing old names in cross-links/prose).

Replacements are CamelCase type/file basenames specific enough not to hit prose.
Order longest-first. Idempotent (no-op on already-updated files).
"""
import os

RENAMES = [
    # longest first
    ("DedicatedServerInstanceRuntimeSnapshot", "DedicatedServerRuntimeSnapshot"),
    ("DedicatedServerInstanceDefinition", "DedicatedServerDefinition"),
    ("DedicatedServerInstanceHealthState", "DedicatedServerHealthState"),
    ("DedicatedServerInstanceProcessState", "DedicatedServerProcessState"),
    ("DedicatedServerInstanceGoalState", "DedicatedServerGoalState"),
    ("DedicatedServerInstanceCatalog", "DedicatedServerCatalog"),
    ("InstanceMetricsStore", "ServerMetricsStore"),
    ("InstanceConsoleDialog", "ServerConsoleDialog"),
    ("InstanceEditorDialog", "ServerEditorDialog"),
    ("Pages/Nodes.razor", "Pages/Hosts.razor"),
    ("Nodes.razor.md", "Hosts.razor.md"),
]

HERE = os.path.dirname(__file__)
ROOT = os.path.abspath(os.path.join(HERE, ".."))

changed = 0
for base, _, fs in os.walk(ROOT):
    if os.path.basename(base) == "data":
        continue
    for f in fs:
        if not f.endswith(".md"):
            continue
        p = os.path.join(base, f)
        text = open(p, encoding="utf-8").read()
        new = text
        for old, rep in RENAMES:
            new = new.replace(old, rep)
        if new != text:
            open(p, "w", encoding="utf-8").write(new)
            changed += 1
print(f"Updated {changed} files with renamed references.")
