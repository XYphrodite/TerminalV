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
    # Mobile targets (android/ios/maccatalyst) require platform SDKs not present on desktop CI.
    # Verify the desktop package without forcing Mobile to build; Mobile is verified separately via Ssh/Mobile tests.
    $useSln = $true
    try {
        dotnet build TerminalV.slnx -c Release --nologo -v q 2>&1 | Tee-Object -FilePath (Join-Path $verification 'build.log') | Out-Null
        if ($LASTEXITCODE -ne 0) { throw 'sln' }
    } catch {
        $useSln = $false
        Write-Host "sln build failed (likely Mobile SDK missing) - falling back to desktop-only build" -ForegroundColor Yellow
        Get-Content -LiteralPath (Join-Path $verification 'build.log') -ErrorAction SilentlyContinue | Select-Object -Last 20 | ForEach-Object { Write-Host $_ -ForegroundColor DarkGray }
    }
    if (-not $useSln) {
        # Build only what verify-package actually exercises: desktop host + test projects.
        $desktopProjects = @(
            'src/TerminalV/TerminalV.csproj',
            'src/TerminalV.Com/TerminalV.Com.csproj',
            'src/TerminalV.Extensibility/TerminalV.Extensibility.csproj',
            'tests/TerminalV.Package.Tests/TerminalV.Package.Tests.csproj',
            'tests/TerminalV.Pty.Tests/TerminalV.Pty.Tests.csproj',
            'tests/TerminalV.Data.Tests/TerminalV.Data.Tests.csproj',
            'tests/TerminalV.Shell.Tests/TerminalV.Shell.Tests.csproj',
            'tests/TerminalV.Extensions.Tests/TerminalV.Extensions.Tests.csproj'
        )
        foreach ($p in $desktopProjects) {
            Write-Host "==> dotnet build $p -c Release" -ForegroundColor Cyan
            dotnet build $p -c Release 2>&1 | Tee-Object -FilePath (Join-Path $verification 'build.log') -Append | Out-Null
            if ($LASTEXITCODE -ne 0) { throw "Release build failed for $p" }
        }
        Write-Host "Desktop-only build succeeded (Mobile skipped)" -ForegroundColor Green
    } else {
        if ((Select-String -Path (Join-Path $verification 'build.log') -Pattern 'error (XA5300|CS5001|MSB3073)' -Quiet)) {
            throw 'Release build reported Mobile errors despite exit code 0'
        }
    }

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

    # Ensure mobile wwwroot is fresh even when Mobile project was skipped (desktop-only build)
    # Mobile wwwroot is derived from desktop wwwroot; just copy without rebuilding (desktop already built)
    Write-Host "==> Syncing mobile wwwroot for verification" -ForegroundColor Cyan
    Push-Location (Join-Path $root 'ui')
    try {
        $oldEA = $ErrorActionPreference; $ErrorActionPreference = 'Continue'
        try {
            node scripts/copy-mobile-ui.js 2>&1 | Tee-Object -FilePath (Join-Path $verification 'ui-build.log') | Out-Null
            if ($LASTEXITCODE -ne 0) { throw 'Mobile UI copy failed' }
        } finally { $ErrorActionPreference = $oldEA }
    } finally { Pop-Location }

    $previousAppRoot = [Environment]::GetEnvironmentVariable('TERMINALV_TEST_APP_ROOT', 'Process')
    try {
        $env:TERMINALV_TEST_APP_ROOT = Join-Path $payload 'wwwroot'
        Push-Location (Join-Path $root 'ui')
        try {
            # Do not run npm pretest: the app fixtures must load the extracted archive, not a fresh UI build.
            # Run sequentially to avoid headless Chrome contention (narrow-window flakiness).
            node --test --test-concurrency=1 tests/*.test.js 2>&1 | Tee-Object -FilePath (Join-Path $verification 'ui.log')
            if ($LASTEXITCODE -ne 0) { throw 'Packaged UI checks failed' }
        } finally { Pop-Location }
    } finally {
        [Environment]::SetEnvironmentVariable('TERMINALV_TEST_APP_ROOT', $previousAppRoot, 'Process')
    }
    Write-Host "Verified local candidate $ExpectedVersion. Logs and extracted files: $verification" -ForegroundColor Green
    Write-Host 'No GUI, production host, installed files, user database, GitHub update or release publication was used.'
} finally { Pop-Location }
