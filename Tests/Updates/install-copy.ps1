# Exercise production copying without registering services or starting Quasar.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tokens = $null; $errors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'scripts/install.ps1'), [ref]$tokens, [ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$function = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Copy-InstallationItem'}, $true)
. ([scriptblock]::Create($function.Extent.Text))
$root = Join-Path ([IO.Path]::GetTempPath()) ("quasar copy ' " + [guid]::NewGuid().ToString('N'))
$package = Join-Path $root 'package'
$installed = Join-Path $root 'installed'
$backup = Join-Path $root 'backup'
try {
    foreach ($folder in @('Host/nested', 'Agent', 'Updater', 'wwwroot/empty')) {
        New-Item -ItemType Directory -Path (Join-Path $package $folder) -Force | Out-Null
    }
    foreach ($file in @('Host/nested/payload.txt', 'Agent/payload.txt', 'Updater/payload.txt', 'wwwroot/.hidden')) {
        [IO.File]::WriteAllText((Join-Path $package $file), 'old')
    }
    Copy-InstallationItem $package $installed
    Copy-InstallationItem $installed $backup
    [IO.File]::WriteAllText((Join-Path $installed 'appsettings.json'), '{"adminValue":42}')
    [IO.File]::WriteAllText((Join-Path $package 'Host/nested/payload.txt'), 'new')
    Copy-InstallationItem $package $installed
    Copy-InstallationItem $package $installed
    if ([IO.File]::ReadAllText((Join-Path $installed 'Host/nested/payload.txt')) -ne 'new') { throw 'Reinstall did not replace the existing file.' }
    Copy-InstallationItem $backup $installed
    foreach ($file in @('Host/nested/payload.txt', 'Agent/payload.txt', 'Updater/payload.txt', 'wwwroot/.hidden')) {
        if ([IO.File]::ReadAllText((Join-Path $installed $file)) -ne 'old') { throw "Recovery did not restore $file" }
    }
    if (-not (Test-Path -LiteralPath (Join-Path $installed 'wwwroot/empty') -PathType Container)) { throw 'Empty directory lost.' }
    foreach ($folder in @('Host/Host', 'Agent/Agent', 'Updater/Updater', 'wwwroot/wwwroot')) {
        if (Test-Path -LiteralPath (Join-Path $installed $folder)) { throw "Nested reinstall/recovery folder: $folder" }
    }
    if ([IO.File]::ReadAllText((Join-Path $installed 'appsettings.json')) -ne '{"adminValue":42}') { throw 'Unrelated administrator data changed.' }
    $conflict = Join-Path $installed 'conflict'
    New-Item -ItemType Directory -Path $conflict | Out-Null
    try {
        Copy-InstallationItem (Join-Path $package 'Agent/payload.txt') $conflict
        throw 'File/directory conflict accepted.'
    } catch {
        if ($_.Exception.Message -notlike 'Installation file/directory conflict:*') { throw }
    }
    Write-Host 'PASS repeated install and recovery merge exact paths, retain hidden/empty entries and preserve administrator data'
} finally {
    if (Test-Path -LiteralPath $root) { Remove-Item -LiteralPath $root -Recurse -Force }
}
