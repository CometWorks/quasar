#!/usr/bin/env python3
"""Extract cross-reference edges and per-module summaries from the description files.

Outputs:
  data/reference_graph.json  - {file: [referenced source paths]}
  data/module_index.json     - {module: [{path, name, kind, summary}]}
These feed module-description synthesis, the TOC, and Index.md.
"""
import json
import os
import re

HERE = os.path.dirname(__file__)
REPO = os.path.abspath(os.path.join(HERE, "..", "..", ".."))
MANIFEST = os.path.join(HERE, "manifest.json")
FILESDIR = os.path.join(REPO, "Docs", "Reference", "files")

files = [r for r in json.load(open(MANIFEST))["files"]
         if r["status"] in ("pending", "documented")]
valid_paths = {r["path"] for r in files}

# Match source paths like Quasar/Services/Foo.cs or Magnetar.Protocol/Model/Bar.cs
PATH_RE = re.compile(r"(?:Quasar(?:\.Agent|\.Bootstrap)?|Magnetar\.Protocol)/[\w./-]+\.\w+")

def parse_desc(path):
    full = os.path.join(FILESDIR, path + ".md")
    text = open(full, encoding="utf-8").read()
    # header line: **Module:** X  **Kind:** Y  **Tier:** Z
    mod = kind = ""
    m = re.search(r"\*\*Module:\*\*\s*([^\s*]+)", text)
    if m: mod = m.group(1)
    m = re.search(r"\*\*Kind:\*\*\s*([^*]+?)\s*\*\*Tier", text)
    if m: kind = m.group(1).strip()
    # Summary section
    summary = ""
    m = re.search(r"##\s*Summary\s*\n(.+?)(?:\n##\s|\Z)", text, re.S)
    if m:
        summary = " ".join(m.group(1).split())
    # Dependencies section -> edges
    deps = set()
    m = re.search(r"##\s*Dependencies\s*\n(.+?)(?:\n##\s|\Z)", text, re.S)
    if m:
        for cand in PATH_RE.findall(m.group(1)):
            if cand in valid_paths and cand != path:
                deps.add(cand)
    return mod, kind, summary, sorted(deps)

graph = {}
modidx = {}
for r in files:
    mod, kind, summary, deps = parse_desc(r["path"])
    graph[r["path"]] = deps
    modidx.setdefault(r["module"], []).append({
        "path": r["path"],
        "name": r["name"],
        "kind": kind,
        "tier": r["tier"],
        "summary": summary,
    })

for m in modidx:
    modidx[m].sort(key=lambda x: x["path"])

json.dump(graph, open(os.path.join(HERE, "reference_graph.json"), "w"), indent=2)
json.dump(modidx, open(os.path.join(HERE, "module_index.json"), "w"), indent=2)

# Report
edges = sum(len(v) for v in graph.values())
print(f"Files: {len(graph)}  Edges: {edges}")
nosum = [p for p, v in graph.items() if not parse_desc(p)[2]]
print(f"Files missing a summary: {len(nosum)} {nosum[:10]}")
print("Modules:", {m: len(v) for m, v in sorted(modidx.items())})
EOF_GUARD = None
