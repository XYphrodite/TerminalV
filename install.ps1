<#
.SYNOPSIS
    Installs TerminalV - a Windows terminal with vertical tabs.

.DESCRIPTION
    Downloads TerminalV-win-x64.zip from the GitHub release, verifies the SHA-256
    published in the release notes, extracts it and adds the install directory
    to the user PATH.

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

try {
    if ($Version -eq 'latest') {
        $releaseUrl = "https://api.github.com/repos/$Repository/releases/latest"
    } else {
        $tag = $Version
        if ($tag -notmatch '^v') { $tag = "v$tag" }
        $releaseUrl = "https://api.github.com/repos/$Repository/releases/tags/$tag"
    }

    Write-Step "looking up release: $Version"
    $headers = @{ 'User-Agent' = 'terminalv-installer'; 'Accept' = 'application/vnd.github+json' }
    $release = Invoke-RestMethod -Uri $releaseUrl -Headers $headers

    $asset = $release.assets | Where-Object { $_.name -eq $AssetName } | Select-Object -First 1
    if (-not $asset) {
        throw "release $($release.tag_name) has no asset named $AssetName"
    }

    $sizeMb = [math]::Round($asset.size / 1MB, 1)
    Write-Step "downloading $AssetName $($release.tag_name) ($sizeMb MB)"

    $tempRoot = Join-Path ([IO.Path]::GetTempPath()) ("terminalv-" + [Guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Path $tempRoot -Force | Out-Null
    $tempZip = Join-Path $tempRoot $AssetName
    Invoke-WebRequest -Uri $asset.browser_download_url -OutFile $tempZip -Headers @{ 'User-Agent' = 'terminalv-installer' }

    $expected = $null
    $hashPattern = 'SHA-256[\s\S]{0,80}?([0-9a-fA-F]{64})'
    if ($release.body -match $hashPattern) {
        $expected = $Matches[1].ToLowerInvariant()
    }

    if ($expected) {
        $actual = (Get-FileHash -Path $tempZip -Algorithm SHA256).Hash.ToLowerInvariant()
        if ($actual -ne $expected) {
            Remove-Item $tempRoot -Recurse -Force -ErrorAction SilentlyContinue
            throw "SHA-256 mismatch. Expected $expected, got $actual"
        }
        Write-Step 'SHA-256 verified'
    } else {
        Write-Warning 'release notes carry no SHA-256, skipping checksum verification'
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
    Write-Host "TerminalV $($release.tag_name) installed: $exe" -ForegroundColor Green
    Write-Host ''
    Write-Host 'Launch:' -ForegroundColor White
    Write-Host '  TerminalV' -ForegroundColor Cyan
    Write-Host ''
    Write-Host "Docs: https://github.com/$Repository#readme" -ForegroundColor DarkGray
} finally {
    $ProgressPreference = $previousProgress
}
