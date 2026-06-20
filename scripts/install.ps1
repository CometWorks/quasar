#!/usr/bin/env pwsh
# Windows analogue of install.sh.
#
# Installs Quasar and registers a Scheduled Task that keeps the launcher running
# (Quasar.exe serve --quiet) at boot, restarting it on failure. This mirrors the
# documented Windows runtime model: foreground console worker supervised by a
# Scheduled Task keep-alive (no Windows Service).

[CmdletBinding()]
param(
    [string]$InstallDir = "$env:ProgramFiles\Quasar",
    [string]$TaskName = 'Quasar',
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [string]$User,
    [switch]$NoEnable,
    [switch]$Start,
    [switch]$NoBuild
)

$ErrorActionPreference = 'Stop'
$ScriptDir = $PSScriptRoot
$RepoDir = Split-Path -Parent $ScriptDir
$RequiredDotNetMajor = 10

function Fail-DotNetRuntimePrerequisite {
    param([string[]]$ExtraLines = @())

    [Console]::Error.WriteLine(".NET $RequiredDotNetMajor runtime is required before installing Quasar.")
    foreach ($line in $ExtraLines) {
        [Console]::Error.WriteLine($line)
    }
    [Console]::Error.WriteLine("Install it first: https://dotnet.microsoft.com/download/dotnet/$RequiredDotNetMajor.0")
    exit 1
}

function Assert-DotNetRuntime {
    $dotnet = Get-Command dotnet -ErrorAction SilentlyContinue
    if (-not $dotnet) {
        Fail-DotNetRuntimePrerequisite
    }

    $runtimes = @(& dotnet --list-runtimes 2>$null)
    if ($LASTEXITCODE -ne 0) {
        Fail-DotNetRuntimePrerequisite @('Could not list installed .NET runtimes.')
    }

    $hasRequiredRuntime = $runtimes | Where-Object { $_ -match "^Microsoft\.NETCore\.App\s+$RequiredDotNetMajor\." } | Select-Object -First 1
    if (-not $hasRequiredRuntime) {
        $details = @('Installed .NET runtimes:')
        if ($runtimes.Count -gt 0) {
            $details += ($runtimes | ForEach-Object { "  $_" })
        }
        else {
            $details += '  (none reported)'
        }
        Fail-DotNetRuntimePrerequisite $details
    }
}

$onWindows = ($PSVersionTable.PSEdition -eq 'Desktop') -or ($IsWindows -eq $true)
if (-not $onWindows) {
    Write-Error 'install.ps1 supports Windows only.'
    exit 1
}

Assert-DotNetRuntime

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principalCheck = New-Object System.Security.Principal.WindowsPrincipal($identity)
if (-not $principalCheck.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'Run from an elevated PowerShell (Administrator) to register the scheduled task.'
    exit 1
}

$localExe = Join-Path $ScriptDir 'Quasar.exe'
$bootstrapProject = Join-Path $RepoDir 'Quasar.Bootstrap\Quasar.Bootstrap.csproj'

$skipBuild = $NoBuild.IsPresent
if (-not $skipBuild -and (Test-Path -LiteralPath $localExe) -and -not (Test-Path -LiteralPath $bootstrapProject)) {
    # Running next to an extracted release zip: install those binaries directly.
    $skipBuild = $true
}

function Normalize-VersionComponent {
    param([string]$Value)
    if ([string]::IsNullOrEmpty($Value) -or $Value -notmatch '^[0-9]+$') { return '0' }
    $number = [int64]$Value
    if ($number -gt 65534) { $number = $number % 10000 }
    return "$number"
}

function Normalize-NugetVersion {
    param([string]$Raw)
    $version = $Raw -replace '^v', ''
    $plus = $version.IndexOf('+')
    if ($plus -ge 0) { $version = $version.Substring(0, $plus) }
    if ([string]::IsNullOrWhiteSpace($version)) { return '1.0.0' }
    if ($version -match '^[0-9]+\.[0-9]+\.[0-9]+(-[0-9A-Za-z][0-9A-Za-z.-]*)?$') { return $version }

    $suffix = $version -replace '[^0-9A-Za-z.-]', '-'
    $suffix = $suffix -replace '^\.', ''
    $suffix = $suffix -replace '^-', ''
    if ([string]::IsNullOrWhiteSpace($suffix)) { $suffix = 'local' }
    return "1.0.0-$suffix"
}

function Build-AssemblyFileVersion {
    param([string]$Raw)
    $rawVersion = $Raw -replace '^v', ''
    $dash = $rawVersion.IndexOf('-')
    if ($dash -ge 0) { $rawVersion = $rawVersion.Substring(0, $dash) }
    $plus = $rawVersion.IndexOf('+')
    if ($plus -ge 0) { $rawVersion = $rawVersion.Substring(0, $plus) }
    if ($rawVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(\.[0-9]+)?$') { return '1.0.0' }

    $parts = $rawVersion.Split('.')
    return "$(Normalize-VersionComponent $parts[0]).$(Normalize-VersionComponent $parts[1]).$(Normalize-VersionComponent $parts[2])"
}

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
        Output = (($output -join "`n").Trim())
        ExitCode = $code
    }
}

function Resolve-BuildVersion {
    if ($env:VERSION) { return $env:VERSION }

    $described = Invoke-GitProbe -C $RepoDir describe --tags --exact-match
    if ($described.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($described.Output)) {
        return $described.Output
    }

    $short = Invoke-GitProbe -C $RepoDir rev-parse --short HEAD
    if ($short.ExitCode -eq 0 -and -not [string]::IsNullOrWhiteSpace($short.Output)) {
        return $short.Output
    }

    return '1.0.0-local'
}

# Stage everything into a temp directory first so an in-place install (InstallDir
# overlapping the source) can never delete its own inputs mid-copy.
$staging = Join-Path ([System.IO.Path]::GetTempPath()) ('quasar-publish-' + [Guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Force -Path $staging | Out-Null
try {
    if ($skipBuild) {
        if (Test-Path -LiteralPath $localExe) {
            $sourceDir = $ScriptDir
        }
        else {
            $sourceDir = Join-Path $RepoDir "Quasar.Bootstrap\bin\$Configuration\net10.0\$Runtime\publish"
        }
        if (-not (Test-Path -LiteralPath (Join-Path $sourceDir 'Quasar.exe'))) {
            Write-Error "Existing publish output not found: $sourceDir"
            exit 1
        }
        Get-ChildItem -LiteralPath $sourceDir -Force |
            Where-Object { $_.Name -notlike 'quasar-*.zip' -and $_.Name -notlike 'quasar-*.tar.gz' } |
            ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $staging $_.Name) -Recurse -Force }
    }
    else {
        $buildVersion = Resolve-BuildVersion
        $nugetVersion = Normalize-NugetVersion $buildVersion
        $assemblyFileVersion = Build-AssemblyFileVersion $buildVersion
        Write-Host "Publishing Quasar ($Configuration, $Runtime, version $nugetVersion)..."
        & dotnet publish $bootstrapProject `
            -c $Configuration `
            -r $Runtime `
            -p:Version=$nugetVersion `
            -p:AssemblyVersion=$assemblyFileVersion `
            -p:FileVersion=$assemblyFileVersion `
            -p:InformationalVersion=$nugetVersion `
            -o $staging `
            -v minimal
        if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed with exit code $LASTEXITCODE" }
    }

    if (-not (Test-Path -LiteralPath (Join-Path $staging 'Quasar.exe'))) {
        Write-Error 'Publish output is missing Quasar.exe.'
        exit 1
    }

    Write-Host "Installing Quasar to $InstallDir..."
    New-Item -ItemType Directory -Force -Path $InstallDir | Out-Null
    Get-ChildItem -LiteralPath $InstallDir -Force | Remove-Item -Recurse -Force
    Get-ChildItem -LiteralPath $staging -Force |
        ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $InstallDir $_.Name) -Recurse -Force }
}
finally {
    Remove-Item -Recurse -Force -LiteralPath $staging -ErrorAction SilentlyContinue
}

$exePath = Join-Path $InstallDir 'Quasar.exe'

# Run Bootstrap directly so that Task Scheduler's job object covers the Quasar
# process itself. The --service flag tells Bootstrap it is running under a
# supervisor (no external release-pointer allowed, browser auto-open suppressed
# for the worker). Using cmd.exe as a wrapper would orphan Bootstrap when the
# task is stopped, because Task Scheduler tracks cmd.exe, not its children.
$action = New-ScheduledTaskAction -Execute $exePath -Argument 'serve --quiet --service' -WorkingDirectory $InstallDir
$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -MultipleInstances IgnoreNew `
    -RestartCount 10 `
    -RestartInterval (New-TimeSpan -Minutes 5) `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

if ($User) {
    $principal = New-ScheduledTaskPrincipal -UserId $User -LogonType ServiceAccount -RunLevel Highest
    $runAs = $User
}
else {
    $currentUser = "$env:USERDOMAIN\$env:USERNAME"
    $principal = New-ScheduledTaskPrincipal -UserId $currentUser -LogonType S4U -RunLevel Highest
    $runAs = $currentUser
}

Write-Host "Registering scheduled task '$TaskName'..."
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}
Register-ScheduledTask `
    -TaskName $TaskName `
    -Action $action `
    -Trigger $trigger `
    -Settings $settings `
    -Principal $principal `
    -Description 'Quasar Space Engineers supervisor' | Out-Null

# New-ScheduledTaskSettingsSet targets Windows 7 (schema v1.2) by default.
# Patch the task XML to v1.4 so Task Scheduler shows "Windows 10" compatibility.
$taskXml = (Export-ScheduledTask -TaskName $TaskName) -replace 'version="1\.2"', 'version="1.4"'
Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
Register-ScheduledTask -TaskName $TaskName -Xml $taskXml -Force | Out-Null

if ($NoEnable) {
    Disable-ScheduledTask -TaskName $TaskName | Out-Null
}

if ($Start) {
    Start-ScheduledTask -TaskName $TaskName
}

# Resolve the UI URL to print. The service runs with
# QUASAR_OPEN_BROWSER_ON_START=false (no auto-open), so this printed URL is the
# operator's only pointer to the web UI. Read the configured port from the
# installed appsettings.json, falling back to the shipped default (8080).
$uiPort = 8080
$installedAppSettings = Join-Path $InstallDir 'appsettings.json'
if (Test-Path -LiteralPath $installedAppSettings) {
    try {
        $cfg = Get-Content -LiteralPath $installedAppSettings -Raw | ConvertFrom-Json
        if ($cfg.Quasar -and $cfg.Quasar.Port) { $uiPort = [int]$cfg.Quasar.Port }
    }
    catch {
        # Keep the default port if appsettings.json cannot be parsed.
    }
}
$uiUrl = "http://localhost:$uiPort"

$startHint = if ($Start) {
    'The task has been started.'
}
else {
    "Start it now with: Start-ScheduledTask -TaskName '$TaskName'  (it also starts at next boot)."
}

Write-Host @"

Installed Quasar.

Scheduled task: $TaskName
Install dir:    $InstallDir
Run as:         $runAs
Web UI:         $uiUrl

$startHint
The task starts at boot and restarts the launcher on failure (keep-alive). On
first start the launcher downloads the Quasar web UI from GitHub and then serves
it at $uiUrl.

Manage the task:
  Get-ScheduledTask -TaskName '$TaskName'
  Start-ScheduledTask -TaskName '$TaskName'
  Stop-ScheduledTask  -TaskName '$TaskName'

Documentation:
  https://github.com/viktor-ferenczi/Quasar/blob/main/Docs/WindowsDeploymentAndUpdates.md
"@
