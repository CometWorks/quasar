#!/usr/bin/env python3
"""Build the locked native peer, manifest and dependency notices for one release."""
import argparse
import hashlib
import json
import os
from pathlib import Path
import subprocess

ROOT = Path(__file__).resolve().parents[1]
VERSION = "go1.27.3"

def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("--output", type=Path, required=True)
    parser.add_argument("--runtime", choices=["linux-x64", "win-x64"], required=True)
    parser.add_argument("--race", action="store_true")
    args = parser.parse_args()
    env = {**os.environ, "GOTOOLCHAIN": "local", "CGO_ENABLED": "1" if args.race else "0",
           "GOOS": "windows" if args.runtime == "win-x64" else "linux", "GOARCH": "amd64"}
    go = os.environ.get("QUASAR_GO", "go")
    def run(*parts):
        return subprocess.check_output([go, *parts], cwd=ROOT / "Quasar.Peer", env=env, text=True)
    if run("env", "GOVERSION").strip() != VERSION:
        raise SystemExit("The peer requires pinned Go 1.27.3; set QUASAR_GO to its executable.")
    run("mod", "download")
    run("mod", "verify")
    args.output = args.output.resolve()
    args.output.parent.mkdir(parents=True, exist_ok=True)
    run("build", "-mod=readonly", "-trimpath", "-buildvcs=false", *(["-race"] if args.race else []), "-o", str(args.output), ".")
    digest = hashlib.sha256(args.output.read_bytes()).hexdigest()
    manifest = {"protocol": 1, "runtime": args.runtime, "go": VERSION, "sha256": digest}
    args.output.with_suffix(args.output.suffix + ".json").write_text(json.dumps(manifest, indent=2) + "\n", encoding="utf8")
    notices = [("Quasar.Peer", (ROOT / "Quasar.Peer/NOTICE").read_text()), ("Go " + VERSION, (Path(run("env", "GOROOT").strip()) / "LICENSE").read_text())]
    # Go emits a sequence of JSON objects. Keep every downloaded build module's license.
    modules = run("list", "-m", "-json", "all")
    decoder = json.JSONDecoder()
    while modules.strip():
        module, end = decoder.raw_decode(modules.lstrip()); modules = modules.lstrip()[end:]
        if module.get("Main") or "Dir" not in module:
            continue
        directory = Path(module["Dir"])
        licenses = [p for p in directory.iterdir() if p.is_file() and p.name.upper().startswith(("LICENSE", "COPYING", "NOTICE"))]
        if not licenses:
            raise SystemExit("No dependency license found: " + module["Path"])
        notices.append((module["Path"] + " " + module["Version"], "\n".join(p.read_text() for p in sorted(licenses))))
    args.output.parent.joinpath("NOTICES.txt").write_text("\n\n".join(title + "\n" + text for title, text in notices), encoding="utf8")
    print(f"Built {args.runtime} peer; SHA256 {digest}")

if __name__ == "__main__":
    main()
