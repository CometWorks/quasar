#!/usr/bin/env python3
"""Verify every relative markdown link in the handbook resolves to an existing file."""
import os
import re
import sys

HERE = os.path.dirname(__file__)
ROOT = os.path.abspath(os.path.join(HERE, ".."))      # Docs/Reference
REPO = os.path.abspath(os.path.join(ROOT, "..", ".."))

LINK_RE = re.compile(r"\[[^\]]*\]\(([^)]+)\)")
broken = []
checked = 0

targets = []
for base, _, fs in os.walk(ROOT):
    for f in fs:
        if f.endswith(".md"):
            targets.append(os.path.join(base, f))

for md in targets:
    text = open(md, encoding="utf-8").read()
    for link in LINK_RE.findall(text):
        url = link.split("#", 1)[0].strip()
        if not url or url.startswith(("http://", "https://", "mailto:")):
            continue
        checked += 1
        resolved = os.path.normpath(os.path.join(os.path.dirname(md), url))
        if not os.path.exists(resolved):
            broken.append((os.path.relpath(md, REPO), link))

print(f"Checked {checked} relative links across {len(targets)} markdown files.")
if broken:
    print(f"BROKEN ({len(broken)}):")
    for src, link in broken:
        print(f"  {src} -> {link}")
    sys.exit(1)
print("All links OK.")
