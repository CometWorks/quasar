#!/usr/bin/env python3
"""Split the documentable files into balanced groups for parallel sub-agents.

Also writes the path-mapping (source path -> description file path) to data/.
"""
import hashlib
import json
import os

HERE = os.path.dirname(__file__)
REPO = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
MANIFEST = os.path.join(HERE, "manifest.json")
GROUPS_OUT = os.path.join(HERE, "groups.json")
PATHMAP_OUT = os.path.join(HERE, "path_map.json")

with open(MANIFEST) as f:
    files = [r for r in json.load(f)["files"] if r["status"] == "pending"]

by_mod = {}
for r in files:
    by_mod.setdefault(r["module"], []).append(r["path"])
for m in by_mod:
    by_mod[m].sort()

def split(lst, n):
    k, m = divmod(len(lst), n)
    return [lst[i*k+min(i, m):(i+1)*k+min(i+1, m)] for i in range(n)]

groups = {}
groups["g01-protocol"] = by_mod["Magnetar.Protocol"]
groups["g02-agent-bootstrap"] = by_mod["Quasar.Agent"] + by_mod["Quasar.Bootstrap"]
groups["g03-models-host"] = by_mod["Quasar.Models"] + by_mod["Quasar.Host"]
core = by_mod["Quasar.Services.Core"]
core_a, core_b = split(core, 2)
groups["g04-services-core-a"] = core_a
groups["g05-services-core-b"] = core_b
groups["g06-analytics-pluginsdk"] = by_mod["Quasar.Services.Analytics"] + by_mod["Quasar.Services.PluginSdk"]
groups["g07-auth-discord"] = by_mod["Quasar.Services.Auth"] + by_mod["Quasar.Services.Discord"]
comp = by_mod["Quasar.Components"]
pages = sorted([p for p in comp if "/Pages/" in p])
other = sorted([p for p in comp if "/Pages/" not in p])
pages_a, pages_b = split(pages, 2)
groups["g08-components-pages-a"] = pages_a
groups["g09-components-pages-b"] = pages_b
groups["g10-components-shared"] = other

with open(GROUPS_OUT, "w") as f:
    json.dump(groups, f, indent=2)

# Path map: source -> description file (mirrored under Docs/Reference/files/).
# Output paths must be unique on case-insensitive filesystems (Windows/macOS):
# a Linux source tree may hold siblings differing only in casing, which would
# collide on checkout. Detect such collisions deterministically and disambiguate
# by appending a short hash of the source path, recording the result here so the
# chosen names stay stable across runs.
pathmap = {}
seen = {}  # lowercased output path -> source path that claimed it
for r in sorted(files, key=lambda r: r["path"]):
    p = r["path"]
    out = f"Docs/Reference/files/{p}.md"
    if out.lower() in seen:
        suffix = hashlib.sha256(p.encode("utf-8")).hexdigest()[:8]
        disambiguated = f"Docs/Reference/files/{p}.{suffix}.md"
        print(f"WARNING: case-insensitive path collision for {p!r} "
              f"(conflicts with {seen[out.lower()]!r}); disambiguated to {disambiguated}")
        out = disambiguated
    seen[out.lower()] = p
    pathmap[p] = out
with open(PATHMAP_OUT, "w") as f:
    json.dump(pathmap, f, indent=2)

total = 0
for g, items in groups.items():
    print(f"{g:28s} {len(items)}")
    total += len(items)
print(f"TOTAL {total}")
