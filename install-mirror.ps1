<#
.SYNOPSIS
    Installs TerminalV Mirror - desktop viewer for another PC's TerminalV sessions.
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\TerminalV.Mirror'),
    [string] $Version = 'latest',
    [switch] $NoPath,
    [switch] $NoShortcut,
    [switch] $DesktopShortcut
)
$ErrorActionPreference = 'Stop'
$Repository = 'XYphrodite/TerminalV'
$AssetName = 'TerminalV.Mirror-win-x64.zip'
$UserAgent = @{ 'User-Agent' = 'terminalv-mirror-installer' }
$previousProgress = $ProgressPreference; $ProgressPreference = 'SilentlyContinue'
try { [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12 } catch {}
function Write-Step { param([string]$Message) Write-Host "==> $Message" -ForegroundColor Cyan }
$ProgressPreference = $previousProgress

function Resolve-Version {
    param([string]$Version)
    if ($Version -ne 'latest' -and $Version -notlike 'v*') { return "v$Version" }
    if ($Version -eq 'latest') {
        $resp = Invoke-WebRequest -Uri "https://github.com/$Repository/releases/latest" -UseBasicParsing -Headers $UserAgent
        if ($resp.Headers.Location) { return ($resp.Headers.Location -split '/')[-1] }
        return 'latest'
    }
    return $Version
}

$resolvedVersion = Resolve-Version -Version $Version
if ($resolvedVersion -eq 'latest') { $resolvedVersion = 'v0.7.40' }
Write-Step "Installing TerminalV Mirror $resolvedVersion to $InstallDir"

$zipUrl = "https://github.com/$Repository/releases/download/$resolvedVersion/$AssetName"
$zipShaUrl = "$zipUrl.sha256"
$tempZip = Join-Path $env:TEMP "TerminalV.Mirror.$([guid]::NewGuid().ToString('N')).zip"
try {
    Write-Step "Downloading $zipUrl"
    Invoke-WebRequest -Uri $zipUrl -OutFile $tempZip -Headers $UserAgent -UseBasicParsing
    Write-Step "Verifying SHA256"
    $tempSha = Join-Path $env:TEMP "TerminalV.Mirror.$([guid]::NewGuid().ToString('N')).sha256"
    Invoke-WebRequest -Uri $zipShaUrl -OutFile $tempSha -UseBasicParsing -Headers $UserAgent
    $expectedSha = ([IO.File]::ReadAllText($tempSha).Split()[0].Trim().ToLowerInvariant())
    Remove-Item -LiteralPath $tempSha -Force -ErrorAction SilentlyContinue
    $actualSha = (Get-FileHash -Algorithm SHA256 -LiteralPath $tempZip).Hash.ToLowerInvariant()
    if ($expectedSha -ne $actualSha) { throw "SHA256 mismatch: expected $expectedSha got $actualSha" }
    Write-Step "Extracting to $InstallDir"
    if (Test-Path -LiteralPath $InstallDir) { Remove-Item -LiteralPath $InstallDir -Recurse -Force }
    Expand-Archive -LiteralPath $tempZip -DestinationPath $InstallDir -Force
    $exe = Get-ChildItem -LiteralPath $InstallDir -Filter 'TerminalV.Mirror.exe' -Recurse | Select-Object -First 1
    if (-not $exe) { throw "TerminalV.Mirror.exe not found after extract" }
    Write-Step "Creating Start Menu shortcut"
    if (-not $NoShortcut) {
        $startMenu = Join-Path $env:APPDATA 'Microsoft\Windows\Start Menu\Programs\TerminalV Mirror.lnk'
        $shell = New-Object -ComObject WScript.Shell
        $shortcut = $shell.CreateShortcut($startMenu)
        $shortcut.TargetPath = $exe.FullName
        $shortcut.WorkingDirectory = $InstallDir
        $shortcut.Save()
        Write-Host "Start Menu: $startMenu" -ForegroundColor Green
        if ($DesktopShortcut) {
            $desktop = Join-Path ([Environment]::GetFolderPath('Desktop')) 'TerminalV Mirror.lnk'
            $dshort = $shell.CreateShortcut($desktop)
            $dshort.TargetPath = $exe.FullName
            $dshort.WorkingDirectory = $InstallDir
            $dshort.Save()
            Write-Host "Desktop: $desktop" -ForegroundColor Green
        }
    }
    if (-not $NoPath) {
        $userPath = [Environment]::GetEnvironmentVariable('Path', 'User')
        if ($userPath -split ';' -notcontains $InstallDir) {
            [Environment]::SetEnvironmentVariable('Path', "$userPath;$InstallDir", 'User')
            Write-Host "Added $InstallDir to user PATH (restart shell)" -ForegroundColor Yellow
        }
    }
    Write-Host "TerminalV Mirror $resolvedVersion installed. Run: TerminalV.Mirror.exe  (gateway ws://other-pc:5454)" -ForegroundColor Green
} finally {
    if (Test-Path -LiteralPath $tempZip) { Remove-Item -LiteralPath $tempZip -Force }
}
