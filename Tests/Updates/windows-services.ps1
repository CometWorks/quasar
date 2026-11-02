# Real isolated native SCM services, virtual accounts and production updater code.
# Fixture app and sealed local package provider replace Quasar/GitHub; no live installs.
$ErrorActionPreference='Stop'
$PSNativeCommandUseErrorActionPreference=$false
$repo=Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
& dotnet build (Join-Path $PSScriptRoot 'UpdateIntegration.csproj') -v quiet
if($LASTEXITCODE){throw 'Fixture build failed'}
$bin=Join-Path $PSScriptRoot 'bin/Debug/net10.0'
$dll=Join-Path $bin 'Quasar.UpdateIntegration.dll'
& dotnet $dll self-test
if($LASTEXITCODE){throw 'Shared tests failed'}
$root=Join-Path ([IO.Path]::GetTempPath()) ('quasar native services '+[guid]::NewGuid().ToString('N'))
$name='QuasarUpdateFixture'+[guid]::NewGuid().ToString('N')
$updater=$name+'Updater'
$task=$name+'Legacy'
$previous=$env:QUASAR_INSTALL_DIR
$sentinel=$null
function Wait-Until([scriptblock]$Predicate,[string]$Message,[int]$Seconds=100){
 $deadline=[DateTime]::UtcNow.AddSeconds($Seconds)
 do{try{if(& $Predicate){return}}catch{};Start-Sleep -Milliseconds 100}while([DateTime]::UtcNow -lt $deadline)
 throw $Message
}
function State{Get-Content -LiteralPath (Join-Path $root 'Updates/operation.json') -Raw|ConvertFrom-Json}
function Phase{(Get-Content -LiteralPath (Join-Path $root 'Updates/LauncherTransaction/phase') -Raw).Trim()}
function Request-Package([string]$Payload){
 & dotnet $dll seal $Payload
 if($LASTEXITCODE){throw 'Sealing failed'}
 $id=& dotnet $dll request $Payload
 if($LASTEXITCODE){throw 'Request failed'}
 return $id
}
function Assert-Sentinel{if(-not(Get-Process -Id $sentinel -ErrorAction SilentlyContinue)){throw 'Detached server-process sentinel died'}}
function Service-Security {
 $output=& sc.exe sdshow $name
 if($LASTEXITCODE){throw "Cannot read fixture service permissions: $output"}
 return [Security.AccessControl.CommonSecurityDescriptor]::new($false,$false,($output|Where-Object{$_ -match '^D:'}).Trim())
}
function Assert-ServiceControl {
 $security=Service-Security
 if($security.GetSddlForm([Security.AccessControl.AccessControlSections]::Audit) -ne $originalAudit){throw 'Installer changed service audit rules'}
 $sid=(New-Object Security.Principal.NTAccount('NT SERVICE\'+$updater)).Translate([Security.Principal.SecurityIdentifier])
 $grants=@($security.DiscretionaryAcl|Where-Object{$_.SecurityIdentifier -eq $sid -and $_.AceQualifier -eq [Security.AccessControl.AceQualifier]::AccessAllowed})
 if($grants.Count -ne 1 -or $grants[0].AccessMask -ne 0xB5){throw 'Updater must have exactly one query/start/stop/interrogate grant, without administrative rights'}
}
try{
 New-Item -ItemType Directory -Path $root|Out-Null
 Get-ChildItem -LiteralPath $bin|Copy-Item -Destination $root -Recurse
 Copy-Item -LiteralPath (Join-Path $bin 'Quasar.UpdateIntegration.exe') -Destination (Join-Path $root 'Quasar.exe')
 Copy-Item -LiteralPath $bin -Destination (Join-Path $root 'Updater') -Recurse
 Copy-Item -LiteralPath (Join-Path $bin 'Quasar.UpdateIntegration.exe') -Destination (Join-Path $root 'Updater/Quasar.Updater.exe')
 [IO.File]::WriteAllText((Join-Path $root 'fixture-release.txt'),'1.4.1')
 [IO.File]::WriteAllText((Join-Path $root 'release.json'),'{"version":"1.4.1","updaterProtocol":1}')
 $files=Get-ChildItem -LiteralPath $root -File -Recurse|ForEach-Object{$_.FullName.Substring($root.Length).TrimStart('\','/').Replace('\','/')}|Where-Object{$_ -notlike 'Updater/*'}
 [IO.File]::WriteAllLines((Join-Path $root 'launcher-files.txt'),[string[]]$files)
 $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
 [IO.File]::WriteAllText((Join-Path $root 'appsettings.json'),('{"Quasar":{"Port":'+$port+'},"adminValue":42}'))
 $env:QUASAR_INSTALL_DIR=$root
 # A real retained Scheduled Task exercises explicit migration and account transition.
 $action=New-ScheduledTaskAction -Execute (Join-Path $root 'Quasar.exe') -Argument 'serve --service' -WorkingDirectory $root
 Register-ScheduledTask -TaskName $task -Action $action -Settings (New-ScheduledTaskSettingsSet) |Out-Null
 Disable-ScheduledTask -TaskName $task|Out-Null
 # A real service descriptor includes an audit SACL after its access DACL.
 # Keep those audit rules intact while granting the updater control rights.
 & sc.exe create $name binPath= ('"'+(Join-Path $root 'Quasar.exe')+'" serve --service') obj= ('NT SERVICE\'+$name) start= demand|Out-Null
 if($LASTEXITCODE){throw 'Cannot create fixture service'}
 $originalAudit=(Service-Security).GetSddlForm([Security.AccessControl.AccessControlSections]::Audit)
 if(-not $originalAudit){throw 'Fixture requires native service audit rules'}
 & (Join-Path $repo 'scripts/install.ps1') -RepairSupervision -InstallDir $root -ServiceName $name -UpdaterServiceName $updater -MigrateScheduledTask -TaskName $task -Start
 Assert-ServiceControl
 Wait-Until {Test-Path -LiteralPath (Join-Path $root 'server.pid')} 'Native main service did not start'
 $sentinel=[int](Get-Content -LiteralPath (Join-Path $root 'server.pid') -Raw)
 & (Join-Path $repo 'scripts/install.ps1') -RepairSupervision -InstallDir $root -ServiceName $name -UpdaterServiceName $updater -MigrateScheduledTask -TaskName $task -Start
 Assert-ServiceControl
 Assert-Sentinel
 Write-Host 'PASS native service DACL grant preserves audit rules and remains idempotent after repair'
 if((Get-ScheduledTask -TaskName $task).State -ne 'Disabled'){throw 'Legacy task was not retained disabled'}
 Wait-Until {Test-Path -LiteralPath (Join-Path $root 'Updates/migration-1.4.1.json')} 'Healthy native 1.4.1 migration receipt'
 & dotnet $dll token-create
 if($LASTEXITCODE){throw 'Credential fixture creation failed'}
 Restart-Service $updater
 Wait-Until {Test-Path -LiteralPath (Join-Path $root 'token-reader-ok')} 'Saved token/keyring must be readable by updater virtual account'
 $beforeStarts=@(Get-Content -LiteralPath (Join-Path $root 'starts.txt')).Count
 [IO.File]::WriteAllText((Join-Path $root 'fatal-background'),'fail')
 Wait-Until {@(Get-Content -LiteralPath (Join-Path $root 'starts.txt')).Count -gt $beforeStarts} 'Reported background failure must trigger SCM recovery'
 Assert-Sentinel
 Write-Host 'PASS virtual-account saved token/keyring sharing and reported-failure SCM recovery'
 $payload=Join-Path $root 'fixture-payload';New-Item -ItemType Directory -Path $payload|Out-Null
 foreach($file in @(Get-Content -LiteralPath (Join-Path $root 'launcher-files.txt'))+@('launcher-files.txt')){
  $target=Join-Path $payload $file;New-Item -ItemType Directory -Path (Split-Path -Parent $target) -Force|Out-Null
  Copy-Item -LiteralPath (Join-Path $root $file) -Destination $target
 }
 # This test provider is local; allow the updater to read these administrator-created fixture files.
 $updaterAccount='NT SERVICE\'+$updater
 & icacls $payload /grant "${updaterAccount}:(OI)(CI)RX" /T /C|Out-Null
 [IO.File]::WriteAllText((Join-Path $payload 'fixture-release.txt'),'1.5.0')
 $original=[IO.File]::ReadAllBytes((Join-Path $payload 'Quasar.exe'))
 [IO.File]::WriteAllText((Join-Path $payload 'Quasar.exe'),'invalid executable')
 $id=Request-Package $payload
 if((Request-Package $payload) -ne $id){throw 'Duplicate operation created another activation'}
 Wait-Until {(State).stage -eq 'Failed'} 'Failed service launch did not record failure'
 Wait-Until {(Phase) -eq 'rolled-back' -and -not(Test-Path -LiteralPath (Join-Path $root 'Updates/application-maintenance'))} 'Complete previous service recovery'
 Assert-Sentinel
 Write-Host 'PASS real virtual-account services, legacy-task migration, duplicate requests and invalid-executable rollback'
 [IO.File]::WriteAllBytes((Join-Path $payload 'Quasar.exe'),$original)
 Request-Package $payload|Out-Null
 # Run the production CLI from the application being replaced, retaining its real image mappings.
 & (Join-Path $root 'Quasar.exe') update --version 1.5.0
 if($LASTEXITCODE){throw 'CLI did not confirm the healthy requested service release'}
 Wait-Until {(State).stage -eq 'Healthy' -and (State).version -eq '1.5.0'} 'Exact requested native service health'
 Assert-Sentinel
 Write-Host 'PASS external updater replaces stopped main files and acknowledges exact healthy release; sentinel survives'
 [IO.File]::WriteAllText((Join-Path $payload 'fixture-release.txt'),'1.6.0')
 [IO.File]::WriteAllText((Join-Path $root 'hold-health'),'1.6.0')
 Request-Package $payload|Out-Null
 Wait-Until {(Phase) -eq 'attempted' -and (State).stage -eq 'Restarting'} 'Uncommitted replacement'
 $updaterPid=(Get-CimInstance Win32_Service -Filter "Name='$updater'").ProcessId
 Stop-Process -Id $updaterPid -Force
 [IO.File]::WriteAllText((Join-Path $root 'fixture-release.txt'),'partial corruption')
 Remove-Item -LiteralPath (Join-Path $root 'hold-health')
 Wait-Until {(State).stage -eq 'Failed' -and (Phase) -eq 'rolled-back' -and -not(Test-Path -LiteralPath (Join-Path $root 'Updates/application-maintenance'))} 'Native updater restart recovery'
 if((Get-Content -LiteralPath (Join-Path $root 'fixture-release.txt') -Raw) -ne '1.5.0'){throw 'Incomplete rollback'}
 Assert-Sentinel
 Write-Host 'PASS real updater crash/restart recovers interrupted full replacement'
 # Real Windows locked-file behavior, exercised inside the actual updater service.
 $locked=[IO.File]::Open((Join-Path $root 'fixture-release.txt'),'Open','Read','Read')
 try{
  Request-Package $payload|Out-Null
  Wait-Until {(State).stage -eq 'Failed'} 'Windows locked file must fail replacement'
 }finally{$locked.Dispose()}
 Wait-Until {-not(Test-Path -LiteralPath (Join-Path $root 'Updates/application-maintenance'))} 'Locked-file recovery after handle released'
 Assert-Sentinel
 Write-Host 'PASS real Windows file locking and deferred complete recovery'
 # Deny destination writes specifically to the updater's virtual account.
 $acl=Get-Acl -LiteralPath $root
 $rule=New-Object Security.AccessControl.FileSystemAccessRule($updaterAccount,'Write','None','None','Deny')
 $acl.AddAccessRule($rule);Set-Acl -LiteralPath $root -AclObject $acl
 try{
  Request-Package $payload|Out-Null
  Wait-Until {(State).stage -eq 'Failed'} 'Updater permissions must be enforced'
 }finally{$acl.RemoveAccessRuleSpecific($rule);Set-Acl -LiteralPath $root -AclObject $acl}
 Wait-Until {-not(Test-Path -LiteralPath (Join-Path $root 'Updates/application-maintenance'))} 'Recovery after ACL repair'
 Assert-Sentinel
 Write-Host 'PASS real insufficient updater permissions and recovery'
 [IO.File]::WriteAllText((Join-Path $root 'exit'),'0')
 Wait-Until {(Get-Service $name).Status -eq 'Stopped'} 'Intentional service shutdown'
 Start-Sleep -Seconds 2
 if((Get-Service $name).Status -ne 'Stopped'){throw 'Intentional shutdown restarted'}
 Assert-Sentinel
 Write-Host 'PASS intentional native shutdown remains stopped and preserves detached server sentinel'
}catch{
 Write-Warning ("Native Windows fixture failed: " + $_.Exception.Message)
 foreach($source in @($name,$updater)){
  Get-WinEvent -FilterHashtable @{LogName='Application';ProviderName=$source} -MaxEvents 8 -ErrorAction SilentlyContinue |
   Select-Object TimeCreated,ProviderName,Message | Format-List | Out-Host
 }
 throw
}finally{
 foreach($service in @($updater,$name)){
  if(Get-Service -Name $service -ErrorAction SilentlyContinue){Stop-Service $service -ErrorAction SilentlyContinue;& sc.exe delete $service|Out-Null}
 }
 if(Get-ScheduledTask -TaskName $task -ErrorAction SilentlyContinue){Unregister-ScheduledTask -TaskName $task -Confirm:$false}
 if($sentinel){Stop-Process -Id $sentinel -Force -ErrorAction SilentlyContinue}
 Remove-Item -LiteralPath (Join-Path $env:ProgramData ('Quasar-ServiceInstallations/'+$name+'.json')) -ErrorAction SilentlyContinue
 $env:QUASAR_INSTALL_DIR=$previous
 Remove-Item -LiteralPath $root -Recurse -Force -ErrorAction SilentlyContinue
}
