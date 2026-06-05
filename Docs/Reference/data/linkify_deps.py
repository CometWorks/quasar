#!/usr/bin/env python3
"""Convert plain source-path references in each description file's Dependencies
section into clickable links to the sibling description files.

Idempotent: a Dependencies section that already contains markdown links is skipped.
"""
import json
import os
import re

HERE = os.path.dirname(__file__)
ROOT = os.path.abspath(os.path.join(HERE, ".."))     # Docs/Reference
FILESDIR = os.path.join(ROOT, "files")
manifest = json.load(open(os.path.join(HERE, "manifest.json")))
valid = {r["path"] for r in manifest["files"] if r["status"] == "pending"}

PATH_RE = re.compile(r"`?((?:Quasar(?:\.Agent|\.Bootstrap)?|Magnetar\.Protocol)/[\w./-]+\.\w+)`?")

changed = 0
for r in manifest["files"]:
    if r["status"] != "pending":
        continue
    selfpath = r["path"]
    md = os.path.join(FILESDIR, selfpath + ".md")
    if not os.path.isfile(md):
        continue
    text = open(md, encoding="utf-8").read()
    m = re.search(r"(##\s*Dependencies\s*\n)(.+?)(\n##\s|\Z)", text, re.S)
    if not m:
        continue
    head, body, tail = m.group(1), m.group(2), m.group(3)
    if "](" in body:          # already linkified
        continue
    self_dir = os.path.dirname(md)

    def repl(mt):
        p = mt.group(1)
        if p == selfpath or p not in valid:
            return mt.group(0)
        target = os.path.join(FILESDIR, p + ".md")
        rel = os.path.relpath(target, self_dir).replace(os.sep, "/")
        return f"[`{p}`]({rel})"

    new_body = PATH_RE.sub(repl, body)
    if new_body != body:
        text = text[:m.start()] + head + new_body + tail + text[m.end():]
        open(md, "w", encoding="utf-8").write(text)
        changed += 1

print(f"Linkified dependency sections in {changed} description files.")
