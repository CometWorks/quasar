# Real isolated native SCM services, virtual accounts and production updater code.
# Fixture app and sealed local package provider replace Quasar/GitHub; no live installs.
param([switch]$NestedData)
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
$data=if($NestedData){Join-Path $root 'Data'}else{$root}
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
function State{Get-Content -LiteralPath (Join-Path $data 'Updates/operation.json') -Raw|ConvertFrom-Json}
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
function Assert-InstallationAccess {
 foreach($role in @('main','updater')){
  $serviceName=if($role -eq 'main'){$name}else{$updater}
  $receipt=Get-Content -LiteralPath (Join-Path $data ("acl-$role.json")) -Raw|ConvertFrom-Json
  $service=Get-CimInstance Win32_Service -Filter "Name='$serviceName'"
  $account='NT SERVICE\'+$serviceName
  $sid=(New-Object Security.Principal.NTAccount($account)).Translate([Security.Principal.SecurityIdentifier]).Value
  if($receipt.account -ine $account -or $receipt.sid -ne $sid -or $receipt.pid -ne $service.ProcessId -or $receipt.coLocated -ne (-not $NestedData)){
   throw "Missing current real-identity ACL evidence for $serviceName"
  }
 }
 $adminSid='S-1-5-32-544';$systemSid='S-1-5-18'
 $updaterSid=(New-Object Security.Principal.NTAccount('NT SERVICE\'+$updater)).Translate([Security.Principal.SecurityIdentifier]).Value
 $mainSid=(New-Object Security.Principal.NTAccount('NT SERVICE\'+$name)).Translate([Security.Principal.SecurityIdentifier]).Value
 $metadata=Join-Path $env:ProgramData ('Quasar-ServiceInstallations/'+$name+'.json')
 foreach($entry in @(@((Join-Path $root 'Updater'),$updaterSid,'ReadAndExecute'),
                     @((Join-Path $root '.update-cache'),$updaterSid,'Modify'),
                     @((Join-Path $root 'Updater/acl-probe.txt'),$updaterSid,'ReadAndExecute'),
                     @((Join-Path $root '.update-cache/acl-probe.txt'),$updaterSid,'Modify'),
                     @($metadata,$mainSid,'Read'), @((Join-Path $root 'service-installation.json'),$mainSid,'Read'))){
  $path,$serviceSid,$rights=$entry
  $item=Get-Item -LiteralPath $path -Force
  $inheritance=if($item.PSIsContainer){[Security.AccessControl.InheritanceFlags]'ContainerInherit, ObjectInherit'}else{[Security.AccessControl.InheritanceFlags]::None}
  $acl=Get-Acl -LiteralPath $path
  $expected=@{ $adminSid='FullControl';$systemSid='FullControl';$serviceSid=$rights }
  if($path -eq $metadata -or $path -eq (Join-Path $root 'service-installation.json')){$expected[$updaterSid]='Read'}
  $rules=@($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
  if(-not $acl.AreAccessRulesProtected -or $rules.Count -ne $expected.Count){throw "Unexpected protected ACL: $path $($acl.Sddl)"}
  foreach($rule in $rules){
   $id=$rule.IdentityReference.Value
   $mask=[int]$rule.FileSystemRights -band (-bnot [int][Security.AccessControl.FileSystemRights]::Synchronize)
   if(-not $expected.ContainsKey($id) -or $rule.IsInherited -or $rule.AccessControlType -ne 'Allow' -or
      $rule.InheritanceFlags -ne $inheritance -or $rule.PropagationFlags -ne 'None' -or
      $mask -ne ([int][Security.AccessControl.FileSystemRights]$expected[$id] -band (-bnot [int][Security.AccessControl.FileSystemRights]::Synchronize))){
    throw "Unexpected protected grant: $path $($acl.Sddl)"
   }
  }
  # Recovery must work under the actual elevated administrator too; open without modifying bytes.
  if($item.PSIsContainer){
   if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $adminSid){throw "Protected tree is not owned by Administrators: $path"}
   $scratch=Join-Path $path 'acl-admin-probe.tmp';[IO.File]::WriteAllText($scratch,'recovery access');[IO.File]::Delete($scratch)
  }else{
   $stream=[IO.File]::Open($path,'Open','ReadWrite','ReadWrite, Delete');$stream.Dispose()
  }
 }
 foreach($file in @('release.json','fixture-release.txt')){
  $path=Join-Path $root $file
  $stream=[IO.File]::Open($path,'Open','ReadWrite','ReadWrite, Delete');$stream.Dispose()
  $rules=@((Get-Acl -LiteralPath $path).GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]))
  foreach($sid in @($adminSid,$systemSid,$mainSid,$updaterSid)){
   if(-not @($rules|Where-Object{$_.IdentityReference.Value -eq $sid -and -not $_.IsInherited -and $_.AccessControlType -eq 'Allow' -and $_.InheritanceFlags -eq 'None' -and $_.PropagationFlags -eq 'None'}).Count){
    throw "Missing direct file grant for $sid on $path"
   }
  }
  if($file -eq 'release.json' -and -not @($rules|Where-Object{$_.IdentityReference.Value -eq 'S-1-5-32-546' -and -not $_.IsInherited -and $_.AccessControlType -eq 'Deny'}).Count){
   throw 'Installer removed an unrelated explicit deny from the application file'
  }
 }
 Write-Host "PASS actual main/updater identity access, effective leaf grants, protected ACLs and administrator recovery access (nested data: $NestedData)"
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
 [IO.File]::WriteAllText((Join-Path $root 'Updater/acl-probe.txt'),'protected updater probe')
 New-Item -ItemType Directory -Path (Join-Path $root '.update-cache')|Out-Null
 [IO.File]::WriteAllText((Join-Path $root '.update-cache/acl-probe.txt'),'private cache probe')
 $acl=Get-Acl -LiteralPath (Join-Path $root 'release.json')
 $guests=New-Object Security.Principal.SecurityIdentifier('S-1-5-32-546')
 $acl.AddAccessRule((New-Object Security.AccessControl.FileSystemAccessRule($guests,'Write','Deny')))
 Set-Acl -LiteralPath (Join-Path $root 'release.json') -AclObject $acl
 $listener=New-Object Net.Sockets.TcpListener([Net.IPAddress]::Loopback,0);$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
 [IO.File]::WriteAllText((Join-Path $root 'appsettings.json'),('{"Quasar":{"Port":'+$port+'},"adminValue":42}'))
 # Fixture CLI locates service metadata at the install root, then loads DataDirectory.
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
 & (Join-Path $repo 'scripts/install.ps1') -RepairSupervision -InstallDir $root -DataDir $data -ServiceName $name -UpdaterServiceName $updater -MigrateScheduledTask -TaskName $task -Start
 Assert-ServiceControl
 Assert-InstallationAccess
 Wait-Until {Test-Path -LiteralPath (Join-Path $data 'server.pid')} 'Native main service did not start'
 $sentinel=[int](Get-Content -LiteralPath (Join-Path $data 'server.pid') -Raw)
 & (Join-Path $repo 'scripts/install.ps1') -RepairSupervision -InstallDir $root -DataDir $data -ServiceName $name -UpdaterServiceName $updater -MigrateScheduledTask -TaskName $task -Start
 Assert-ServiceControl
 Assert-InstallationAccess
 Assert-Sentinel
 Write-Host 'PASS native service DACL grant preserves audit rules and remains idempotent after repair'
 if((Get-ScheduledTask -TaskName $task).State -ne 'Disabled'){throw 'Legacy task was not retained disabled'}
 Wait-Until {Test-Path -LiteralPath (Join-Path $data 'Updates/migration-1.4.1.json')} 'Healthy native 1.4.1 migration receipt'
 & dotnet $dll token-create
 if($LASTEXITCODE){throw 'Credential fixture creation failed'}
 Restart-Service $updater
 Wait-Until {Test-Path -LiteralPath (Join-Path $data 'token-reader-ok')} 'Saved token/keyring must be readable by updater virtual account'
 $beforeStarts=@(Get-Content -LiteralPath (Join-Path $data 'starts.txt')).Count
 [IO.File]::WriteAllText((Join-Path $data 'fatal-background'),'fail')
 Wait-Until {@(Get-Content -LiteralPath (Join-Path $data 'starts.txt')).Count -gt $beforeStarts} 'Reported background failure must trigger SCM recovery'
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
 Wait-Until {(Phase) -eq 'rolled-back' -and -not(Test-Path -LiteralPath (Join-Path $data 'Updates/application-maintenance'))} 'Complete previous service recovery'
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
 [IO.File]::WriteAllText((Join-Path $data 'hold-health'),'1.6.0')
 Request-Package $payload|Out-Null
 Wait-Until {(Phase) -eq 'attempted' -and (State).stage -eq 'Restarting'} 'Uncommitted replacement'
 $updaterPid=(Get-CimInstance Win32_Service -Filter "Name='$updater'").ProcessId
 Stop-Process -Id $updaterPid -Force
 [IO.File]::WriteAllText((Join-Path $root 'fixture-release.txt'),'partial corruption')
 Remove-Item -LiteralPath (Join-Path $data 'hold-health')
 Wait-Until {(State).stage -eq 'Failed' -and (Phase) -eq 'rolled-back' -and -not(Test-Path -LiteralPath (Join-Path $data 'Updates/application-maintenance'))} 'Native updater restart recovery'
 if((Get-Content -LiteralPath (Join-Path $root 'fixture-release.txt') -Raw) -ne '1.5.0'){throw 'Incomplete rollback'}
 Assert-Sentinel
 Write-Host 'PASS real updater crash/restart recovers interrupted full replacement'
 # Real Windows locked-file behavior, exercised inside the actual updater service.
 $locked=[IO.File]::Open((Join-Path $root 'fixture-release.txt'),'Open','Read','Read')
 try{
  Request-Package $payload|Out-Null
  Wait-Until {(State).stage -eq 'Failed'} 'Windows locked file must fail replacement'
 }finally{$locked.Dispose()}
 Wait-Until {-not(Test-Path -LiteralPath (Join-Path $data 'Updates/application-maintenance'))} 'Locked-file recovery after handle released'
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
 Wait-Until {-not(Test-Path -LiteralPath (Join-Path $data 'Updates/application-maintenance'))} 'Recovery after ACL repair'
 Assert-Sentinel
 Write-Host 'PASS real insufficient updater permissions and recovery'
 [IO.File]::WriteAllText((Join-Path $data 'exit'),'0')
 Wait-Until {(Get-Service $name).Status -eq 'Stopped'} 'Intentional service shutdown'
 Start-Sleep -Seconds 2
 if((Get-Service $name).Status -ne 'Stopped'){throw 'Intentional shutdown restarted'}
 Assert-Sentinel
 Write-Host 'PASS intentional native shutdown remains stopped and preserves detached server sentinel'
}catch{
 $failure=$_
 Write-Warning ("Native Windows fixture failed: " + $failure.Exception.Message)
 foreach($serviceName in @($name,$updater)){
  Get-CimInstance Win32_Service -Filter "Name='$serviceName'" -ErrorAction SilentlyContinue |
   Select-Object Name,StartName,ProcessId,State,PathName | Format-List | Out-Host
 }
 foreach($path in @($root,$data,(Join-Path $root 'release.json'),(Join-Path $root 'fixture-release.txt'),
                    (Join-Path $root 'Updater'),(Join-Path $root 'Updater/acl-probe.txt'),
                    (Join-Path $root '.update-cache'),(Join-Path $root '.update-cache/acl-probe.txt'),
                    (Join-Path $root 'service-installation.json'),
                    (Join-Path $env:ProgramData ('Quasar-ServiceInstallations/'+$name+'.json')))){
  try{
   $acl=Get-Acl -LiteralPath $path
   Write-Host "ACL $path attributes=$((Get-Item -LiteralPath $path -Force).Attributes) owner=$($acl.Owner) protected=$($acl.AreAccessRulesProtected) SDDL=$($acl.Sddl)"
  }catch{Write-Warning "Cannot inspect ACL for ${path}: $($_.Exception.Message)"}
 }
 foreach($source in @($name,$updater)){
  Get-WinEvent -FilterHashtable @{LogName='Application';ProviderName=$source} -MaxEvents 8 -ErrorAction SilentlyContinue |
   Select-Object TimeCreated,ProviderName,Message | Format-List | Out-Host
 }
 throw $failure
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
