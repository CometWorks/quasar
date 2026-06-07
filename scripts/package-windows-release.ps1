#!/usr/bin/env pwsh
# Windows equivalent of scripts/package-linux-release.sh.
#
# Produces the win-x64 release artifacts under artifacts/windows:
#   - quasar-web-win-x64.zip   (replaceable web UI worker + wwwroot)
#   - quasar-win-x64.zip       (stable launcher + install/uninstall scripts)
#   - SHA256SUMS               (sha256 of both archives, lowercase, two-space separated)
#
# The version-normalization rules mirror package-linux-release.sh exactly so the
# two pipelines stamp identical assembly/NuGet versions for the same input.

$ErrorActionPreference = 'Stop'
# PowerShell 7.4+ defaults to throwing on any native non-zero exit when
# ErrorActionPreference is Stop. Disable that here so the `git describe` probe can
# fall back gracefully; native failures we care about are checked via $LASTEXITCODE.
$PSNativeCommandUseErrorActionPreference = $false

$ScriptDir = $PSScriptRoot
$RepoDir = Split-Path -Parent $ScriptDir
$ArtifactDir = Join-Path $RepoDir 'artifacts\windows'
$Configuration = if ($env:CONFIGURATION) { $env:CONFIGURATION } else { 'Release' }
$Runtime = if ($env:RUNTIME) { $env:RUNTIME } else { 'win-x64' }
$Version = if ($env:VERSION) { $env:VERSION } else { '' }
$DefaultAssemblyFileVersion = '0.1.1'

function Normalize-VersionComponent {
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value)) { return '0' }
    if ($Value -notmatch '^[0-9]+$') { return '0' }
    $number = [int64]$Value
    if ($number -gt 65534) { $number = $number % 10000 }
    return "$number"
}

function Normalize-NugetVersion {
    param([string]$Raw)
    $v = $Raw -replace '^v', ''
    $plus = $v.IndexOf('+')
    if ($plus -ge 0) { $v = $v.Substring(0, $plus) }

    if ([string]::IsNullOrEmpty($v)) { return '0.1.1' }

    if ($v -match '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') { return $v }

    if ($v -match '^[0-9]+(\.[0-9]+){3}$') {
        $parts = $v.Split('.')
        return "$($parts[0]).$($parts[1]).$($parts[2])-$($parts[3])"
    }

    $suffix = $v -replace '[^0-9A-Za-z.-]', '-'
    $suffix = $suffix -replace '^\.', ''
    $suffix = $suffix -replace '^-', ''
    if ([string]::IsNullOrEmpty($suffix)) { $suffix = 'local' }
    return "0.1.1-$suffix"
}

function Build-AssemblyFileVersion {
    param([string]$Raw)
    $raw = $Raw -replace '^v', ''
    $dash = $raw.IndexOf('-')
    if ($dash -ge 0) { $raw = $raw.Substring(0, $dash) }
    $plus = $raw.IndexOf('+')
    if ($plus -ge 0) { $raw = $raw.Substring(0, $plus) }

    if ($raw -match '^[0-9]+$') {
        $numeric = [int64]$raw
        $build = $numeric % 10000
        return "0.0.$build"
    }

    if ($raw -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$') {
        return $DefaultAssemblyFileVersion
    }

    $parts = $raw.Split('.')
    $major = Normalize-VersionComponent $parts[0]
    $minor = Normalize-VersionComponent $parts[1]
    $build = Normalize-VersionComponent $parts[2]
    return "$major.$minor.$build"
}

# Runs git tolerating non-zero exits and stderr. Windows PowerShell 5.1 wraps a
# native command's redirected stderr in a terminating NativeCommandError when
# ErrorActionPreference is Stop, so lower it for the duration of the probe.
function Invoke-GitProbe {
    param([Parameter(ValueFromRemainingArguments)][string[]]$Arguments)
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'SilentlyContinue'
    try {
        $output = (& git @Arguments 2>$null)
        $code = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $previous
    }
    return [pscustomobject]@{
        Output   = (($output -join "`n").Trim())
        ExitCode = $code
    }
}

# Copies the *contents* of $Source into $Destination, merging into any existing
# tree. Enumerating files and recreating their relative paths avoids the
# Copy-Item -Recurse "copy into vs. merge" ambiguity.
function Copy-Tree {
    param([string]$Source, [string]$Destination)
    $resolvedSource = (Resolve-Path -LiteralPath $Source).Path
    if (-not (Test-Path -LiteralPath $Destination)) {
        New-Item -ItemType Directory -Path $Destination -Force | Out-Null
    }
    $resolvedDestination = (Resolve-Path -LiteralPath $Destination).Path
    Get-ChildItem -LiteralPath $resolvedSource -Recurse -File | ForEach-Object {
        $relative = $_.FullName.Substring($resolvedSource.Length).TrimStart('\', '/')
        $target = Join-Path $resolvedDestination $relative
        $targetDir = Split-Path -Parent $target
        if (-not (Test-Path -LiteralPath $targetDir)) {
            New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
        }
        Copy-Item -LiteralPath $_.FullName -Destination $target -Force
    }
}

# Copies README.md into the launcher archive with its repo-relative documentation
# links rewritten to absolute GitHub URLs. The extracted launcher zip has no Docs/
# tree beside the README, so relative links like ](Docs/Configuration.md) would
# dangle when opened from the unpacked archive. The in-repo README keeps its
# relative links (ideal for browsing on GitHub); only the packaged copy is changed.
function Copy-ReadmeWithAbsoluteDocLinks {
    param([string]$Source, [string]$Destination)
    $owner = if ($env:GITHUB_REPOSITORY) { ($env:GITHUB_REPOSITORY -split '/')[0] } else { 'viktor-ferenczi' }
    $repo = if ($env:GITHUB_REPOSITORY) { ($env:GITHUB_REPOSITORY -split '/')[1] } else { 'Quasar' }
    # Pin doc links to main: docs on main are always current and always resolve,
    # unlike a tag/PR-merge ref that may predate a doc or not be a valid blob path.
    $baseUrl = "https://github.com/$owner/$repo/blob/main/Docs/"
    # Read as UTF-8 explicitly: Windows PowerShell 5.1's default Get-Content uses the
    # ANSI codepage and would mojibake the README's UTF-8 characters (e.g. em-dashes).
    $content = Get-Content -LiteralPath $Source -Raw -Encoding UTF8
    $content = $content -replace '\]\(Docs/', "]($baseUrl"
    # Write UTF-8 without BOM to match the source README's encoding.
    [System.IO.File]::WriteAllText($Destination, $content, (New-Object System.Text.UTF8Encoding($false)))
}

function New-ZipFromDirectory {
    param([string]$SourceDir, [string]$ZipPath)
    if (-not ('System.IO.Compression.ZipFile' -as [type])) {
        Add-Type -AssemblyName System.IO.Compression.FileSystem
    }
    if (Test-Path -LiteralPath $ZipPath) {
        Remove-Item -LiteralPath $ZipPath -Force
    }
    # includeBaseDirectory = $false zips the contents of $SourceDir at the archive
    # root, matching `tar -C "$DIR" -czf archive .` on Linux.
    [System.IO.Compression.ZipFile]::CreateFromDirectory(
        (Resolve-Path -LiteralPath $SourceDir).Path,
        $ZipPath,
        [System.IO.Compression.CompressionLevel]::Optimal,
        $false)
}

if ([string]::IsNullOrWhiteSpace($Version)) {
    $described = Invoke-GitProbe -C $RepoDir describe --tags --exact-match
    if ($described.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($described.Output)) {
        $Version = $described.Output
    }
}
if ([string]::IsNullOrWhiteSpace($Version)) {
    $Version = (Invoke-GitProbe -C $RepoDir rev-parse --short HEAD).Output
}
$Version = $Version -replace '^v', ''
$NugetVersion = Normalize-NugetVersion $Version
$AssemblyFileVersion = Build-AssemblyFileVersion $Version

$PublishDir = Join-Path $ArtifactDir 'publish'
$WebDir = Join-Path $ArtifactDir 'web'
$BootstrapDir = Join-Path $ArtifactDir 'bootstrap'

if (Test-Path -LiteralPath $ArtifactDir) {
    Remove-Item -LiteralPath $ArtifactDir -Recurse -Force
}
New-Item -ItemType Directory -Path $PublishDir -Force | Out-Null
New-Item -ItemType Directory -Path $WebDir -Force | Out-Null
New-Item -ItemType Directory -Path $BootstrapDir -Force | Out-Null

Write-Host "Building Quasar.Agent ($Configuration)..."
& dotnet build (Join-Path $RepoDir 'Quasar.Agent\Quasar.Agent.csproj') `
    -c $Configuration `
    -p:Platform=x64 `
    -p:RuntimeIdentifier= `
    -p:SelfContained= `
    -p:PublishSingleFile= `
    -p:CopyToDeployDir=false `
    -v minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet build Quasar.Agent failed with exit code $LASTEXITCODE" }

Write-Host "Publishing Quasar.Bootstrap ($Configuration, $Runtime, version $NugetVersion)..."
& dotnet publish (Join-Path $RepoDir 'Quasar.Bootstrap\Quasar.Bootstrap.csproj') `
    -c $Configuration `
    -r $Runtime `
    -p:CopyToDeployDir=false `
    -p:Version=$NugetVersion `
    -p:AssemblyVersion=$AssemblyFileVersion `
    -p:FileVersion=$AssemblyFileVersion `
    -p:InformationalVersion=$NugetVersion `
    -o $PublishDir `
    -v minimal
if ($LASTEXITCODE -ne 0) { throw "dotnet publish Quasar.Bootstrap failed with exit code $LASTEXITCODE" }

# Web UI worker archive: WebService/* overlaid with the full source wwwroot.
Copy-Tree (Join-Path $PublishDir 'WebService') $WebDir
Copy-Tree (Join-Path $RepoDir 'Quasar\wwwroot') (Join-Path $WebDir 'wwwroot')

$requiredWebFiles = @(
    'Quasar.exe',
    'wwwroot',
    'wwwroot/_framework/blazor.web.js',
    'wwwroot/_content/MudBlazor/MudBlazor.min.css',
    'wwwroot/_content/MudBlazor/MudBlazor.min.js'
)
foreach ($requiredFile in $requiredWebFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $WebDir $requiredFile))) {
        throw "web release missing required file: $requiredFile"
    }
}

$webZip = Join-Path $ArtifactDir 'quasar-web-win-x64.zip'
New-ZipFromDirectory $WebDir $webZip

# Launcher archive: the stable bootstrap launcher plus config and helper scripts.
Copy-Item -LiteralPath (Join-Path $PublishDir 'Quasar.exe') -Destination (Join-Path $BootstrapDir 'Quasar.exe') -Force
Copy-Item -LiteralPath (Join-Path $RepoDir 'Quasar\appsettings.json') -Destination (Join-Path $BootstrapDir 'appsettings.json') -Force
Copy-Item -LiteralPath (Join-Path $ScriptDir 'install.ps1') -Destination (Join-Path $BootstrapDir 'install.ps1') -Force
Copy-Item -LiteralPath (Join-Path $ScriptDir 'uninstall.ps1') -Destination (Join-Path $BootstrapDir 'uninstall.ps1') -Force
Copy-ReadmeWithAbsoluteDocLinks (Join-Path $RepoDir 'README.md') (Join-Path $BootstrapDir 'README.md')

$bootstrapZip = Join-Path $ArtifactDir 'quasar-win-x64.zip'
New-ZipFromDirectory $BootstrapDir $bootstrapZip

Push-Location $ArtifactDir
try {
    $sumLines = foreach ($name in @('quasar-web-win-x64.zip', 'quasar-win-x64.zip')) {
        $hash = (Get-FileHash -LiteralPath $name -Algorithm SHA256).Hash.ToLowerInvariant()
        "$hash  $name"
    }
    Set-Content -LiteralPath 'SHA256SUMS' -Value $sumLines -Encoding ascii
}
finally {
    Pop-Location
}

Write-Host "Created Windows release artifacts in $ArtifactDir"
