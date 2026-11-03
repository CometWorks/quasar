#!/usr/bin/env pwsh
# Installs native main/updater services. Run elevated; neither service needs a password.
[CmdletBinding()]
param(
    [string]$InstallDir,
    [string]$DataDir,
    [string]$ServiceName = 'Quasar',
    [string]$UpdaterServiceName,
    [string]$TaskName,
    [string]$Configuration = 'Release',
    [string]$Runtime = 'win-x64',
    [switch]$RepairSupervision,
    [switch]$MigrateScheduledTask,
    [switch]$NoBuild,
    [switch]$NoEnable,
    [switch]$Start
)
$ErrorActionPreference = 'Stop'
$PSNativeCommandUseErrorActionPreference = $false
function Set-InstallationAcl {
    param([Parameter(ValueFromRemainingArguments=$true)][string[]]$AclArguments)
    $output = & icacls @AclArguments 2>&1
    if ($LASTEXITCODE) { throw "Cannot apply required installation permissions to $($AclArguments[0]): $($output -join ' ')" }
    Write-Verbose ($output -join [Environment]::NewLine)
}
function Set-InstallationTreeAcl([string]$Path, [hashtable]$ServiceAccess, [switch]$Protected, [switch]$RemoveInheritedAccess, [string[]]$ExcludedPaths = @()) {
    if ($ExcludedPaths -contains [IO.Path]::GetFullPath($Path)) { return }
    $item = Get-Item -LiteralPath $Path -Force
    if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw "Installation ACL path is a reparse point: $Path" }
    $acl = if (-not $Protected) { Get-Acl -LiteralPath $Path } elseif ($item.PSIsContainer) { New-Object Security.AccessControl.DirectorySecurity } else { New-Object Security.AccessControl.FileSecurity }
    $administrators = New-Object Security.Principal.SecurityIdentifier('S-1-5-32-544')
    $system = New-Object Security.Principal.SecurityIdentifier('S-1-5-18')
    $inheritance = if ($item.PSIsContainer) { [Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit' } else { [Security.AccessControl.InheritanceFlags]::None }
    if ($Protected) { $acl.SetOwner($administrators) }
    if ($Protected -or $RemoveInheritedAccess) { $acl.SetAccessRuleProtection($true, $false) }
    $entries = @(@($administrators, 'FullControl'), @($system, 'FullControl'))
    foreach ($account in $ServiceAccess.Keys) {
        $sid = (New-Object Security.Principal.NTAccount($account)).Translate([Security.Principal.SecurityIdentifier])
        $entries += ,@($sid, $ServiceAccess[$account])
    }
    foreach ($entry in $entries) {
        $rule = New-Object Security.AccessControl.FileSystemAccessRule($entry[0], $entry[1], $inheritance, [Security.AccessControl.PropagationFlags]::None, [Security.AccessControl.AccessControlType]::Allow)
        [void]$acl.AddAccessRule($rule)
    }
    # Files need effective grants without directory inheritance flags. Publish
    # each complete DACL once, including recovery access, before visiting children.
    # Ordinary trees retain unrelated explicit rules; protected trees start empty.
    Set-Acl -LiteralPath $Path -AclObject $acl
    if ($item.PSIsContainer) {
        foreach ($child in Get-ChildItem -LiteralPath $Path -Force) {
            Set-InstallationTreeAcl $child.FullName $ServiceAccess -Protected:$Protected -RemoveInheritedAccess:$RemoveInheritedAccess -ExcludedPaths $ExcludedPaths
        }
    }
}
if (-not $IsWindows -and $PSVersionTable.PSEdition -ne 'Desktop') { throw 'Windows only.' }
$identity = [Security.Principal.WindowsIdentity]::GetCurrent()
if (-not (New-Object Security.Principal.WindowsPrincipal($identity)).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)) { throw 'Run this installer in an elevated PowerShell.' }
if ($ServiceName -notmatch '^[A-Za-z0-9_.-]+$') { throw 'Invalid service name.' }
if (-not $UpdaterServiceName) { $UpdaterServiceName = $ServiceName + 'Updater' }
if (-not $TaskName) { $TaskName = $ServiceName }
if ($UpdaterServiceName -notmatch '^[A-Za-z0-9_.-]+$' -or $UpdaterServiceName -eq $ServiceName) { throw 'Invalid updater service name.' }
$scriptDir = $PSScriptRoot
$repo = Split-Path -Parent $scriptDir
$packaged = Test-Path -LiteralPath (Join-Path $scriptDir 'Quasar.exe')
if (-not $InstallDir) { $InstallDir = if ($packaged) { $scriptDir } else { Join-Path $env:ProgramFiles 'Quasar' } }
$InstallDir = [IO.Path]::GetFullPath($InstallDir)
$existingMetadata = Join-Path $InstallDir 'service-installation.json'
$old = if (Test-Path -LiteralPath $existingMetadata) { Get-Content -LiteralPath $existingMetadata -Raw | ConvertFrom-Json } else { $null }
if ($old -and -not $old.installDirectory -and $old.metadataPath) { $old = Get-Content -LiteralPath $old.metadataPath -Raw | ConvertFrom-Json }
if ($old -and ($old.mainService -ne $ServiceName -or $old.updaterService -ne $UpdaterServiceName)) { throw 'Use the existing service names when repairing/upgrading this installation.' }
if (-not $DataDir) { $DataDir = if ($old) { $old.dataDirectory } elseif (Test-Path -LiteralPath (Join-Path $InstallDir 'appsettings.json')) { $InstallDir } else { Join-Path $InstallDir 'Data' } }
$DataDir = [IO.Path]::GetFullPath($DataDir)
$unsafeDirectories = @([IO.Path]::GetPathRoot($InstallDir), [IO.Path]::GetPathRoot($DataDir), $env:SystemRoot, $env:ProgramFiles, ${env:ProgramFiles(x86)}, $env:ProgramData, $env:USERPROFILE) |
    Where-Object { $_ } | ForEach-Object { $_.TrimEnd('\','/') }
foreach ($directory in @($InstallDir, $DataDir)) {
    if ($unsafeDirectories -contains $directory.TrimEnd('\','/')) { throw "Choose a dedicated Quasar folder, not a system or drive root: $directory" }
}
$prerequisitePackage = if ($RepairSupervision) { $InstallDir } elseif ($packaged) { $scriptDir } else { $null }
if ($prerequisitePackage) {
    & (Join-Path $prerequisitePackage 'Updater/Quasar.Updater.exe') check-upgrade-path --package $prerequisitePackage --data-dir $DataDir
    if ($LASTEXITCODE) { throw 'Upgrade prerequisite failed. Complete and start the 1.4.1 migration package before installing 1.5 or later.' }
}
$legacy = Get-ScheduledTask -TaskName $TaskName -ErrorAction SilentlyContinue
if ($legacy -and -not $MigrateScheduledTask) { throw 'An existing Scheduled Task needs -MigrateScheduledTask. Save worlds and stop that installation first; task-job migration may require a one-time game-server restart.' }
if ($legacy -and $MigrateScheduledTask) {
    $actions = @($legacy.Actions)
    if ($actions.Count -ne 1 -or [IO.Path]::GetFullPath([string]$actions[0].Execute) -ine (Join-Path $InstallDir 'Quasar.exe') -or
        [IO.Path]::GetFullPath([string]$actions[0].WorkingDirectory) -ine $InstallDir) {
        throw 'The selected legacy task does not directly run this installation. Check TaskName/InstallDir and migrate a custom wrapper explicitly.'
    }
}
if ($legacy -and $legacy.State -eq 'Running') { throw 'Stop the old Quasar task and its game servers before migration. The old task is retained until the new service passes health checks.' }
foreach ($name in @($ServiceName, $UpdaterServiceName)) {
    $existing = Get-CimInstance Win32_Service -Filter "Name='$name'"
    if ($existing -and $existing.StartName -ine ('NT SERVICE\' + $name)) { throw "Service $name uses another account. Migrate its account explicitly before using this virtual-account installer." }
    if ($existing -and $existing.State -ne 'Stopped') { Stop-Service -Name $name; (Get-Service $name).WaitForStatus('Stopped', [TimeSpan]::FromSeconds(90)) }
}
$backup = $null
$legacyEnabled = $legacy -and $legacy.State -ne 'Disabled'
if ($legacy -and $MigrateScheduledTask -and -not $Start) { throw 'Scheduled Task migration requires -Start so the replacement can be health-checked before completion.' }
if ($legacy -and $MigrateScheduledTask) { Disable-ScheduledTask -TaskName $TaskName | Out-Null }
if ((Test-Path -LiteralPath (Join-Path $InstallDir 'Quasar.exe')) -and ($legacy -or $old -or $InstallDir -ine $scriptDir)) {
    $backup = Join-Path ([IO.Path]::GetTempPath()) ('quasar-previous-installation-' + [guid]::NewGuid().ToString('N'))
    New-Item -ItemType Directory -Path $backup | Out-Null
    $oldManifest = Join-Path $InstallDir 'launcher-files.txt'
    $oldFiles = if (Test-Path -LiteralPath $oldManifest) { @(Get-Content -LiteralPath $oldManifest) } else { @('Quasar.exe','WebService','Host') }
    foreach ($file in ($oldFiles + @('launcher-files.txt','service-installation.json','Updater'))) {
        $source = [IO.Path]::GetFullPath((Join-Path $InstallDir $file))
        if (-not $source.StartsWith($InstallDir.TrimEnd('\') + '\',[StringComparison]::OrdinalIgnoreCase)) { throw 'Invalid previous package manifest.' }
        if (Test-Path -LiteralPath $source) {
            $destination = Join-Path $backup $file
            New-Item -ItemType Directory -Path (Split-Path -Parent $destination) -Force | Out-Null
            Copy-Item -LiteralPath $source -Destination $destination -Recurse
        }
    }
}
$staging = Join-Path ([IO.Path]::GetTempPath()) ('quasar-install-' + [guid]::NewGuid().ToString('N'))
try {
    if (-not $RepairSupervision) {
        New-Item -ItemType Directory -Path $staging | Out-Null
        if ($packaged -or $NoBuild) {
            $source = if ($packaged) { $scriptDir } else { Join-Path $repo "Quasar\bin\$Configuration\net10.0\$Runtime\publish" }
            $manifest = Join-Path $source 'launcher-files.txt'
            if (-not (Test-Path -LiteralPath $manifest)) { throw 'Use a complete current platform package.' }
            foreach ($name in @((Get-Content -LiteralPath $manifest)) + @('launcher-files.txt', 'Updater')) {
                $sourceFile = Join-Path $source $name
                $target = Join-Path $staging $name
                New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force | Out-Null
                Copy-Item -LiteralPath $sourceFile -Destination $target -Recurse -Force
            }
            Copy-Item -LiteralPath (Join-Path $source 'appsettings.json') -Destination $staging
        } else {
            foreach ($component in @('Quasar', 'Quasar.Updater', 'Quasar.Host')) {
                $target = if ($component -eq 'Quasar') { $staging } elseif ($component -eq 'Quasar.Updater') { Join-Path $staging 'Updater' } else { Join-Path $staging 'Host' }
                & dotnet publish (Join-Path $repo "$component/$component.csproj") -c $Configuration -r $Runtime -p:SelfContained=true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -o $target -v quiet
                if ($LASTEXITCODE) { throw "Publish failed: $component" }
            }
            $releaseVersion = & (Join-Path $staging 'Quasar.exe') --version
            if ($LASTEXITCODE) { throw 'Cannot read the published release version.' }
            @{version=$releaseVersion.Trim();updaterProtocol=1}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $staging 'release.json') -Encoding utf8
            Copy-Item -LiteralPath (Join-Path $repo 'Quasar/appsettings.json') -Destination $staging -Force
            Copy-Item -LiteralPath $PSCommandPath -Destination (Join-Path $staging 'install.ps1')
            Copy-Item -LiteralPath (Join-Path $scriptDir 'uninstall.ps1') -Destination $staging
            $files = Get-ChildItem -LiteralPath $staging -File -Recurse | ForEach-Object { $_.FullName.Substring($staging.Length).TrimStart('\','/').Replace('\','/') } | Where-Object { $_ -notlike 'Updater/*' -and $_ -notin @('appsettings.json','launcher-files.txt') }
            [IO.File]::WriteAllLines((Join-Path $staging 'launcher-files.txt'), [string[]]$files)
        }
        & (Join-Path $staging 'Updater/Quasar.Updater.exe') check-upgrade-path --package $staging --data-dir $DataDir
        if ($LASTEXITCODE) { throw 'Complete and start the 1.4.1 migration package before installing 1.5 or later.' }
        & (Join-Path $staging 'Updater/Quasar.Updater.exe') check-stopped --root $InstallDir --data-dir $DataDir
        if ($LASTEXITCODE) { throw 'Stop the existing Quasar application before copying files.' }
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
        Get-ChildItem -LiteralPath $staging | Where-Object { $_.Name -ne 'appsettings.json' } | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $InstallDir $_.Name) -Recurse -Force }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $InstallDir 'Updater/Quasar.Updater.exe'))) { throw 'Updater missing. Replace the complete platform package before repair.' }
    New-Item -ItemType Directory -Path $DataDir -Force | Out-Null
    $settings = Join-Path $DataDir 'appsettings.json'
    if (-not (Test-Path -LiteralPath $settings)) {
        $defaults = if (Test-Path -LiteralPath (Join-Path $staging 'appsettings.json')) { Join-Path $staging 'appsettings.json' } else { Join-Path $InstallDir 'appsettings.json' }
        Copy-Item -LiteralPath $defaults -Destination $settings
    }
    $mainAccount = 'NT SERVICE\' + $ServiceName
    $updaterAccount = 'NT SERVICE\' + $UpdaterServiceName
    $metadataDir = Join-Path $env:ProgramData 'Quasar-ServiceInstallations'
    New-Item -ItemType Directory -Path $metadataDir -Force | Out-Null
    $metadata = Join-Path $metadataDir ($ServiceName + '.json')
    # Register first so virtual service SIDs exist before their ACLs are granted.
    $mainCommand = '"' + (Join-Path $InstallDir 'Quasar.exe') + '" serve --service'
    $updaterCommand = '"' + (Join-Path $InstallDir 'Updater/Quasar.Updater.exe') + '" --installation "' + $metadata + '"'
    foreach ($definition in @(@($ServiceName,$mainCommand,$mainAccount), @($UpdaterServiceName,$updaterCommand,$updaterAccount))) {
        $name,$command,$account = $definition
        if (Get-Service -Name $name -ErrorAction SilentlyContinue) { & sc.exe config $name binPath= $command obj= $account start= auto | Out-Null }
        else { & sc.exe create $name binPath= $command obj= $account start= auto | Out-Null }
        if ($LASTEXITCODE) { throw "Cannot register $name" }
        & sc.exe sidtype $name unrestricted | Out-Null
        & sc.exe failure $name reset= 86400 actions= restart/5000/restart/15000/restart/60000 | Out-Null
        if ($LASTEXITCODE) { throw "Cannot configure failure recovery for $name" }
        if (-not [Diagnostics.EventLog]::SourceExists($name)) { New-EventLog -LogName Application -Source $name }
        # Graceful service stops remain stopped; only failures trigger recovery.
        & sc.exe failureflag $name 1 | Out-Null
        if ($LASTEXITCODE) { throw "Cannot configure reported-failure recovery for $name" }
    }
    Set-InstallationAcl $metadataDir /setowner '*S-1-5-32-544' | Out-Null
    Set-InstallationAcl $metadataDir /inheritance:r /remove:g '*S-1-1-0' '*S-1-5-11' '*S-1-5-32-545' /grant:r 'SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' | Out-Null
    & (Join-Path $InstallDir 'Updater/Quasar.Updater.exe') configure --install-dir $InstallDir --data-dir $DataDir --metadata $metadata --main-service $ServiceName --updater-service $UpdaterServiceName --account $mainAccount --updater-account $updaterAccount
    if ($LASTEXITCODE) { throw 'Cannot configure service installation.' }
    # Main owns its data. Updater may replace application files, but its own binary
    # and protected service metadata are never writable by the application account.
    $updaterDir = Join-Path $InstallDir 'Updater'
    # The archive checked by the updater must remain private through extraction
    # and replacement, even when legacy data lives in the application directory.
    $privateCache = Join-Path $InstallDir '.update-cache'
    # Reapplying ordinary grants must not temporarily open protected subtrees to
    # detached processes that still run as the main service account during repair.
    $protectedPaths = @($updaterDir, $privateCache, $metadata, (Join-Path $InstallDir 'service-installation.json'))
    Set-InstallationTreeAcl $DataDir @{ $mainAccount = 'Modify'; $updaterAccount = 'Modify' } -RemoveInheritedAccess -ExcludedPaths $protectedPaths
    Set-InstallationTreeAcl $InstallDir @{ $mainAccount = 'ReadAndExecute'; $updaterAccount = 'Modify' } -ExcludedPaths $protectedPaths
    Set-InstallationTreeAcl $updaterDir @{ $updaterAccount = 'ReadAndExecute' } -Protected
    New-Item -ItemType Directory -Path $privateCache -Force | Out-Null
    Set-InstallationTreeAcl $privateCache @{ $updaterAccount = 'Modify' } -Protected
    Set-InstallationAcl $metadataDir /inheritance:r /grant:r 'SYSTEM:(OI)(CI)F' 'BUILTIN\Administrators:(OI)(CI)F' | Out-Null
    Set-InstallationAcl $metadataDir /grant "${mainAccount}:RX" "${updaterAccount}:RX" | Out-Null
    Set-InstallationTreeAcl $metadata @{ $mainAccount = 'Read'; $updaterAccount = 'Read' } -Protected
    Set-InstallationTreeAcl (Join-Path $InstallDir 'service-installation.json') @{ $mainAccount = 'Read'; $updaterAccount = 'Read' } -Protected
    # Grant the updater query/start/stop rights for this one service, not SCM-wide administration.
    $sid = (New-Object Security.Principal.NTAccount($updaterAccount)).Translate([Security.Principal.SecurityIdentifier])
    $securityOutput = & sc.exe sdshow $ServiceName
    if ($LASTEXITCODE) { throw "Cannot read $ServiceName service permissions: $securityOutput" }
    $sddl = ($securityOutput | Where-Object { $_ -match '^D:' }).Trim()
    $descriptor = [Security.AccessControl.CommonSecurityDescriptor]::new($false, $false, $sddl)
    # AddAccess merges an existing grant. Appending SDDL text can put an allow ACE
    # in the SACL when sdshow includes S:, which Windows rejects as invalid.
    $descriptor.DiscretionaryAcl.AddAccess([Security.AccessControl.AccessControlType]::Allow, $sid, 0xB5,
        [Security.AccessControl.InheritanceFlags]::None, [Security.AccessControl.PropagationFlags]::None)
    $securityOutput = & sc.exe sdset $ServiceName $descriptor.GetSddlForm([Security.AccessControl.AccessControlSections]::Access)
    if ($LASTEXITCODE) { throw "Cannot grant updater service-control permissions: $securityOutput" }
    if ($NoEnable) { Set-Service -Name $ServiceName -StartupType Manual; Set-Service -Name $UpdaterServiceName -StartupType Manual }
    if ($Start) {
        Start-Service -Name $UpdaterServiceName
        Start-Service -Name $ServiceName
        $installationSettings = Get-Content -LiteralPath $metadata -Raw | ConvertFrom-Json
        $deadline = [DateTime]::UtcNow.AddSeconds(90)
        $releaseMetadata = Join-Path $InstallDir 'release.json'
        $expectedVersion = if (Test-Path -LiteralPath $releaseMetadata) { (Get-Content -LiteralPath $releaseMetadata -Raw | ConvertFrom-Json).version } else { $null }
        $healthy = $false
        do {
            try {
                $response = Invoke-RestMethod ($installationSettings.healthUrl + '/api/internal/update-health') -Headers @{'X-Quasar-Launcher-Token'=$installationSettings.healthToken} -TimeoutSec 2
                $service = Get-CimInstance Win32_Service -Filter "Name='$ServiceName'"
                $healthy = $response.pid -eq $service.ProcessId -and $service.State -eq 'Running' -and (-not $expectedVersion -or $response.version -eq $expectedVersion)
            } catch { }
            if (-not $healthy) { Start-Sleep -Milliseconds 500 }
        } while (-not $healthy -and [DateTime]::UtcNow -lt $deadline)
        if (-not $healthy) { throw 'The new main service failed health checks. The legacy Scheduled Task has been retained; inspect service logs before retrying migration.' }
        if ($legacy -and $MigrateScheduledTask) { Disable-ScheduledTask -TaskName $TaskName | Out-Null }
    }
    Write-Host "Installed native services $ServiceName and $UpdaterServiceName. Data: $DataDir. Use Quasar.exe update --status for supervision and update status."
} catch {
    $failure = $_
    $restoreSucceeded = $true
    Write-Warning ("Installation failed before recovery: " + $failure.Exception.Message)
    foreach ($name in @($UpdaterServiceName,$ServiceName)) {
        $service = Get-Service -Name $name -ErrorAction SilentlyContinue
        if ($service) {
            try {
                Stop-Service -Name $name -ErrorAction Stop
                $service.WaitForStatus('Stopped',[TimeSpan]::FromSeconds(90))
                Set-Service -Name $name -StartupType Manual
            } catch {
                $restoreSucceeded = $false
                Write-Warning ("Cannot stop/disable " + $name + " for recovery: " + $_.Exception.Message)
            }
        }
    }
    if ($backup -and $restoreSucceeded) {
        try {
            Get-ChildItem -LiteralPath $backup | ForEach-Object { Copy-Item -LiteralPath $_.FullName -Destination (Join-Path $InstallDir $_.Name) -Recurse -Force }
            if (-not (Test-Path -LiteralPath (Join-Path $backup 'service-installation.json'))) {
                Remove-Item -LiteralPath (Join-Path $InstallDir 'service-installation.json') -ErrorAction SilentlyContinue
            }
            Write-Warning "Previous complete application restored. Retained recovery copy: $backup"
        } catch {
            $restoreSucceeded = $false
            Write-Warning ("Automatic restoration is incomplete. Retained recovery copy: " + $backup + ". " + $_.Exception.Message)
        }
    }
    if ($legacyEnabled -and $restoreSucceeded) {
        if (Test-Path -LiteralPath (Join-Path $DataDir 'Updates/bootstrap-update-request.json')) {
            Write-Warning 'The old task remains disabled because a legacy activation request could not be safely quarantined. Inspect it before manually starting the old installation.'
        } else { Enable-ScheduledTask -TaskName $TaskName | Out-Null }
    }
    if (-not $restoreSucceeded) { Write-Warning "Keep the legacy task disabled and inspect the retained recovery copy before restarting. Original failure follows." }
    throw $failure
} finally {
    if (Test-Path -LiteralPath $staging) { Remove-Item -LiteralPath $staging -Recurse -Force }
}
