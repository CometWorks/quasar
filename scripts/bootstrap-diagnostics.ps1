param([string] $DiagnosticsProject)
$ErrorActionPreference = 'Stop'
$RepoDirectory = Split-Path $PSScriptRoot -Parent
if (-not $DiagnosticsProject) { $DiagnosticsProject = Join-Path $RepoDirectory '../error-reporting/Diagnostics/CometWorks.Diagnostics.csproj' }
if (-not (Test-Path $DiagnosticsProject)) { throw 'Pass the error-reporting Diagnostics/CometWorks.Diagnostics.csproj checkout path.' }
& dotnet pack $DiagnosticsProject -c Release -o (Join-Path $RepoDirectory '../diagnostics-artifacts/packages')
if ($LASTEXITCODE -ne 0) { throw 'Diagnostics package build failed.' }
