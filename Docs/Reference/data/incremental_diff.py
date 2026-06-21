#!/usr/bin/env python3
"""Incremental change detection against the previous manifest.

Uses the SHA-256 hash as the cache key. The hash is platform-independent (text files
are newline-normalized before hashing in build_manifest.py), so a line-ending-only
difference between a Windows and a Linux checkout is treated as UNCHANGED:
  - path present before & after with same hash -> UNCHANGED (reuse description)
  - path present before & after with different hash -> CHANGED (re-describe)
  - path only after -> NEW (describe; usually a rename target)
  - path only before -> REMOVED (delete its stale description file)

Run AFTER build_manifest.py has regenerated manifest.json. Expects the prior
manifest at manifest.prev.json. Carries forward status='documented' for unchanged
files, sets 'pending' for changed/new, and deletes stale description files.
"""
import json
import os

HERE = os.path.dirname(__file__)
ROOT = os.path.abspath(os.path.join(HERE, ".."))      # Docs/Reference
FILESDIR = os.path.join(ROOT, "files")
cur = json.load(open(os.path.join(HERE, "manifest.json")))
prev = json.load(open(os.path.join(HERE, "manifest.prev.json")))

prev_hash = {r["path"]: r["sha256"] for r in prev["files"]}
cur_paths = {r["path"] for r in cur["files"]}

changed, new, unchanged = [], [], []
for r in cur["files"]:
    if r["status"].startswith("skipped"):
        continue
    p = r["path"]
    if p not in prev_hash:
        new.append(p); r["status"] = "pending"
    elif prev_hash[p] != r["sha256"]:
        changed.append(p); r["status"] = "pending"
    else:
        unchanged.append(p); r["status"] = "documented"

removed = sorted(set(prev_hash) - cur_paths)

# Delete stale description files for removed (renamed/deleted) sources.
deleted_files = []
for p in removed:
    md = os.path.join(FILESDIR, p + ".md")
    if os.path.isfile(md):
        os.remove(md)
        deleted_files.append(md)

json.dump(cur, open(os.path.join(HERE, "manifest.json"), "w"), indent=2)
json.dump({"changed": sorted(changed), "new": sorted(new), "removed": removed},
          open(os.path.join(HERE, "todo.json"), "w"), indent=2)

print(f"UNCHANGED (reuse): {len(unchanged)}")
print(f"CHANGED (re-describe): {len(changed)}")
for p in sorted(changed): print("   M", p)
print(f"NEW (describe): {len(new)}")
for p in sorted(new): print("   A", p)
print(f"REMOVED (stale desc deleted): {len(removed)}")
for p in removed: print("   D", p)
print(f"\nDeleted {len(deleted_files)} stale description files.")
print(f"Total to (re)describe: {len(changed) + len(new)}")
