#!/usr/bin/env pwsh
[CmdletBinding()]
param([string]$ServiceName='Quasar', [string]$UpdaterServiceName, [string]$TaskName, [string]$InstallDir, [switch]$Purge)
$ErrorActionPreference='Stop'
if (-not $IsWindows -and $PSVersionTable.PSEdition -ne 'Desktop') { throw 'Windows only.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this uninstaller in an elevated PowerShell.' }
if ($ServiceName -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Invalid service name.' }
if (-not $UpdaterServiceName) { $UpdaterServiceName=$ServiceName+'Updater' }
if ($UpdaterServiceName -notmatch '^[A-Za-z0-9_.-]+$' -or $UpdaterServiceName -eq $ServiceName) { throw 'Invalid updater service name.' }
if (-not $TaskName) { $TaskName=$ServiceName }
$metadata=Join-Path $env:ProgramData ('Quasar-ServiceInstallations/'+$ServiceName+'.json')
if (-not $InstallDir) {
    $InstallDir = if (Test-Path -LiteralPath $metadata) { (Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json).installDirectory }
        elseif (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Quasar.exe')) { $PSScriptRoot }
        else { Join-Path $env:ProgramFiles 'Quasar' }
}
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
if ($Purge) {
    $unsafe = @([IO.Path]::GetPathRoot($InstallDir), $env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData, $env:USERPROFILE) | Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\','/') }
    if ($unsafe -contains $InstallDir.TrimEnd('\','/')) { throw "Refusing unsafe install directory: $InstallDir" }
}
foreach ($name in @($UpdaterServiceName,$ServiceName)) {
    if (Get-Service -Name $name -ErrorAction SilentlyContinue) {
        Stop-Service -Name $name
        (Get-Service $name).WaitForStatus('Stopped',[TimeSpan]::FromSeconds(90))
        & sc.exe delete $name
        if ($LASTEXITCODE) { throw "Cannot remove service $name" }
    }
}
# Migration tasks are retained disabled until explicitly uninstalled.
if (Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue) {
    Stop-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
    Unregister-ScheduledTask -TaskName $TaskName -Confirm:$false
}
if (Test-Path -LiteralPath $metadata) { Remove-Item -LiteralPath $metadata }
if ($Purge) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
Write-Host 'Native services removed. Data is retained unless -Purge was supplied. Detached game servers require their own explicit shutdown.'
