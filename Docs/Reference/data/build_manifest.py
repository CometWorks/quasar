#!/usr/bin/env python3
"""Build a manifest of all source documents for the structured-documentation pipeline.

Records name, location, file type, size, sha256, tier and module for every tracked
source file. The sha256 is the cache key: on re-runs, unchanged files keep their
existing description and only differences are re-processed.

The hash is platform-independent: text files are newline-normalized (CRLF/CR -> LF)
before hashing and sizing, so a Windows checkout (CRLF) and a Linux checkout (LF) of
this repo yield identical hashes. This keeps the committed cache valid no matter which
OS the repo is cloned on; binary assets are hashed by their exact bytes.

Run from the repository root.
"""
import hashlib
import json
import os
import subprocess
import sys

REPO = os.path.abspath(os.path.join(os.path.dirname(__file__), "..", "..", ".."))
OUT = os.path.join(REPO, "Docs", "Reference", "data", "manifest.json")

# File extensions we document. Binary assets (png, ico) are recorded as "skipped".
TEXT_EXTS = {".cs", ".razor", ".css", ".js", ".json", ".csproj", ".props", ".sln", ".sh", ".md"}
BINARY_EXTS = {".png", ".ico"}


def git_files():
    out = subprocess.check_output(["git", "ls-files"], cwd=REPO, text=True)
    return [f for f in out.splitlines() if f.strip()]


def module_of(path):
    """Map a file path to a documentation module key."""
    p = path.replace("\\", "/")
    if p.startswith("Magnetar.Protocol/"):
        return "Magnetar.Protocol"
    if p.startswith("Quasar.Agent/"):
        return "Quasar.Agent"
    if p.startswith("Quasar.Bootstrap/"):
        return "Quasar.Bootstrap"
    if p.startswith("Quasar/Services/Analytics/"):
        return "Quasar.Services.Analytics"
    if p.startswith("Quasar/Services/Auth/"):
        return "Quasar.Services.Auth"
    if p.startswith("Quasar/Services/Discord/"):
        return "Quasar.Services.Discord"
    if p.startswith("Quasar/Services/PluginSdk/"):
        return "Quasar.Services.PluginSdk"
    if p.startswith("Quasar/Services/"):
        return "Quasar.Services.Core"
    if p.startswith("Quasar/Models/"):
        return "Quasar.Models"
    if p.startswith("Quasar/Components/"):
        return "Quasar.Components"
    if p.startswith("Quasar/wwwroot/"):
        return "Quasar.Host"
    if p.startswith("Quasar/"):
        return "Quasar.Host"
    return None  # root-level docs/scripts not part of code modules


def tier_of(path, ext, size):
    """Assign a documentation tier.

    Tier 1: small/complex core logic needing full understanding (protocol, models,
            core services, agent, bootstrap, host entrypoint).
    Tier 2: UI components and feature service subsystems (often longer, more uniform).
    Tier 3: low-priority / generated / asset-like (css, js, json, csproj, props).
    """
    if ext in (".css", ".js", ".json", ".csproj", ".props", ".sln"):
        return 3
    if ext == ".razor":
        return 2
    p = path.replace("\\", "/")
    if p.startswith(("Magnetar.Protocol/", "Quasar.Agent/", "Quasar.Bootstrap/",
                     "Quasar/Models/")) or p == "Quasar/Program.cs":
        return 1
    if p.startswith("Quasar/Services/"):
        # Core services tier 1; feature subsystems tier 2
        sub = p[len("Quasar/Services/"):]
        if "/" in sub:
            return 2
        return 1
    return 2


def main():
    files = git_files()
    records = []
    for f in files:
        full = os.path.join(REPO, f)
        if not os.path.isfile(full):
            continue
        ext = os.path.splitext(f)[1].lower()
        mod = module_of(f)
        # Only document code/asset files that belong to a module.
        if mod is None:
            continue
        with open(full, "rb") as fh:
            data = fh.read()
        # Normalize line endings before hashing/sizing so the cache key and size
        # are stable across platforms/checkouts (git stores LF via `* text=auto`,
        # but a Windows working tree has CRLF). Convert CRLF and lone CR to LF;
        # binary assets are hashed by their exact bytes (no normalization).
        if ext in TEXT_EXTS:
            data = data.replace(b"\r\n", b"\n").replace(b"\r", b"\n")
        size = len(data)
        digest = hashlib.sha256(data).hexdigest()
        if ext in BINARY_EXTS:
            status = "skipped-binary"
            tier = 3
        elif ext in TEXT_EXTS:
            status = "pending"
            tier = tier_of(f, ext, size)
        else:
            status = "skipped-unknown"
            tier = 3
        records.append({
            "path": f,
            "name": os.path.basename(f),
            "ext": ext,
            "size": size,
            "sha256": digest,
            "module": mod,
            "tier": tier,
            "status": status,
        })
    records.sort(key=lambda r: r["path"])
    os.makedirs(os.path.dirname(OUT), exist_ok=True)
    with open(OUT, "w") as fh:
        json.dump({"files": records}, fh, indent=2)

    # Summary to stdout
    from collections import Counter
    by_mod = Counter(r["module"] for r in records if r["status"] == "pending")
    by_tier = Counter(r["tier"] for r in records if r["status"] == "pending")
    skipped = [r["path"] for r in records if r["status"].startswith("skipped")]
    print(f"Total recorded: {len(records)}")
    print(f"Documentable (pending): {sum(by_mod.values())}")
    print("By module:")
    for m, c in sorted(by_mod.items()):
        print(f"  {m:32s} {c}")
    print("By tier:", dict(sorted(by_tier.items())))
    print(f"Skipped (binary/unknown): {len(skipped)} -> {skipped}")


if __name__ == "__main__":
    main()
