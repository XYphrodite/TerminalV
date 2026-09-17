#Requires -Version 5.1
<#
.SYNOPSIS
    Verify a local package and its UI/update paths without using the installed TerminalV.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Package,
    [string] $ExpectedVersion
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Console in powershell.exe defaults to CP866 → Cyrillic from dotnet/node appears as ╨Ю╨┐...  Force UTF-8.
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::InputEncoding = [System.Text.Encoding]::UTF8 } catch {}
try { $OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
try { chcp 65001 >$null } catch {}
$root = Split-Path -Parent $PSScriptRoot
$zip = (Resolve-Path -LiteralPath $Package).Path
if ([string]::IsNullOrWhiteSpace($ExpectedVersion)) {
    [xml] $project = Get-Content -LiteralPath (Join-Path $root 'src\TerminalV\TerminalV.csproj') -Raw
    $ExpectedVersion = $project.SelectSingleNode('//Version').InnerText
}
$verification = Join-Path (Split-Path -Parent $zip) ('verification-' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $verification | Out-Null
$payload = Join-Path $verification 'payload'

Push-Location $root
try {
    dotnet build TerminalV.slnx -c Release 2>&1 | Tee-Object -FilePath (Join-Path $verification 'build.log')
    if ($LASTEXITCODE -ne 0) { throw 'Release build failed' }

    dotnet run --project tests/TerminalV.Package.Tests -c Release --no-build -- $zip $ExpectedVersion $payload 2>&1 |
        Tee-Object -FilePath (Join-Path $verification 'package.log')
    if ($LASTEXITCODE -ne 0) { throw 'Package/update checks failed; see package.log' }

    dotnet run --project tests/TerminalV.Pty.Tests -c Release --no-build 2>&1 |
        Tee-Object -FilePath (Join-Path $verification 'pty.log')
    if ($LASTEXITCODE -ne 0) { throw 'ConPTY checks failed' }

    dotnet run --project tests/TerminalV.Data.Tests -c Release --no-build 2>&1 |
        Tee-Object -FilePath (Join-Path $verification 'data.log')
    if ($LASTEXITCODE -ne 0) { throw 'SQLite checks failed' }

    dotnet run --project tests/TerminalV.Shell.Tests -c Release --no-build 2>&1 |
        Tee-Object -FilePath (Join-Path $verification 'shortcuts.log')
    if ($LASTEXITCODE -ne 0) { throw 'Shortcut checks failed' }

    powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer-shortcuts.tests.ps1 2>&1 |
        Tee-Object -FilePath (Join-Path $verification 'installer-shortcuts.log')
    if ($LASTEXITCODE -ne 0) { throw 'Installer shortcut checks failed' }

    $previousAppRoot = [Environment]::GetEnvironmentVariable('TERMINALV_TEST_APP_ROOT', 'Process')
    try {
        $env:TERMINALV_TEST_APP_ROOT = Join-Path $payload 'wwwroot'
        Push-Location (Join-Path $root 'ui')
        try {
            # Do not run npm pretest: the app fixtures must load the extracted archive, not a fresh UI build.
            node --test tests/*.test.js 2>&1 | Tee-Object -FilePath (Join-Path $verification 'ui.log')
            if ($LASTEXITCODE -ne 0) { throw 'Packaged UI checks failed' }
        } finally { Pop-Location }
    } finally {
        [Environment]::SetEnvironmentVariable('TERMINALV_TEST_APP_ROOT', $previousAppRoot, 'Process')
    }
    Write-Host "Verified local candidate $ExpectedVersion. Logs and extracted files: $verification" -ForegroundColor Green
    Write-Host 'No GUI, production host, installed files, user database, GitHub update or release publication was used.'
} finally { Pop-Location }
