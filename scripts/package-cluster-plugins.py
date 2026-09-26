#!/usr/bin/env python3
"""Export resolved Magnetar plugin caches as self-contained local bundles; no downloads or builds."""
import argparse
from pathlib import Path
import re
import shutil
import subprocess
import tempfile
import xml.etree.ElementTree as ET


def export(manifest: Path, cache: Path, destination: Path) -> None:
    for root in (manifest, cache):
        if root.is_symlink() or any(path.is_symlink() for path in root.rglob("*")):
            raise ValueError(f"input must not contain links: {root}")
    data = ET.parse(manifest).getroot()
    plugin_id = data.findtext("Id", "")
    if not re.fullmatch(r"[A-Za-z0-9_-]+", plugin_id) or plugin_id in ("cluster-node", "cluster-wa", "direct-transport"):
        raise ValueError("expected a common plugin ID; cluster/transport bundles are separate inputs")
    if not re.fullmatch(r"[0-9a-f]{40}", data.findtext("Commit", "")):
        raise ValueError("plugin source metadata must pin an exact commit")
    if data.findtext("Runtimes") != "CoreCLR" or data.findtext("Platforms", "Linux") != "Linux":
        raise ValueError("expected CoreCLR/Linux plugin metadata")
    cache_data = ET.parse(cache / "manifest.xml").getroot()
    if not cache_data.findtext("RuntimeIdentifier", "").startswith("linux-"):
        raise ValueError("expected an already resolved Linux Magnetar cache")
    binary = cache / "Bin/plugin.dll"
    if not binary.is_file() or not binary.stat().st_size:
        raise ValueError("cache has no compiled Bin/plugin.dll")
    folder = destination / plugin_id
    if folder.exists():
        raise ValueError(f"duplicate plugin ID: {plugin_id}")
    shutil.copytree(cache / "Bin", folder)
    (folder / "plugin.dll").rename(folder / (plugin_id + ".dll"))
    # A cached companion manifest may describe paths before relocation.
    for name in ("plugin.xml", "plugin.dll.xml"):
        (folder / name).unlink(missing_ok=True)
    if (cache / "Assets").exists():
        shutil.copytree(cache / "Assets", folder / "Assets")
    resolved = {entry.findtext("Name"): entry.findtext("Path")
                for entry in cache_data.findall("Assets/Asset")}
    for asset in list(data.findall("Asset")):
        if asset.get("Platforms", "Linux") != "Linux" or asset.get("Runtimes", "CoreCLR") != "CoreCLR":
            data.remove(asset)
            continue
        path = resolved.get(asset.get("Name"))
        if not path:
            raise ValueError(f"cache did not resolve asset {asset.get('Name')}")
        path = Path(path).resolve(strict=True)
        if path.is_relative_to((cache / "Bin").resolve()):
            relative = path.relative_to((cache / "Bin").resolve())
        elif path.is_relative_to((cache / "Assets").resolve()):
            relative = Path("Assets") / path.relative_to((cache / "Assets").resolve())
        else:
            raise ValueError("cached asset is outside Bin/Assets; materialize it before export")
        if not (folder / relative).exists():
            raise ValueError("asset was not included in the bundle")
        for key in ("Url", "Sha256", "Extract"):
            asset.attrib.pop(key, None)
        asset.set("Path", relative.as_posix())
    ET.ElementTree(data).write(folder / (plugin_id + ".xml"), encoding="utf-8", xml_declaration=True)
    shutil.copyfile(manifest, folder / "source-metadata.xml")
    shutil.copyfile(cache / "manifest.xml", folder / "build-cache.xml")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--plugin", nargs=2, action="append", required=True, metavar=("MANIFEST", "CACHE"))
    parser.add_argument("--output", type=Path, required=True)
    args = parser.parse_args()
    destination = args.output.absolute()
    if destination.exists() or destination.is_symlink():
        parser.error("output already exists; choose a new bundle directory")
    destination.parent.mkdir(parents=True, exist_ok=True)
    stage = Path(tempfile.mkdtemp(prefix=".plugins-stage-", dir=destination.parent))
    try:
        for manifest, cache in args.plugin:
            export(Path(manifest).absolute(), Path(cache).absolute(), stage)
        if not {"dotnet-compat", "linux-compat"} <= {path.name for path in stage.iterdir()}:
            raise ValueError("include both dotnet-compat and linux-compat")
        # Same-filesystem atomic publication, with GNU mv's no-clobber behavior.
        subprocess.run(["mv", "-T", "--no-clobber", "--", str(stage), str(destination)], check=True)
        if stage.exists():
            raise ValueError("output appeared during export; refusing to replace it")
    finally:
        if stage.exists():
            shutil.rmtree(stage)
    print(destination)


if __name__ == "__main__":
    main()
