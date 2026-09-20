#!/usr/bin/env bash
# Build one pinned transport artifact for cluster dependency provisioning. Never deploys into Magnetar.
set -euo pipefail

if [[ $# != 5 ]]; then
    echo "Usage: $0 SOURCE_CHECKOUT COMMIT DS64 MAGNETAR_INSTALL OUTPUT_DIRECTORY" >&2
    exit 2
fi
source_checkout=$(realpath "$1")
source_commit=$2
ds64=$(realpath "$3")
magnetar_install=$(realpath "$4")
output=$(realpath -m "$5")
if [[ ! $source_commit =~ ^[0-9a-f]{40}$ ]] || [[ $(git -C "$source_checkout" rev-parse "$source_commit^{commit}") != "$source_commit" ]]; then
    echo "An exact lowercase source commit is required." >&2
    exit 2
fi
if [[ -e $output || -L $output ]]; then
    echo "Output already exists; choose a new artifact directory." >&2
    exit 2
fi
if [[ ! -f $ds64/SpaceEngineers.Game.dll || ! -f $magnetar_install/Libraries/MagnetarInterim/PluginSdk.dll ]]; then
    echo "DS64 and the current Linux Magnetar SDK must be installed first." >&2
    exit 2
fi
if git -C "$source_checkout" ls-tree -r "$source_commit" | grep -q '^160000 '; then
    echo "Submodules require a separate pinned build procedure." >&2
    exit 2
fi
build_root=$(mktemp -d)
stage=''
cleanup() {
    rm -rf -- "$build_root"
    if [[ -n $stage ]]; then rm -rf -- "$stage"; fi
}
trap cleanup EXIT
# Build committed files only, including when the operator's checkout has local changes.
git -C "$source_checkout" archive "$source_commit" | tar -x -C "$build_root"
dotnet build "$build_root/ServerPlugin/ServerPlugin.csproj" -c Release -f net10.0 \
    -p:SolutionDir="$build_root/" -p:Dedicated64="$ds64" -p:Magnetar="$magnetar_install" \
    -p:MagnetarBin="$magnetar_install/Libraries/MagnetarInterim" \
    -p:RunPostBuildEvent=Never -p:CopyLocalLockFileAssemblies=true --nologo
mkdir -p "$(dirname "$output")"
stage=$(mktemp -d "$(dirname "$output")/.transport-stage-XXXXXXXX")
for file in DirectTransport.dll LiteNetLib.dll; do
    cp -- "$build_root/ServerPlugin/bin/Release/net10.0/$file" "$stage/$file"
done
python3 - "$build_root/DirectTransportServer.xml" "$source_commit" "$stage" <<'PY'
import pathlib
import sys
import xml.etree.ElementTree as ET

source, commit, destination = sys.argv[1:]
tree = ET.parse(source)
root = tree.getroot()
if root.findtext("Id") != "direct-transport":
    raise SystemExit("Unexpected Direct Transport plugin ID")
element = root.find("Commit")
if element is None:
    element = ET.SubElement(root, "Commit")
element.text = commit
# This helper builds net10.0 for Linux. Older source manifests use runtime names
# that current Magnetar no longer recognizes, even though the assembly is compatible.
for name, value in (("Runtimes", "CoreCLR"), ("Platforms", "Linux")):
    element = root.find(name)
    if element is None:
        element = ET.SubElement(root, name)
    element.text = value
for name in ("DirectTransport.xml", "DirectTransport.dll.xml"):
    tree.write(pathlib.Path(destination) / name, encoding="utf-8", xml_declaration=True)
PY
# GNU mv --no-clobber leaves the staging directory intact if another build won the destination.
mv -T --no-clobber -- "$stage" "$output"
if [[ -d $stage ]]; then
    echo "Output appeared during the build; refusing to replace it." >&2
    exit 1
fi
stage=''
echo "Built Direct Transport $source_commit at $output"
