#!/usr/bin/env python3
"""Place a hub plugin's pinned assets where its dev folder manifest expects them.

A dev folder override (Configs -> Developer) builds a plugin from a checkout, and Magnetar
resolves the manifest's <Asset Path="..."> entries relative to that checkout. A hub
manifest pins the same assets as release downloads (<Asset Url="..." Sha256="...">).
This script downloads each pinned asset, checks its SHA-256 and puts it at the dev
manifest's path for the asset of the same name, so a branch build of the plugin's C#
runs with the hub pin's native libraries (linux-compat: LinuxDependencies, NativeWrappers).

    dev-override-assets.py ~/overrides/se1/linux-compat ServerPlugin/ServerPlugin.xml \\
        https://raw.githubusercontent.com/CometWorks/magnetar-hub/main/Plugins/LinuxCompat.xml

An asset path inside the checkout is refused: it would make the dev folder dirty, and a
cluster stages only a clean dev folder. Existing targets are kept unless --force is given.
"""

import argparse
import hashlib
import shutil
import sys
import tarfile
import tempfile
import urllib.request
import xml.etree.ElementTree as ET
import zipfile
from pathlib import Path


def read_xml(source: str) -> ET.Element:
    if source.startswith(("https://", "http://")):
        with urllib.request.urlopen(source, timeout=60) as response:
            return ET.fromstring(response.read())
    return ET.parse(source).getroot()


def is_true(value: str | None) -> bool:
    return (value or "").strip().lower() == "true"


def download(url: str, sha256: str, destination: Path) -> None:
    with urllib.request.urlopen(url, timeout=600) as response, destination.open(
        "wb"
    ) as file:
        shutil.copyfileobj(response, file)
    actual = hashlib.sha256(destination.read_bytes()).hexdigest()
    if actual != sha256.lower():
        raise ValueError(f"{url}: SHA-256 {actual}, the hub pins {sha256}")


def extract(archive: Path, destination: Path) -> None:
    if zipfile.is_zipfile(archive):
        with zipfile.ZipFile(archive) as zip_file:
            zip_file.extractall(destination)
    else:
        with tarfile.open(archive) as tar:
            tar.extractall(destination, filter="data")


def place(
    checkout: Path, dev_asset: ET.Element, hub_asset: ET.Element, force: bool
) -> str:
    name = dev_asset.get("Name")
    target = (checkout / dev_asset.get("Path")).resolve()
    if target.is_relative_to(checkout):
        raise ValueError(f"{name}: {target} is inside the checkout")
    if (
        target.is_file()
        and hashlib.sha256(target.read_bytes()).hexdigest()
        == hub_asset.get("Sha256", "").lower()
    ):
        return f"{name}: {target} is already the hub pin"
    if target.exists() and not force:
        return f"{name}: kept existing {target} (--force replaces it)"
    with tempfile.TemporaryDirectory() as temp:
        archive = Path(temp) / "asset"
        download(hub_asset.get("Url"), hub_asset.get("Sha256", ""), archive)
        if target.is_dir():
            shutil.rmtree(target)
        elif target.exists():
            target.unlink()
        target.parent.mkdir(parents=True, exist_ok=True)
        # The dev manifest extracts an archive itself; a folder asset gets the hub archive's content.
        if is_true(hub_asset.get("Extract")) and not is_true(dev_asset.get("Extract")):
            extract(archive, target)
        else:
            shutil.copyfile(archive, target)
    return f"{name}: {hub_asset.get('Url')} -> {target}"


def main() -> int:
    parser = argparse.ArgumentParser(
        description=__doc__, formatter_class=argparse.RawDescriptionHelpFormatter
    )
    parser.add_argument(
        "checkout", type=Path, help="the dev folder (the plugin's checkout)"
    )
    parser.add_argument("manifest", help="the dev manifest, relative to the checkout")
    parser.add_argument("hub_manifest", help="the hub manifest, a file or an https URL")
    parser.add_argument(
        "--force", action="store_true", help="replace assets that already exist"
    )
    args = parser.parse_args()

    checkout = args.checkout.expanduser().resolve()
    dev_assets = [
        a
        for a in read_xml(str(checkout / args.manifest)).iter("Asset")
        if a.get("Path")
    ]
    hub_assets = {
        a.get("Name"): a
        for a in read_xml(args.hub_manifest).iter("Asset")
        if a.get("Url")
    }
    if not dev_assets:
        print("The dev manifest has no asset paths; nothing to place.")
        return 0
    missing = [a.get("Name") for a in dev_assets if a.get("Name") not in hub_assets]
    if missing:
        print(
            "The hub manifest pins no download for: " + ", ".join(missing),
            file=sys.stderr,
        )
        return 1
    for dev_asset in dev_assets:
        print(place(checkout, dev_asset, hub_assets[dev_asset.get("Name")], args.force))
    return 0


if __name__ == "__main__":
    sys.exit(main())
