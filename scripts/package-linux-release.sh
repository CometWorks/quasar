#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
REPO_DIR="$(cd "$SCRIPT_DIR/.." && pwd)"
ARTIFACT_DIR="$REPO_DIR/artifacts/linux"
CONFIGURATION="${CONFIGURATION:-Release}"
RUNTIME="${RUNTIME:-linux-x64}"
VERSION="${VERSION:-}"

if [[ -z "$VERSION" ]]; then
    VERSION="$(git -C "$REPO_DIR" describe --tags --exact-match 2>/dev/null || true)"
fi
if [[ -z "$VERSION" ]]; then
    VERSION="$(git -C "$REPO_DIR" rev-parse --short HEAD)"
fi
VERSION="${VERSION#v}"

PUBLISH_DIR="$ARTIFACT_DIR/publish"
WEB_DIR="$ARTIFACT_DIR/web"
BOOTSTRAP_DIR="$ARTIFACT_DIR/bootstrap"

rm -rf "$ARTIFACT_DIR"
mkdir -p "$PUBLISH_DIR" "$WEB_DIR" "$BOOTSTRAP_DIR"

dotnet publish "$REPO_DIR/Quasar.Bootstrap/Quasar.Bootstrap.csproj" \
    -c "$CONFIGURATION" \
    -r "$RUNTIME" \
    -p:CopyToDeployDir=false \
    -p:Version="$VERSION" \
    -p:AssemblyVersion="$VERSION" \
    -p:FileVersion="$VERSION" \
    -o "$PUBLISH_DIR" \
    -v minimal

cp -a "$PUBLISH_DIR/WebService/." "$WEB_DIR/"
chmod +x "$WEB_DIR/Quasar"
tar -C "$WEB_DIR" -czf "$ARTIFACT_DIR/quasar-web-linux-x64.tar.gz" .

cp -a "$PUBLISH_DIR/Quasar" "$BOOTSTRAP_DIR/Quasar"
cp -a "$REPO_DIR/Quasar/appsettings.json" "$BOOTSTRAP_DIR/appsettings.json"
cp -a "$REPO_DIR/install.sh" "$BOOTSTRAP_DIR/install.sh"
cp -a "$REPO_DIR/uninstall.sh" "$BOOTSTRAP_DIR/uninstall.sh"
cp -a "$REPO_DIR/README.md" "$BOOTSTRAP_DIR/README.md"
chmod +x "$BOOTSTRAP_DIR/Quasar" "$BOOTSTRAP_DIR/install.sh" "$BOOTSTRAP_DIR/uninstall.sh"
tar -C "$BOOTSTRAP_DIR" -czf "$ARTIFACT_DIR/quasar-bootstrap-linux-x64.tar.gz" .

(
    cd "$ARTIFACT_DIR"
    sha256sum quasar-web-linux-x64.tar.gz quasar-bootstrap-linux-x64.tar.gz > SHA256SUMS
)

echo "Created Linux release artifacts in $ARTIFACT_DIR"
