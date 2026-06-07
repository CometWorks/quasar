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

$onWindows = ($PSVersionTable.PSEdition -eq 'Desktop') -or ($IsWindows -eq $true)
if (-not $onWindows) {
    Write-Error 'install.ps1 supports Windows only.'
    exit 1
}

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
        Write-Host "Publishing Quasar ($Configuration, $Runtime)..."
        & dotnet publish $bootstrapProject `
            -c $Configuration `
            -r $Runtime `
            -p:CopyToDeployDir=false `
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

# The Scheduled Task action runs through cmd.exe so the service-mode environment
# variables (mirroring install.sh's Environment= lines) are set for the worker.
$commandLine = '/c set "QUASAR_MODE=Service" & set "QUASAR_OPEN_BROWSER_ON_START=false" & "' + $exePath + '" serve --quiet'
$action = New-ScheduledTaskAction -Execute "$env:SystemRoot\System32\cmd.exe" -Argument $commandLine -WorkingDirectory $InstallDir
$trigger = New-ScheduledTaskTrigger -AtStartup
$settings = New-ScheduledTaskSettingsSet `
    -AllowStartIfOnBatteries `
    -DontStopIfGoingOnBatteries `
    -StartWhenAvailable `
    -RestartCount 3 `
    -RestartInterval (New-TimeSpan -Minutes 1) `
    -ExecutionTimeLimit ([TimeSpan]::Zero)

if ($User) {
    $principal = New-ScheduledTaskPrincipal -UserId $User -LogonType ServiceAccount -RunLevel Highest
    $runAs = $User
}
else {
    $principal = New-ScheduledTaskPrincipal -UserId 'SYSTEM' -LogonType ServiceAccount -RunLevel Highest
    $runAs = 'SYSTEM'
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

if ($NoEnable) {
    Disable-ScheduledTask -TaskName $TaskName | Out-Null
}

if ($Start) {
    Start-ScheduledTask -TaskName $TaskName
}

Write-Host @"

Installed Quasar.

Scheduled task: $TaskName
Install dir:    $InstallDir
Run as:         $runAs

The task starts at boot and restarts the launcher on failure (keep-alive).

Manage the task:
  Get-ScheduledTask -TaskName '$TaskName'
  Start-ScheduledTask -TaskName '$TaskName'
  Stop-ScheduledTask  -TaskName '$TaskName'
"@
