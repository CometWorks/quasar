#!/usr/bin/env bash
set -euo pipefail
repo_directory="$(cd -- "$(dirname -- "${BASH_SOURCE[0]}")/.." && pwd)"
diagnostics_project="${1:-$repo_directory/../error-reporting/Diagnostics/CometWorks.Diagnostics.csproj}"
if [[ ! -f "$diagnostics_project" ]]; then
  echo 'Pass the path to the error-reporting Diagnostics/CometWorks.Diagnostics.csproj checkout.' >&2
  exit 1
fi
dotnet pack "$diagnostics_project" -c Release -o "$repo_directory/../diagnostics-artifacts/packages"
