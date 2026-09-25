#Requires -Version 5.1
# Exercise installer functions with private sentinel files, never the live host,
# installed application, user database, network, shortcuts or PATH.
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$installer = Join-Path (Split-Path -Parent $PSScriptRoot) 'install.ps1'
$source = Get-Content -LiteralPath $installer -Raw -Encoding UTF8
if ($source.ToCharArray() | Where-Object { [int]$_ -gt 127 }) { throw 'Installer must remain ASCII-only' }
$tokens = $null
$parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseInput($source, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors.Count) { throw ($parseErrors | Out-String) }
foreach ($statement in $ast.EndBlock.Statements) {
    if ($statement -is [Management.Automation.Language.FunctionDefinitionAst]) {
        . ([scriptblock]::Create($statement.Extent.Text))
    }
}
$VariantMarker = '.terminalv-variant'
function Write-Step { param($Message) }
function Assert { param([bool] $Condition, [string] $Message) if (-not $Condition) { throw $Message } }
function Assert-Throws {
    param([scriptblock] $Body)
    $threw = $false
    try { & $Body | Out-Null } catch { $threw = $true }
    Assert $threw 'Expected operation to fail'
}
function Write-Sentinel {
    param([string] $Root, [string] $Relative, [string] $Value)
    $path = Join-Path $Root $Relative
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path)) | Out-Null
    [IO.File]::WriteAllText($path, $Value)
}
function Read-Sentinel {
    param([string] $Root, [string] $Relative)
    return [IO.File]::ReadAllText((Join-Path $Root $Relative))
}
function Get-Inventory {
    param([string] $Root)
    return (@(Get-ChildItem -LiteralPath $Root -Force -Recurse | Sort-Object FullName | ForEach-Object {
        $relative = $_.FullName.Substring($Root.Length)
        if ($_.PSIsContainer) { "D $relative" }
        else { "F $relative $((Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash)" }
    }) -join "`n")
}
function New-Fixture {
    $caseRoot = Join-Path $testRoot ([guid]::NewGuid().ToString('N'))
    $install = Join-Path $caseRoot ("install [test] & user's " + [char]0x0416)
    $payload = Join-Path $caseRoot 'payload'
    foreach ($root in @($install, $payload)) { [IO.Directory]::CreateDirectory($root) | Out-Null }
    Write-Sentinel $install 'TerminalV.exe' 'old full exe'
    Write-Sentinel $install 'TerminalV.com' 'old shim'
    Write-Sentinel $install 'wwwroot\index.html' 'old page'
    Write-Sentinel $install 'wwwroot\assets\old.js' 'old asset'
    Write-Sentinel $install '.terminalv-variant' 'full'
    Write-Sentinel $install 'TerminalV.exe.WebView2\cache' 'browser data'
    Write-Sentinel $install 'custom\keep.txt' 'user file'
    Write-Sentinel $payload 'TerminalV.exe' 'new light exe'
    Write-Sentinel $payload 'TerminalV.com' 'new shim'
    Write-Sentinel $payload 'wwwroot\index.html' 'new page'
    Write-Sentinel $payload 'wwwroot\assets\new.js' 'new asset'
    Write-Sentinel $payload 'new-runtime.dll' 'new dependency'
    return [pscustomobject]@{ Install = $install; Payload = $payload }
}
function Assert-Restored {
    param($Fixture)
    Assert ((Read-Sentinel $Fixture.Install 'TerminalV.exe') -eq 'old full exe') 'Old executable not restored'
    Assert ((Read-Sentinel $Fixture.Install 'TerminalV.com') -eq 'old shim') 'Old shim not restored'
    Assert ((Read-Sentinel $Fixture.Install 'wwwroot\index.html') -eq 'old page') 'Old page not restored'
    Assert ((Read-Sentinel $Fixture.Install 'wwwroot\assets\old.js') -eq 'old asset') 'Old assets not restored'
    Assert (-not (Test-Path -LiteralPath (Join-Path $Fixture.Install 'wwwroot\assets\new.js'))) 'New assets survived rollback'
    Assert (-not (Test-Path -LiteralPath (Join-Path $Fixture.Install 'new-runtime.dll'))) 'New dependency survived rollback'
    Assert ((Read-Sentinel $Fixture.Install '.terminalv-variant') -eq 'full') 'Variant changed after failure'
    Assert ((Read-Sentinel $Fixture.Install 'custom\keep.txt') -eq 'user file') 'User files changed'
    Assert ((Read-Sentinel $Fixture.Install 'TerminalV.exe.WebView2\cache') -eq 'browser data') 'WebView2 data changed'
}
$testRoot = Join-Path ([IO.Path]::GetTempPath()) ('terminalv-installer-host-tests-' + [guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($testRoot) | Out-Null
$passed = 0
function Check {
    param([string] $Name, [scriptblock] $Body)
    & $Body
    $script:passed++
    Write-Host "PASS $Name"
}
try {
    Check 'process selection distinguishes hosts, desktops and other installations' {
        $install = Join-Path $testRoot 'target'
        $exe = Join-Path $install 'TerminalV.exe'
        $other = Join-Path $testRoot 'other\TerminalV.exe'
        $processes = @(
            [pscustomobject]@{ ProcessId = 11; ExecutablePath = $exe; CommandLine = ('"' + $exe + '" --host') },
            [pscustomobject]@{ ProcessId = 12; ExecutablePath = $exe.ToUpperInvariant(); CommandLine = ('"' + $exe + '" --HOST') },
            [pscustomobject]@{ ProcessId = 13; ExecutablePath = $exe; CommandLine = ('"' + $exe + '"') },
            [pscustomobject]@{ ProcessId = 14; ExecutablePath = $other; CommandLine = ('"' + $other + '"') },
            [pscustomobject]@{ ProcessId = 15; ExecutablePath = $exe; CommandLine = ('"' + $exe + '" --hosted') },
            [pscustomobject]@{ ProcessId = 16; ExecutablePath = $exe; CommandLine = ('"' + $exe + '" update --host') }
        )
        $selected = @(Select-TerminalVInstallProcesses -InstallDir $install -Processes $processes)
        Assert ($selected.Count -eq 5) 'Wrong install filtering'
        Assert (@($selected | Where-Object { $_.IsHost }).Count -eq 2) 'Wrong host classification'
        Assert (@($selected | Where-Object { $_.Id -eq 11 -and $_.IsHost }).Count -eq 1) 'Quoted host missed'
        Assert (@($selected | Where-Object { $_.Id -eq 12 -and $_.IsHost }).Count -eq 1) 'Case-insensitive host missed'
        Assert (@($selected | Where-Object { $_.Id -eq 14 }).Count -eq 0) 'Other installation blocks update'
    }
    Check 'unknown process path and missing command line conservatively block' {
        $install = Join-Path $testRoot 'target'
        $exe = Join-Path $install 'TerminalV.exe'
        $processes = @(
            [pscustomobject]@{ ProcessId = 20; ExecutablePath = $null; CommandLine = ('"' + $exe + '" --host') },
            [pscustomobject]@{ ProcessId = 21; ExecutablePath = $exe; CommandLine = $null }
        )
        $selected = @(Select-TerminalVInstallProcesses -InstallDir $install -Processes $processes)
        Assert ($selected.Count -eq 2) 'Unknown process silently ignored'
        Assert (@($selected | Where-Object { $_.IsHost }).Count -eq 0) 'Unverified process treated as host'
    }
    Check 'empty process list permits installation' {
        Assert (@(Select-TerminalVInstallProcesses -InstallDir $testRoot -Processes @()).Count -eq 0) 'Empty list misclassified'
    }
    Check 'full to light preserves unknown files and retains old executable backup' {
        $fixture = New-Fixture
        $script:probeCount = 0
        $backup = Install-TerminalVPayload -PayloadDir $fixture.Payload -InstallDir $fixture.Install -Variant light -Probe {
            param($exe)
            $script:probeCount++
            Assert ($exe -eq (Join-Path $fixture.Install 'TerminalV.exe')) 'Probe used wrong executable'
            Assert ([IO.File]::ReadAllText($exe) -eq 'new light exe') 'Probe did not see replacement'
            Assert ((Read-Sentinel $fixture.Install '.terminalv-variant') -eq 'full') 'Variant committed before probe'
        }
        Assert ($script:probeCount -eq 1) 'Expected one installed probe'
        Assert ((Read-Sentinel $fixture.Install 'TerminalV.exe') -eq 'new light exe') 'New executable missing'
        Assert ((Read-Sentinel $fixture.Install 'TerminalV.com') -eq 'new shim') 'New shim missing'
        Assert ((Read-Sentinel $fixture.Install 'wwwroot\index.html') -eq 'new page') 'New UI missing'
        Assert ((Read-Sentinel $fixture.Install 'new-runtime.dll') -eq 'new dependency') 'New dependency missing'
        Assert ((Read-Sentinel $fixture.Install '.terminalv-variant').Trim() -eq 'light') 'Variant not committed'
        Assert ((Read-Sentinel $fixture.Install 'custom\keep.txt') -eq 'user file') 'Unknown files removed'
        Assert ((Read-Sentinel $fixture.Install 'TerminalV.exe.WebView2\cache') -eq 'browser data') 'WebView2 data removed'
        Assert ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($backup)) -eq $fixture.Install) 'Backup is outside install directory'
        Assert ([IO.Path]::GetFileName($backup) -match '^\.terminalv-backup-[a-f0-9]{32}$') 'Unexpected backup name'
        Assert ((Read-Sentinel $backup 'TerminalV.exe') -eq 'old full exe') 'Old host executable backup missing'
    }
    Check 'replacing a mapped executable preserves its running process' {
        $fixture = New-Fixture
        $exe = Join-Path $fixture.Install 'TerminalV.exe'
        $commandProcessor = Join-Path ([Environment]::GetFolderPath([Environment+SpecialFolder]::System)) 'cmd.exe'
        [IO.File]::Copy($commandProcessor, $exe, $true)
        $oldHash = (Get-FileHash -LiteralPath $exe -Algorithm SHA256).Hash
        $info = New-Object Diagnostics.ProcessStartInfo
        $info.FileName = $exe
        $info.Arguments = '/d /q'
        $info.WorkingDirectory = $fixture.Install
        $info.UseShellExecute = $false
        $info.CreateNoWindow = $true
        $info.RedirectStandardInput = $true
        $info.RedirectStandardOutput = $true
        $info.RedirectStandardError = $true
        $ownedProcess = [Diagnostics.Process]::Start($info)
        try {
            $output = $ownedProcess.StandardOutput.ReadToEndAsync()
            $errors = $ownedProcess.StandardError.ReadToEndAsync()
            Assert (-not $ownedProcess.WaitForExit(150)) 'Owned command processor exited before replacement'
            $ownedPid = $ownedProcess.Id
            $backup = Install-TerminalVPayload -PayloadDir $fixture.Payload -InstallDir $fixture.Install -Variant light -Probe {
                param($installedExe)
                Assert ([IO.File]::ReadAllText($installedExe) -eq 'new light exe') 'Replacement was not installed'
            }
            Assert (-not $ownedProcess.HasExited -and $ownedProcess.Id -eq $ownedPid) 'Replacing executable terminated its process'
            Assert ((Read-Sentinel $fixture.Install 'TerminalV.exe') -eq 'new light exe') 'New executable content missing'
            Assert ((Get-FileHash -LiteralPath (Join-Path $backup 'TerminalV.exe') -Algorithm SHA256).Hash -eq $oldHash) 'Mapped executable backup was not retained'
        } finally {
            try {
                if (-not $ownedProcess.HasExited) {
                    try {
                        # cmd.exe waits on its redirected stdin; no child process is started.
                        $ownedProcess.StandardInput.WriteLine('exit')
                        $ownedProcess.StandardInput.Flush()
                        $ownedProcess.StandardInput.Close()
                    } catch {}
                    if (-not $ownedProcess.WaitForExit(5000)) {
                        # Only the exact process this test created, never a process-name lookup.
                        $ownedProcess.Kill()
                        $ownedProcess.WaitForExit()
                    }
                }
            } finally { $ownedProcess.Dispose() }
        }
    }
    Check 'probe failure restores prior files and variant' {
        $fixture = New-Fixture
        $script:probeCount = 0
        Assert-Throws {
            Install-TerminalVPayload -PayloadDir $fixture.Payload -InstallDir $fixture.Install -Variant light -Probe {
                param($exe)
                $script:probeCount++
                throw 'Injected installed probe failure'
            }
        }
        Assert ($script:probeCount -eq 1) 'Probe failure was not reached'
        Assert-Restored $fixture
    }
    Check 'locked variant marker failure restores installed payload' {
        $fixture = New-Fixture
        $marker = Join-Path $fixture.Install '.terminalv-variant'
        $lock = [IO.File]::Open($marker, [IO.FileMode]::Open, [IO.FileAccess]::Read, [IO.FileShare]::None)
        try {
            Assert-Throws {
                Install-TerminalVPayload -PayloadDir $fixture.Payload -InstallDir $fixture.Install -Variant light -Probe { param($exe) }
            }
        } finally { $lock.Dispose() }
        Assert-Restored $fixture
    }
    foreach ($required in @('TerminalV.exe', 'TerminalV.com', 'wwwroot\index.html')) {
        Check "missing $required leaves installation untouched" {
            $fixture = New-Fixture
            [IO.File]::Delete((Join-Path $fixture.Payload $required))
            $before = Get-Inventory $fixture.Install
            $script:probeCount = 0
            Assert-Throws {
                Install-TerminalVPayload -PayloadDir $fixture.Payload -InstallDir $fixture.Install -Variant light -Probe { param($exe) $script:probeCount++ }
            }
            Assert ($script:probeCount -eq 0) 'Malformed payload was probed'
            Assert ((Get-Inventory $fixture.Install) -ceq $before) 'Malformed payload mutated installation'
        }
    }
    Check 'pending update leaves installation untouched' {
        $fixture = New-Fixture
        Write-Sentinel $fixture.Install '.terminalv-pending-ui' 'existing pending update'
        $before = Get-Inventory $fixture.Install
        $script:probeCount = 0
        Assert-Throws {
            Install-TerminalVPayload -PayloadDir $fixture.Payload -InstallDir $fixture.Install -Variant light -Probe { param($exe) $script:probeCount++ }
        }
        Assert ($script:probeCount -eq 0) 'Pending update was overwritten before probe'
        Assert ((Get-Inventory $fixture.Install) -ceq $before) 'Pending update changed installation'
    }
    Write-Host "Installer running-host checks: $passed passed. No real sessions or installed files touched."
} finally {
    $resolved = [IO.Path]::GetFullPath($testRoot)
    $tempParent = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd('\') + '\'
    if (-not $resolved.StartsWith($tempParent, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($resolved) -notmatch '^terminalv-installer-host-tests-[a-f0-9]{32}$') { throw 'Unsafe cleanup path' }
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
