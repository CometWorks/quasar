#!/usr/bin/env python3
"""Maintenance tool of the Quasar handbook. Readers never need it.

  python3 data/book.py check    verify links, anchors, Sources lists and style
  python3 data/book.py stale    list pages whose sources changed since the last record
  python3 data/book.py record   store the source hashes after the pages were refreshed
  python3 data/book.py index    regenerate Index.md from the pages

Each topic page ends with a "## Sources" list of repo files. Their LF-normalized SHA256
hashes are kept in data/sources.json, so a refresh after a Quasar release only rereads
the pages whose sources changed.
"""

import hashlib
import json
import re
import subprocess
import sys
from pathlib import Path

BOOK = Path(__file__).resolve().parent.parent
REPO = BOOK.parent.parent
RECORD = BOOK / "data" / "sources.json"
NAVIGATION = {"SKILL.md", "TOC.md", "Index.md", "OpenQuestions.md"}
# Inputs a refresh must look at even when no page lists them yet
WATCHED = ["Docs/*.md", "Docs/StateMachines/*.md", "README.md"]

LINK = re.compile(r"\[[^\]]*\]\(([^)\s]+)\)")
SOURCE_LINE = re.compile(r"^- `([^`]+)`")


def pages() -> list[Path]:
    return sorted(BOOK.glob("*.md"))


def sections(page: Path) -> list[tuple[int, str, str, str]]:
    """Level, title, GitHub-style anchor and body text of every heading of a page."""
    found: list[tuple[int, str, str, str]] = []
    slugs: set[str] = set()
    body: list[str] = []
    in_code = False
    for line in page.read_text(encoding="utf-8").splitlines() + ["# end"]:
        if line.startswith("```"):
            in_code = not in_code
        if in_code or not line.startswith("#"):
            body.append(line)
            continue
        if found:
            found[-1] = found[-1][:3] + ("\n".join(body),)
        body = []
        title = line.lstrip("#").strip()
        slug = re.sub(r"[^\w\- ]", "", title.lower()).replace(" ", "-")
        base, n = slug, 1
        while slug in slugs:
            slug = f"{base}-{n}"
            n += 1
        slugs.add(slug)
        found.append((len(line) - len(line.lstrip("#")), title, slug, ""))
    return found[:-1]


def anchors(page: Path) -> set[str]:
    return {slug for _, _, slug, _ in sections(page)}


def sources(page: Path) -> list[str]:
    text = page.read_text(encoding="utf-8")
    _, _, tail = text.partition("\n## Sources\n")
    return [m.group(1) for line in tail.splitlines() if (m := SOURCE_LINE.match(line))]


def file_hash(path: Path) -> str:
    data = path.read_bytes()
    if b"\0" not in data[:8192]:
        data = data.replace(b"\r\n", b"\n").replace(b"\r", b"\n")
    return hashlib.sha256(data).hexdigest()


def check() -> int:
    errors = []
    names = {p.name for p in pages()}
    toc = (BOOK / "TOC.md").read_text(encoding="utf-8")
    index = (BOOK / "Index.md").read_text(encoding="utf-8")
    for page in pages():
        text = page.read_text(encoding="utf-8")
        for target in LINK.findall(text):
            if re.match(r"[a-z]+:", target):
                continue
            name, _, anchor = target.partition("#")
            name = name or page.name
            if name not in names:
                errors.append(f"{page.name}: broken link {target}")
            elif anchor and anchor not in anchors(BOOK / name):
                errors.append(f"{page.name}: missing anchor {target}")
        for dash in ("—", "–"):
            if dash in text:
                errors.append(f"{page.name}: contains an em or en dash")
        if page.name in NAVIGATION:
            continue
        if f"({page.name}" not in toc:
            errors.append(f"{page.name}: not linked from TOC.md")
        if f"({page.name}" not in index:
            errors.append(f"{page.name}: not linked from Index.md")
        listed = sources(page)
        if not listed:
            errors.append(f"{page.name}: no Sources list")
        errors += [
            f"{page.name}: source not in repo: {s}"
            for s in listed
            if not (REPO / s).is_file()
        ]
    print("\n".join(errors) or "OK")
    return 1 if errors else 0


def current() -> dict[str, list[str]]:
    return {p.name: sources(p) for p in pages() if p.name not in NAVIGATION}


def stale() -> int:
    if not RECORD.exists():
        print("No data/sources.json yet, run record first")
        return 1
    recorded = json.loads(RECORD.read_text(encoding="utf-8"))
    hashes = recorded["files"]
    print(f"Recorded at Quasar commit {recorded['commit']}")
    changed = 0
    for page, listed in current().items():
        reasons = []
        for source in listed:
            path = REPO / source
            if not path.is_file():
                reasons.append(f"removed {source}")
            elif hashes.get(source) != file_hash(path):
                reasons.append(f"changed {source}")
        if reasons:
            changed += 1
            print(f"{page}:\n  " + "\n  ".join(reasons))
    covered = {s for listed in current().values() for s in listed}
    unread = sorted(
        str(p.relative_to(REPO)) for pattern in WATCHED for p in REPO.glob(pattern)
    )
    unread = [p for p in unread if p not in covered and p not in hashes]
    if unread:
        print("New inputs no page lists yet:\n  " + "\n  ".join(unread))
    print(f"{changed} page(s) to refresh")
    return 0


def record() -> int:
    listed = sorted({s for sources_ in current().values() for s in sources_})
    watched = {
        str(p.relative_to(REPO)) for pattern in WATCHED for p in REPO.glob(pattern)
    }
    commit = subprocess.run(
        ["git", "-C", str(REPO), "rev-parse", "HEAD"],
        capture_output=True,
        text=True,
        check=True,
    ).stdout.strip()
    files = {
        s: file_hash(REPO / s)
        for s in sorted(set(listed) | watched)
        if (REPO / s).is_file()
    }
    RECORD.write_text(
        json.dumps({"commit": commit, "files": files}, indent=1) + "\n",
        encoding="utf-8",
    )
    print(f"Recorded {len(files)} files at {commit}")
    return 0


ENV_VAR = re.compile(r"`(QUASAR_[A-Z0-9_]+)")
FILE_NAME = re.compile(
    r"`([\w.-]+\.(?:json|jsonl|sh|ps1|log|sbc|sbl|cfg|service|timer))`"
)


def index() -> int:
    """Index.md: every section heading, environment variable and file name, linked to where it appears."""
    groups: dict[str, dict[str, set[str]]] = {
        "Topics": {},
        "Environment variables": {},
        "Files": {},
    }
    for page in pages():
        if page.name in NAVIGATION:
            continue
        title = sections(page)[0][1]
        for level, heading, slug, text in sections(page):
            if heading == "Sources":
                continue
            link = (
                f"[{title}]({page.name}#{slug})"
                if level > 1
                else f"[{title}]({page.name})"
            )
            if level > 1:
                groups["Topics"].setdefault(heading, set()).add(link)
            for pattern, group in (
                (ENV_VAR, "Environment variables"),
                (FILE_NAME, "Files"),
            ):
                for term in pattern.findall(heading + "\n" + text):
                    entries = groups[group].setdefault(f"`{term}`", set())
                    if not any(e.startswith(f"[{title}]") for e in entries):
                        entries.add(link)
    lines = [
        "# Index",
        "",
        "Every section, environment variable and file name in the handbook, alphabetically, with the pages that cover it.",
        "Generated by `python3 data/book.py index`; don't edit by hand. For the pages by topic see [TOC.md](TOC.md).",
    ]
    for group, entries in groups.items():
        lines += ["", f"## {group}", ""]
        for term in sorted(entries, key=lambda s: s.strip("`").lower()):
            lines.append(f"- {term}: " + ", ".join(sorted(entries[term])))
    lines += ["", "## Pages", ""]
    lines += [
        f"- [{sections(p)[0][1]}]({p.name})"
        for p in pages()
        if p.name not in NAVIGATION
    ]
    (BOOK / "Index.md").write_text("\n".join(lines) + "\n", encoding="utf-8")
    print(f"Index.md: {sum(len(e) for e in groups.values())} entries")
    return 0


if __name__ == "__main__":
    commands = {"check": check, "stale": stale, "record": record, "index": index}
    if len(sys.argv) != 2 or sys.argv[1] not in commands:
        print(__doc__)
        sys.exit(2)
    sys.exit(commands[sys.argv[1]]())
