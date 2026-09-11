<#
.SYNOPSIS
    Installs TerminalV - a Windows terminal with vertical tabs.

.DESCRIPTION
    Downloads TerminalV-win-x64.zip from GitHub Releases (the public download
    URLs, not the REST API), verifies the SHA-256 asset, extracts it and adds
    the install directory to the user PATH.

    This file is intentionally ASCII-only: Windows PowerShell 5.1 reads a BOM-less
    script as ANSI, while `irm | iex` chokes on a leading BOM. ASCII keeps both
    paths working.

.EXAMPLE
    irm https://raw.githubusercontent.com/XYphrodite/TerminalV/main/install.ps1 | iex

.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/XYphrodite/TerminalV/main/install.ps1))) -InstallDir 'D:\TerminalV'
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\TerminalV'),

    [string] $Version = 'latest',

    [switch] $NoPath,

    [switch] $NoShortcut
)

$ErrorActionPreference = 'Stop'
$Repository = 'XYphrodite/TerminalV'
$AssetName = 'TerminalV-win-x64.zip'
$UserAgent = @{ 'User-Agent' = 'terminalv-installer' }

$previousProgress = $ProgressPreference
$ProgressPreference = 'SilentlyContinue'

try {
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
} catch {
    Write-Verbose "TLS 1.2 already enabled: $_"
}

function Write-Step {
    param([string] $Message)
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Get-ReleaseTag {
    param([string] $Requested)

    if ($Requested -ne 'latest') {
        if ($Requested -notmatch '^v') { return "v$Requested" }
        return $Requested
    }

    $latestUrl = "https://github.com/$Repository/releases/latest"
    try {
        $null = Invoke-WebRequest -Uri $latestUrl -MaximumRedirection 0 -UseBasicParsing -Headers $UserAgent
    } catch {
        $response = $_.Exception.Response
        if ($response -and $response.Headers['Location']) {
            $location = [string]$response.Headers['Location']
            if ($location -match '/releases/tag/(v?[A-Za-z0-9._-]+)') {
                $tag = $Matches[1]
                if ($tag -notmatch '^v') { $tag = "v$tag" }
                return $tag
            }
        }
    }

    return 'latest'
}

try {
    $tag = Get-ReleaseTag -Requested $Version
    if ($tag -eq 'latest') {
        $zipUrl = "https://github.com/$Repository/releases/latest/download/$AssetName"
        $shaUrl = "https://github.com/$Repository/releases/latest/download/$AssetName.sha256"
    } else {
        $zipUrl = "https://github.com/$Repository/releases/download/$tag/$AssetName"
        $shaUrl = "https://github.com/$Repository/releases/download/$tag/$AssetName.sha256"
    }

    Write-Step "downloading $AssetName $tag"
    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("terminalv-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    $tempZip = Join-Path $tempRoot $AssetName
    Invoke-WebRequest -Uri $zipUrl -OutFile $tempZip -UseBasicParsing -Headers $UserAgent

    $expected = $null
    try {
        $shaBody = (Invoke-WebRequest -Uri $shaUrl -UseBasicParsing -Headers $UserAgent).Content
        if ($shaBody -match '([0-9a-fA-F]{64})') {
            $expected = $Matches[1].ToLowerInvariant()
        }
    } catch {
        Write-Warning "checksum asset missing: $($_.Exception.Message)"
    }

    if ($expected) {
        $actual = (Get-FileHash -Path $tempZip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
            throw "SHA-256 mismatch. Expected $expected, got $actual"
        }
        Write-Step 'SHA-256 verified'
    } else {
        Write-Warning 'release has no SHA-256 asset, skipping checksum verification'
    }

    $running = Get-Process -Name 'TerminalV' -ErrorAction SilentlyContinue
    if ($running) {
        Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
        throw "TerminalV is running (pid $($running.Id -join ', ')). Close it and run the installer again."
    }

    if (-not (Test-Path -LiteralPath $InstallDir)) {
        New-Item -ItemType Directory -Path $InstallDir -Force | Out-Null
    } else {
        Get-ChildItem -LiteralPath $InstallDir -Force | Where-Object { $_.Name -ne 'TerminalV.exe.WebView2' } | ForEach-Object {
            Remove-Item -LiteralPath $_.FullName -Recurse -Force -ErrorAction SilentlyContinue
        }
    }

    Write-Step "installing to $InstallDir"
    Expand-Archive -LiteralPath $tempZip -DestinationPath $InstallDir -Force
    Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue

    $exe = Join-Path $InstallDir 'TerminalV.exe'
    if (-not (Test-Path -LiteralPath $exe)) {
        $nested = Get-ChildItem -LiteralPath $InstallDir -Filter 'TerminalV.exe' -Recurse | Select-Object -First 1
        if ($nested) { $exe = $nested.FullName } else { throw "TerminalV.exe was not found after extract" }
    }

    if (-not $NoPath) {
        $separator = [IO.Path]::PathSeparator
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if (-not $userPath) { $userPath = '' }
        $entries = @($userPath -split $separator | Where-Object { $_ -ne '' })
        $normalized = $InstallDir.TrimEnd([IO.Path]::DirectorySeparatorChar)
        $alreadyThere = $entries | Where-Object { $_.TrimEnd([IO.Path]::DirectorySeparatorChar) -ieq $normalized }

        if (-not $alreadyThere) {
            $newPath = ($entries + $InstallDir) -join $separator
            [Environment]::SetEnvironmentVariable('Path', $newPath, 'User')
            Write-Step 'install directory added to the user PATH'
            Write-Host '    new terminal windows will find TerminalV right away' -ForegroundColor DarkGray
        }

        if (@($env:Path -split [regex]::Escape($separator)) -notcontains $InstallDir) {
            $env:Path = $env:Path + $separator + $InstallDir
        }
    }

    if (-not $NoShortcut) {
        $programs = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs'
        $lnkPath = Join-Path $programs 'TerminalV.lnk'
        $wshell = New-Object -ComObject WScript.Shell
        $lnk = $wshell.CreateShortcut($lnkPath)
        $lnk.TargetPath = $exe
        $lnk.WorkingDirectory = [IO.Path]::GetDirectoryName($exe)
        $lnk.Description = 'Terminal with vertical tabs'
        $lnk.Save()
        Write-Step "Start Menu shortcut: $lnkPath"
    }

    Write-Host ''
    Write-Host "TerminalV $tag installed: $exe" -ForegroundColor Green
    Write-Host ''
    Write-Host 'Launch:' -ForegroundColor White
    Write-Host '  TerminalV' -ForegroundColor Cyan
    Write-Host ''
    Write-Host "Docs: https://github.com/$Repository#readme" -ForegroundColor DarkGray
} finally {
    $ProgressPreference = $previousProgress
}
