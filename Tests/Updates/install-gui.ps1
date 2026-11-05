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
    [IO.File]::WriteAllText($script, 'param([switch]$Elevated,[string]$InstallDir,[string]$DataDir,[string]$ResultFile) $PSBoundParameters | ConvertTo-Json -Compress')
    $application = "C:\Quasar ' ; `$literal & spaces"
    $data = "D:\Worlds ' ; `$literal & data"
    $resultFile = Join-Path $root "result ' ; literal.txt"
    $encoded = Get-ElevatedInstallCommand $script $application $data $resultFile
    $shell = (Get-Process -Id $PID).Path
    $actual = & $shell -NoProfile -EncodedCommand $encoded | ConvertFrom-Json
    if ($LASTEXITCODE -or -not $actual.Elevated -or $actual.InstallDir -cne $application -or $actual.DataDir -cne $data -or $actual.ResultFile -cne $resultFile) { throw 'Elevation command changed an argument or executed path text as code.' }
    Write-Host 'PASS production Windows elevation encoding with literal paths; no UAC or GUI was executed'
    $browser = $ast.Find({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq 'Open-QuasarBrowser'}, $true)
    . ([scriptblock]::Create($browser.Extent.Text))
    function Start-Process([string]$FilePath) { $script:opened = $FilePath }
    Open-QuasarBrowser 'http://127.0.0.1:18967/setup'
    if ($opened -ne 'http://127.0.0.1:18967/setup') { throw 'Browser launch lost the configured address.' }
    foreach ($unsafe in @('file:///C:/Windows/notepad.exe', 'javascript:alert(1)', 'not a URL')) {
        $script:opened = $null
        try { Open-QuasarBrowser $unsafe; throw 'Unsafe browser address accepted.' } catch {
            if ($_.Exception.Message -ne 'Invalid Quasar address.') { throw }
        }
        if ($opened) { throw 'Invalid address reached the browser launcher.' }
    }
    Write-Host 'PASS configured browser address and scheme validation; no browser was opened'
} finally { Remove-Item -LiteralPath $root -Recurse -Force }
