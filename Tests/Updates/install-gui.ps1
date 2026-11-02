# Executes the production elevation encoder against a harmless script. No UAC or UI.
$ErrorActionPreference = 'Stop'
$repo = Split-Path -Parent (Split-Path -Parent $PSScriptRoot)
$tokens=$null; $errors=$null
$ast = [Management.Automation.Language.Parser]::ParseFile((Join-Path $repo 'scripts/install-gui.ps1'),[ref]$tokens,[ref]$errors)
if ($errors.Count) { throw ($errors | Out-String) }
$function = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Get-ElevatedInstallCommand'}, $true)
. ([scriptblock]::Create($function.Extent.Text))
$root = Join-Path ([IO.Path]::GetTempPath()) ("quasar gui ' "+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root | Out-Null
try {
    $script = Join-Path $root "fixture ' ; literal.ps1"
    [IO.File]::WriteAllText($script, 'param([switch]$Elevated,[string]$InstallDir,[string]$DataDir) $PSBoundParameters | ConvertTo-Json -Compress')
    $application = "C:\Quasar ' ; `$literal & spaces"
    $data = "D:\Worlds ' ; `$literal & data"
    $encoded = Get-ElevatedInstallCommand $script $application $data
    $shell = (Get-Process -Id $PID).Path
    $actual = & $shell -NoProfile -EncodedCommand $encoded | ConvertFrom-Json
    if ($LASTEXITCODE -or -not $actual.Elevated -or $actual.InstallDir -cne $application -or $actual.DataDir -cne $data) { throw 'Elevation command changed an argument or executed path text as code.' }
    Write-Host 'PASS production Windows elevation encoding with literal paths; no UAC or GUI was executed'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
