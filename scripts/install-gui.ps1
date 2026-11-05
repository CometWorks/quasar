#!/usr/bin/env pwsh
# A graphical entry point only; install.ps1 owns files, migration and services.
[CmdletBinding()]
param([string]$InstallDir, [string]$DataDir, [switch]$Elevated, [switch]$Manual, [string]$ResultFile)
function Get-ElevatedInstallCommand([string]$Script, [string]$ApplicationPath, [string]$DataPath, [string]$ResultPath) {
    function Literal([string]$Value) { return "'" + $Value.Replace("'", "''") + "'" }
    $command = '& ' + (Literal $Script) + ' -Elevated -InstallDir ' + (Literal $ApplicationPath) + ' -DataDir ' + (Literal $DataPath) + ' -ResultFile ' + (Literal $ResultPath)
    return [Convert]::ToBase64String([Text.Encoding]::Unicode.GetBytes($command))
}
function Open-QuasarBrowser([string]$Url) {
    $uri = $null
    if (-not [Uri]::TryCreate($Url, [UriKind]::Absolute, [ref]$uri) -or $uri.Scheme -notin @('http','https')) { throw 'Invalid Quasar address.' }
    Start-Process -FilePath $uri.AbsoluteUri
}
$ErrorActionPreference = 'Stop'
if (-not $IsWindows -and $PSVersionTable.PSEdition -ne 'Desktop') { throw 'Windows only.' }
Add-Type -AssemblyName System.Windows.Forms
[Windows.Forms.Application]::EnableVisualStyles()
$backend = Join-Path $PSScriptRoot 'install.ps1'
$metadata = Join-Path $env:ProgramData 'Quasar-ServiceInstallations/Quasar.json'
# Protected metadata is read only after UAC. A filtered administrator token (or
# a standard desktop user) cannot read the service accounts' private record.
$existingDetected = $null -ne (Get-Service -Name Quasar -ErrorAction SilentlyContinue)
$existing = $null
if (-not $InstallDir) { $InstallDir = Join-Path $env:ProgramFiles 'Quasar' }
if (-not $DataDir) { $DataDir = Join-Path $env:ProgramData 'Quasar' }

function Show-Manual {
    [void][Windows.Forms.MessageBox]::Show("From an elevated PowerShell in this package:`r`n`r`n.\install.ps1 -InstallDir '<application folder>' -DataDir '<data folder>' -Start`r`n`r`nCustom service names, existing named accounts and Scheduled Task migration use the options in INSTALL.md. For a portable foreground session, run .\Quasar.exe serve --foreground.", 'Quasar manual installation', 'OK', 'Information')
}
if ($Manual) { Show-Manual; exit 0 }
try {
    if (-not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'launcher-files.txt')) -or
        -not (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'Updater/Quasar.Updater.exe'))) {
        throw 'Extract the complete Windows platform package first, then open Install.cmd from its Quasar folder.'
    }
    if ($Elevated) {
        $identity = [Security.Principal.WindowsIdentity]::GetCurrent()
        if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Administrator approval is required to install Windows services.' }
        if (Test-Path -LiteralPath $metadata) { $existing = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json }
        $arguments = @{ InstallDir=$InstallDir; DataDir=$DataDir; Start=$true }
        if ($existing) {
            $arguments.InstallDir = $existing.installDirectory
            $arguments.DataDir = $existing.dataDirectory
            $arguments.ServiceName = $existing.mainService
            $arguments.UpdaterServiceName = $existing.updaterService
        }
        & $backend @arguments
        # Only the address crosses back to the desktop process, never the health token.
        $installed = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
        [IO.File]::WriteAllText($ResultFile, $installed.healthUrl)
        exit 0
    }
    $form = New-Object Windows.Forms.Form
    $form.Text = 'Install Quasar'
    $form.ClientSize = New-Object Drawing.Size(570,290)
    $form.StartPosition = 'CenterScreen'
    $form.FormBorderStyle = 'FixedDialog'
    $form.MaximizeBox = $false
    $form.MinimizeBox = $false
    $label = New-Object Windows.Forms.Label
    $label.Text = "Install Quasar and its updater as Windows services. No account password is needed. Windows will request administrator approval. Existing installations keep their paths and service names."
    $label.SetBounds(20,15,530,58)
    $form.Controls.Add($label)
    $boxes = @()
    foreach ($field in @(@('Application folder',$InstallDir), @('Data folder (worlds and settings)',$DataDir))) {
        $y = 80 + $boxes.Count * 62
        $caption = New-Object Windows.Forms.Label
        $caption.Text = $field[0]; $caption.SetBounds(20,$y,530,20); $form.Controls.Add($caption)
        $box = New-Object Windows.Forms.TextBox
        $box.Text = if ($existingDetected) { 'Existing installation (read after administrator approval)' } else { $field[1] }; $box.SetBounds(20,($y+22),530,25); $box.ReadOnly = $existingDetected
        $form.Controls.Add($box); $boxes += $box
    }
    $install = New-Object Windows.Forms.Button
    $install.Text = 'Install'; $install.SetBounds(205,235,105,32); $install.DialogResult = 'OK'
    $manualButton = New-Object Windows.Forms.Button
    $manualButton.Text = 'Manual install'; $manualButton.SetBounds(320,235,115,32); $manualButton.DialogResult = 'Retry'
    $cancel = New-Object Windows.Forms.Button
    $cancel.Text = 'Cancel'; $cancel.SetBounds(445,235,105,32); $cancel.DialogResult = 'Cancel'
    $form.Controls.AddRange(@($install,$manualButton,$cancel))
    $form.AcceptButton = $install; $form.CancelButton = $cancel
    $result = $form.ShowDialog()
    if ($result -eq 'Retry') { Show-Manual; exit 0 }
    if ($result -ne 'OK') { exit 0 }
    if (-not $existingDetected) {
        $InstallDir = [IO.Path]::GetFullPath($boxes[0].Text)
        $DataDir = [IO.Path]::GetFullPath($boxes[1].Text)
    }
    # Encode PowerShell literals, not command-line interpolation. Apostrophes and
    # shell metacharacters in user-selected paths remain data through UAC.
    $resultPath = [IO.Path]::GetTempFileName()
    try {
        $encoded = Get-ElevatedInstallCommand $PSCommandPath $InstallDir $DataDir $resultPath
        $powershell = Join-Path $env:SystemRoot 'System32/WindowsPowerShell/v1.0/powershell.exe'
        $process = Start-Process -FilePath $powershell -ArgumentList @('-NoProfile','-ExecutionPolicy','Bypass','-EncodedCommand',$encoded) -Verb RunAs -Wait -PassThru
        if ($process.ExitCode -ne 0) { exit $process.ExitCode }
        $message = 'Quasar and its updater are installed and running. Keep this package until you have checked your installation.'
        try {
            $url = [IO.File]::ReadAllText($resultPath).Trim()
            # Launch as the original desktop user, after the elevated installer exits.
            Open-QuasarBrowser $url
            $message += "`r`n`r`nOpening Quasar in your browser: $url"
        } catch {
            $message += "`r`n`r`nCould not open the browser. Open the configured Quasar address manually ($url)."
        }
        [void][Windows.Forms.MessageBox]::Show($message, 'Quasar installed', 'OK', 'Information')
        exit 0
    } finally { Remove-Item -LiteralPath $resultPath -Force -ErrorAction SilentlyContinue }
} catch {
    if ($_.Exception.NativeErrorCode -eq 1223) { exit 0 } # UAC cancelled, no installation.
    [void][Windows.Forms.MessageBox]::Show($_.Exception.Message + "`r`n`r`nUse Manual install for custom names, accounts or legacy task migration. Run install.ps1 from an elevated PowerShell to retain full diagnostic output.", 'Quasar installation failed', 'OK', 'Error')
    exit 1
}
