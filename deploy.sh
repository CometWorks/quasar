#!/usr/bin/env bash
set -euo pipefail

SCRIPT_DIR="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
DEPLOY_DIR="$HOME/Documents/Quasar"
CONFIGURATION="Release"
RUN=false

for arg in "$@"; do
    case "$arg" in
        --debug) CONFIGURATION="Debug" ;;
        --run)   RUN=true ;;
    esac
done

echo "Deploying Quasar ($CONFIGURATION) → $DEPLOY_DIR"
mkdir -p "$DEPLOY_DIR"

dotnet publish "$SCRIPT_DIR/Quasar.Bootstrap/Quasar.Bootstrap.csproj" \
    -c "$CONFIGURATION" \
    -r linux-x64 \
    -p:PublishSingleFile=true \
    --no-self-contained \
    -o "$DEPLOY_DIR" \
    -v minimal

chmod +x "$DEPLOY_DIR/Quasar"

echo "Done. $DEPLOY_DIR"

if [[ "$RUN" == "true" ]]; then
    echo "Starting Quasar..."
    exec "$DEPLOY_DIR/Quasar" ensure-running --open-browser
fi
