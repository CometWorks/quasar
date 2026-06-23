#!/usr/bin/env pwsh
# Windows analogue of uninstall.sh.
#
# Stops and removes the Quasar Scheduled Task registered by install.ps1. Runtime
# and config data under the install directory is left untouched. Pass -Purge to
# also delete the install directory.

[CmdletBinding()]
param(
    [string]$TaskName = 'Quasar',
    [string]$InstallDir = $PSScriptRoot,
    [switch]$Purge
)

$ErrorActionPreference = 'Stop'

$onWindows = ($PSVersionTable.PSEdition -eq 'Desktop') -or ($IsWindows -eq $true)
if (-not $onWindows) {
    Write-Error 'uninstall.ps1 supports Windows only.'
    exit 1
}

$identity = [System.Security.Principal.WindowsIdentity]::GetCurrent()
$principalCheck = New-Object System.Security.Principal.WindowsPrincipal($identity)
if (-not $principalCheck.IsInRole([System.Security.Principal.WindowsBuiltInRole]::Administrator)) {
    Write-Error 'Run from an elevated PowerShell (Administrator) to remove the scheduled task.'
    exit 1
}

$task = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($task) {
    if ($task.State -eq 'Running') {
        Write-Host "Stopping scheduled task '$TaskName'..."
        Stop-ScheduledTask -TaskName $TaskName
    }
    Write-Host "Removing scheduled task '$TaskName'..."
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}
else {
    Write-Host "Scheduled task '$TaskName' not found; nothing to remove."
}

if ($Purge) {
    $normalized = $InstallDir.TrimEnd('\', '/')
    $unsafe = @(
        $env:SystemDrive + '\',
        $env:SystemRoot,
        $env:ProgramFiles,
        ${env:ProgramFiles(x86)},
        $env:ProgramData,
        $env:windir
    ) | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\', '/') }

    if ([string]::IsNullOrWhiteSpace($normalized) -or ($unsafe -contains $normalized)) {
        Write-Error "Refusing unsafe install directory: $InstallDir"
        exit 1
    }

    if (Test-Path -LiteralPath $InstallDir) {
        Write-Host "Removing $InstallDir..."
        Remove-Item -LiteralPath $InstallDir -Recurse -Force
    }
}

Write-Host ''
Write-Host "Uninstalled Quasar scheduled task: $TaskName"
if ($Purge) {
    Write-Host "Removed install dir: $InstallDir"
}
else {
    Write-Host "Install dir left in place: $InstallDir"
    Write-Host 'Remove binaries too:  ./uninstall.ps1 -Purge'
}
