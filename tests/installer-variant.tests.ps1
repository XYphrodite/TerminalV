#Requires -Version 5.1
# Variant selection of install.ps1: the light package is picked only when the
# .NET desktop runtime is present. Loads only the pure selection functions,
# never the download/install/PATH code.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$installer = Join-Path (Split-Path -Parent $PSScriptRoot) 'install.ps1'
$source = Get-Content -LiteralPath $installer -Raw -Encoding UTF8
if ($source.ToCharArray() | Where-Object { [int]$_ -gt 127 }) { throw 'Installer must remain ASCII-only' }
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
$DesktopMajor = '10'
$AssetName = 'TerminalV-win-x64.zip'
$LightAssetName = 'TerminalV-win-x64-light.zip'
foreach ($name in @('Test-DesktopRuntimeLine', 'Select-TerminalVAsset')) {
    $function = $ast.Find({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true)
    if ($null -eq $function) { throw "Function $name not found in install.ps1" }
    . ([scriptblock]::Create($function.Extent.Text))
}
function Assert { param([bool] $Condition, [string] $Message) if (-not $Condition) { throw $Message } }
$passed = 0
function Check {
    param([string] $Name, [scriptblock] $Body)
    try { & $Body; Write-Host "PASS $Name" -ForegroundColor Green; $script:passed++ }
    catch { Write-Host "FAIL $Name : $_" -ForegroundColor Red; throw }
}

Check 'auto picks light with the runtime' { Assert ((Select-TerminalVAsset -Variant auto -HasRuntime $true) -eq $LightAssetName) 'not light' }
Check 'auto picks full without the runtime' { Assert ((Select-TerminalVAsset -Variant auto -HasRuntime $false) -eq $AssetName) 'not full' }
Check 'explicit full wins over the runtime' { Assert ((Select-TerminalVAsset -Variant full -HasRuntime $true) -eq $AssetName) 'not full' }
Check 'explicit light wins without the runtime' { Assert ((Select-TerminalVAsset -Variant light -HasRuntime $false) -eq $LightAssetName) 'not light' }
Check 'desktop runtime line matches exact major' {
    Assert (Test-DesktopRuntimeLine -Line 'Microsoft.WindowsDesktop.App 10.0.5 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]') 'missed 10.x'
}
Check 'core runtime line does not count' {
    Assert (-not (Test-DesktopRuntimeLine -Line 'Microsoft.NETCore.App 10.0.5 [C:\Program Files\dotnet\shared\Microsoft.NETCore.App]')) 'matched NETCore'
}
Check 'other majors do not count' {
    Assert (-not (Test-DesktopRuntimeLine -Line 'Microsoft.WindowsDesktop.App 9.0.1 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]')) 'matched 9.x'
    Assert (-not (Test-DesktopRuntimeLine -Line 'Microsoft.WindowsDesktop.App 11.0.0 [C:\Program Files\dotnet\shared\Microsoft.WindowsDesktop.App]')) 'matched 11.x'
}
Check 'empty and garbage lines do not count' {
    Assert (-not (Test-DesktopRuntimeLine -Line '')) 'matched empty'
    Assert (-not (Test-DesktopRuntimeLine -Line 'Microsoft.WindowsDesktop.App')) 'matched bare name'
}

Write-Host ''
Write-Host "Variant checks: $passed passed" -ForegroundColor Green
