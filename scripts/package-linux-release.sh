#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ARTIFACT_DIR="$REPO_DIR/artifacts/linux"
CONFIGURATION="${CONFIGURATION:-Release}"
RUNTIME="${RUNTIME:-linux-x64}"
VERSION="${VERSION:-}"
ASSEMBLY_FILE_VERSION="1.0.0"
INFORMATIONAL_VERSION="$VERSION"
NUGET_VERSION="$VERSION"
PACKAGE_ARCHIVE_NAME="quasar-linux-x64.tar.gz"
PACKAGE_ROOT_NAME="Quasar"
SYMBOLS_ARCHIVE_NAME="quasar-symbols-linux-x64.zip"

normalize_version_component() {
    local value="${1:-0}"
    if [[ ! "$value" =~ ^[0-9]+$ ]]; then
        echo 0
        return
    fi
    if (( value > 65534 )); then
        value=$((value % 10000))
    fi
    echo "$value"
}

normalize_nuget_version() {
    local version="${1#v}"
    version="${version%%+*}"

    if [[ -z "$version" ]]; then
        echo "1.0.0"
        return
    fi

    if [[ "$version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$ ]]; then
        echo "$version"
        return
    fi

    if [[ "$version" =~ ^([0-9]+)\.([0-9]+)\.([0-9]+)\.([0-9]+)-([0-9A-Za-z][0-9A-Za-z.-]*)$ ]]; then
        echo "${BASH_REMATCH[1]}.${BASH_REMATCH[2]}.${BASH_REMATCH[3]}-${BASH_REMATCH[4]}.${BASH_REMATCH[5]}"
        return
    fi

    if [[ "$version" =~ ^[0-9]+(\.[0-9]+){3}$ ]]; then
        IFS='.' read -r -a version_parts <<< "$version"
        echo "${version_parts[0]}.${version_parts[1]}.${version_parts[2]}-${version_parts[3]}"
        return
    fi

    local suffix="${version//[^0-9A-Za-z.-]/-}"
    suffix="${suffix#.}"
    suffix="${suffix#-}"
    if [[ -z "$suffix" ]]; then
        suffix="local"
    fi
    echo "1.0.0-${suffix}"
}

# Builds the launcher archive's README from the repo README plus the Linux
# install/run instructions, so whoever unpacks quasar-linux-x64.tar.gz learns how
# to actually run the binary they downloaded — not just the project overview.
#
# Two transforms are applied to the packaged copy (the in-repo README is left
# untouched, keeping its GitHub-friendly relative links and platform-agnostic
# "Getting started" pointer):
#   1. The `<!-- BEGIN/END packaged install instructions -->` marker block is
#      replaced with the platform snippet.
#   2. Repo-relative doc links (`](Docs/...)`) are rewritten to absolute GitHub
#      URLs, pinned to main. The extracted launcher tarball has no Docs/ tree
#      beside the README, so relative links would dangle when opened from it.
build_packaged_readme() {
    local source="$1" snippet="$2" dest="$3"
    local repo_slug="${GITHUB_REPOSITORY:-CometWorks/quasar}"
    local owner="${repo_slug%%/*}"
    local repo="${repo_slug##*/}"
    local base_url="https://github.com/$owner/$repo/blob/main/Docs/"
    awk -v snippet="$snippet" '
        BEGIN { while ((getline line < snippet) > 0) snip = snip line "\n" }
        /<!-- BEGIN packaged install instructions -->/ { printf "%s", snip; skip = 1; next }
        /<!-- END packaged install instructions -->/   { skip = 0; next }
        skip { next }
        { print }
    ' "$source" | sed "s|](Docs/|]($base_url|g" > "$dest"
}

build_assembly_file_version() {
    local raw_version="${1#v}"
    raw_version="${raw_version%%-*}"
    raw_version="${raw_version%%+*}"

    if [[ "$raw_version" =~ ^[0-9]+$ ]]; then
        local numeric="$raw_version"
        local major=0
        local minor=0
        local build=$(( numeric % 10000 ))
        echo "${major}.${minor}.${build}"
        return
    fi

    if [[ ! "$raw_version" =~ ^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$ ]]; then
        echo "$ASSEMBLY_FILE_VERSION"
        return
    fi

    IFS='.' read -r -a version_parts <<< "$raw_version"
    local major
    local minor
    local build

    major="$(normalize_version_component "${version_parts[0]}")"
    minor="$(normalize_version_component "${version_parts[1]}")"
    build="$(normalize_version_component "${version_parts[2]}")"

    echo "${major}.${minor}.${build}"
}

if [[ -z "$VERSION" ]]; then
    VERSION="$(git -C "$REPO_DIR" describe --tags --exact-match 2>/dev/null || true)"
fi
if [[ -z "$VERSION" ]]; then
    VERSION="$(git -C "$REPO_DIR" rev-parse --short HEAD)"
fi
VERSION="${VERSION#v}"
INFORMATIONAL_VERSION="$VERSION"
NUGET_VERSION="$(normalize_nuget_version "$VERSION")"
ASSEMBLY_FILE_VERSION="$(build_assembly_file_version "$VERSION")"

PUBLISH_DIR="$ARTIFACT_DIR/publish"
PACKAGE_DIR="$ARTIFACT_DIR/$PACKAGE_ROOT_NAME"

rm -rf "$ARTIFACT_DIR"
mkdir -p "$PUBLISH_DIR" "$PACKAGE_DIR"

dotnet build "$REPO_DIR/Quasar.Agent/Quasar.Agent.csproj" \
    -c "$CONFIGURATION" \
    -p:Platform=x64 \
    -p:RuntimeIdentifier= \
    -p:SelfContained= \
    -p:PublishSingleFile= \
    -v minimal

dotnet publish "$REPO_DIR/Quasar/Quasar.csproj" \
    -c "$CONFIGURATION" \
    -r "$RUNTIME" -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
    -p:Version="$NUGET_VERSION" \
    -p:AssemblyVersion="$ASSEMBLY_FILE_VERSION" \
    -p:FileVersion="$ASSEMBLY_FILE_VERSION" \
    -p:InformationalVersion="$INFORMATIONAL_VERSION" \
    -p:DebugSymbols=true -p:DebugType=portable -p:Optimize=true -p:IncludeSymbolsInSingleFile=false \
    -o "$PUBLISH_DIR" \
    -v minimal

for component in Quasar.Updater Quasar.Host; do
    component_dir=Updater
    if [[ "$component" == Quasar.Host ]]; then component_dir=Host; fi
    dotnet publish "$REPO_DIR/$component/$component.csproj" -c "$CONFIGURATION" -r "$RUNTIME" \
        -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true \
        -p:Version="$NUGET_VERSION" -p:AssemblyVersion="$ASSEMBLY_FILE_VERSION" -p:FileVersion="$ASSEMBLY_FILE_VERSION" \
        -p:InformationalVersion="$INFORMATIONAL_VERSION" -p:DebugSymbols=true -p:DebugType=portable -p:Optimize=true \
        -o "$PUBLISH_DIR/$component_dir" -v minimal
done
cp -a "$REPO_DIR/Quasar/wwwroot/." "$PUBLISH_DIR/wwwroot/"
python3 "$SCRIPT_DIR/build-peer.py" --runtime "$RUNTIME" --output "$PUBLISH_DIR/Peer/quasar-peer"

python3 "$SCRIPT_DIR/package-symbols.py" --repo "$REPO_DIR" --publish "$PUBLISH_DIR" \
    --output "$ARTIFACT_DIR/$SYMBOLS_ARCHIVE_NAME" --runtime "$RUNTIME" \
    --configuration "$CONFIGURATION" --version "$VERSION"
# Symbols belong exclusively to the matching debug artifact.
find "$PUBLISH_DIR" -type f -iname '*.pdb' -delete

required_application_files=(
    "Quasar"
    "LICENSE"
    "LICENSE.MIT"
    "NOTICE"
    "LICENSING.md"
    "EXCEPTION.md"
    "Peer/quasar-peer"
    "Peer/quasar-peer.json"
    "Peer/NOTICES.txt"
    "wwwroot/_quasar/sdk/v1/rtc.js"
    "wwwroot/_quasar/sdk/v1/boot.js"
    "wwwroot/_quasar/sdk/v1/circuit.js"
    "wwwroot/_quasar/sdk/v1/download-worker.js"
    "Host/Quasar.Host"
    "Updater/Quasar.Updater"
    "wwwroot"
    "wwwroot/_framework/blazor.web.js"
    "wwwroot/_quasar/sdk/v1/client.js"
    "wwwroot/quasar-sdk.js"
    "wwwroot/_content/MudBlazor/MudBlazor.min.css"
    "wwwroot/_content/MudBlazor/MudBlazor.min.js"
)
for required_file in "${required_application_files[@]}"; do
    if [[ ! -e "$PUBLISH_DIR/$required_file" ]]; then
        echo "ERROR: application package missing required file: $required_file" >&2
        exit 1
    fi
done

cp -a "$PUBLISH_DIR/." "$PACKAGE_DIR/"
cp -a "$REPO_DIR/Quasar/appsettings.json" "$PACKAGE_DIR/appsettings.json"
cp -a "$REPO_DIR/install.sh" "$PACKAGE_DIR/install.sh"
cp -a "$REPO_DIR/uninstall.sh" "$PACKAGE_DIR/uninstall.sh"
cp -a "$SCRIPT_DIR/install-gui.sh" "$SCRIPT_DIR/Install.desktop" "$PACKAGE_DIR/"
cp -a "$REPO_DIR/Docs/Installation.md" "$PACKAGE_DIR/INSTALL.md"
cp -a "$REPO_DIR/tools/quasar-renice.c" "$PACKAGE_DIR/quasar-renice.c"
build_packaged_readme "$REPO_DIR/README.md" "$SCRIPT_DIR/readme-install-linux.md" "$PACKAGE_DIR/README.md"
chmod +x "$PACKAGE_DIR/Quasar" "$PACKAGE_DIR/install.sh" "$PACKAGE_DIR/uninstall.sh" "$PACKAGE_DIR/install-gui.sh" "$PACKAGE_DIR/Install.desktop"
# Public release identity differs from the NuGet-compatible build version.
RELEASE_VERSION="$("$PUBLISH_DIR/Quasar" --version)"
printf '{"version":"%s","updaterProtocol":1,"browserTransport":1}\n' "$RELEASE_VERSION" > "$PACKAGE_DIR/release.json"
(cd "$PACKAGE_DIR"; find . -type f ! -path './Updater/*' ! -name appsettings.json ! -name service-installation.json ! -name launcher-files.txt -printf '%P\n' | LC_ALL=C sort > launcher-files.txt)
tar -C "$ARTIFACT_DIR" -czf "$ARTIFACT_DIR/$PACKAGE_ARCHIVE_NAME" "$PACKAGE_ROOT_NAME"

(
    cd "$ARTIFACT_DIR"
    sha256sum "$PACKAGE_ARCHIVE_NAME" "$SYMBOLS_ARCHIVE_NAME" > SHA256SUMS
)

echo "Created Linux release artifacts in $ARTIFACT_DIR"
