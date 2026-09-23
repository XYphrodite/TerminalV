<#
.SYNOPSIS
    Installs TerminalV - a Windows terminal with vertical tabs.

.DESCRIPTION
    Downloads TerminalV-win-x64.zip from GitHub Releases (the public download
    URLs, not the REST API), verifies the SHA-256 asset, extracts it and adds
    the install directory to the user PATH.

    When the .NET desktop runtime is installed, the smaller framework-dependent
    TerminalV-win-x64-light.zip is used instead; -Variant overrides the choice.
    The chosen variant is recorded next to the executable so `TerminalV update`
    keeps installing the same kind of build.

    This file is intentionally ASCII-only: Windows PowerShell 5.1 reads a BOM-less
    script as ANSI, while `irm | iex` chokes on a leading BOM. ASCII keeps both
    paths working.

.EXAMPLE
    irm https://raw.githubusercontent.com/XYphrodite/TerminalV/main/install.ps1 | iex

.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/XYphrodite/TerminalV/main/install.ps1))) -InstallDir 'D:\TerminalV'

.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/XYphrodite/TerminalV/main/install.ps1))) -DesktopShortcut

.EXAMPLE
    & ([scriptblock]::Create((irm https://raw.githubusercontent.com/XYphrodite/TerminalV/main/install.ps1))) -Variant full

.PARAMETER Variant
    auto (default) picks the light package when the .NET desktop runtime is
    installed, full always installs the self-contained package, light always
    installs the framework-dependent package (and fails fast without the runtime).

.PARAMETER DesktopShortcut
    Also create a shortcut on the current user's desktop. Off by default.

.PARAMETER NoShortcut
    Do not create any shortcuts, including when DesktopShortcut is specified.
#>
#Requires -Version 5.1
[CmdletBinding()]
param(
    [string] $InstallDir = (Join-Path $env:LOCALAPPDATA 'Programs\TerminalV'),

    [string] $Version = 'latest',

    [ValidateSet('auto', 'full', 'light')]
    [string] $Variant = 'auto',

    [switch] $NoPath,

    [switch] $NoShortcut,

    [switch] $DesktopShortcut
)

$ErrorActionPreference = 'Stop'
$Repository = 'XYphrodite/TerminalV'
$AssetName = 'TerminalV-win-x64.zip'
$LightAssetName = 'TerminalV-win-x64-light.zip'
$VariantMarker = '.terminalv-variant'
# Major of the .NET desktop runtime the light package targets. Bump together
# with the app's target framework; must match TerminalVUpdate in the source.
$DesktopMajor = '10'
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

function Send-ShortcutNotification {
    param([string] $Path, [bool] $Created)
    if (-not ('TerminalV.InstallerShell' -as [type])) {
        Add-Type -TypeDefinition @'
using System;
using System.Runtime.InteropServices;
namespace TerminalV {
    public static class InstallerShell {
        [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
        public static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);
    }
}
'@
    }
    $eventId = if ($Created) { 0x2 } else { 0x2000 }
    [TerminalV.InstallerShell]::SHChangeNotify($eventId, 0x2005, $Path, [IntPtr]::Zero)
}

function New-TerminalVShortcut {
    param([string] $Executable, [string] $Folder)
    if ([string]::IsNullOrWhiteSpace($Folder) -or -not [IO.Path]::IsPathRooted($Folder)) {
        throw 'Windows did not return a shortcut folder.'
    }
    $Executable = [IO.Path]::GetFullPath($Executable)
    if (-not [IO.File]::Exists($Executable)) { throw 'TerminalV executable not found.' }
    [IO.Directory]::CreateDirectory($Folder) | Out-Null
    $path = Join-Path $Folder 'TerminalV.lnk'
    $existed = Test-Path -LiteralPath $path
    if ($existed -and ([IO.File]::GetAttributes($path) -band ([IO.FileAttributes]::ReparsePoint -bor [IO.FileAttributes]::Directory))) {
        throw 'TerminalV.lnk is a directory or symbolic link.'
    }
    $temporary = Join-Path $Folder ('.TerminalV-' + [guid]::NewGuid().ToString('N') + '.lnk')
    $wshell = $null
    $lnk = $null
    try {
        $wshell = New-Object -ComObject WScript.Shell
        if ($existed) { [IO.File]::Copy($path, $temporary) }
        $lnk = $wshell.CreateShortcut($temporary)
        if ($existed) {
            if ($lnk.TargetPath -ine $Executable -or -not [string]::IsNullOrEmpty($lnk.Arguments)) {
                throw 'TerminalV.lnk targets another copy or has custom arguments. Rename it and retry.'
            }
        } else {
            $lnk.TargetPath = $Executable
            $lnk.WorkingDirectory = [IO.Path]::GetDirectoryName($Executable)
            $lnk.Description = 'Terminal with vertical tabs'
            $lnk.WindowStyle = 1
        }
        $lnk.IconLocation = "$Executable,0"
        $lnk.Save()
        if (-not [IO.File]::Exists($temporary)) { throw 'Windows did not save the shortcut.' }
        [Runtime.InteropServices.Marshal]::FinalReleaseComObject($lnk) | Out-Null
        $lnk = $null
        # PS 5.1 coerces $null to an empty string for string parameters.
        if ($existed) { [IO.File]::Replace($temporary, $path, [NullString]::Value) }
        else { [IO.File]::Move($temporary, $path) }
        try { Send-ShortcutNotification -Path $path -Created (-not $existed) }
        catch { Write-Verbose "Shortcut saved, but Shell notification failed: $_" }
        return $path
    } finally {
        if ($null -ne $lnk) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($lnk) | Out-Null }
        if ($null -ne $wshell) { [Runtime.InteropServices.Marshal]::FinalReleaseComObject($wshell) | Out-Null }
        # Only this function's unique temporary file, never a folder or the user's link.
        if ([IO.File]::Exists($temporary)) { [IO.File]::Delete($temporary) }
    }
}

function Install-TerminalVShortcuts {
    [CmdletBinding()]
    param(
        [string] $Executable,
        [switch] $NoShortcut,
        [switch] $DesktopShortcut,
        [scriptblock] $FolderPath = { param($Name) [Environment]::GetFolderPath([Environment+SpecialFolder]::$Name) }
    )
    if ($NoShortcut) { return }
    $destinations = @('Programs')
    if ($DesktopShortcut) { $destinations += 'DesktopDirectory' }
    foreach ($destination in $destinations) {
        try {
            $path = New-TerminalVShortcut -Executable $Executable -Folder (& $FolderPath $destination)
            Write-Step "Shortcut: $path"
        } catch {
            Write-Warning "Could not create $destination shortcut: $($_.Exception.Message). You can retry in TerminalV Settings."
        }
    }
}

function Get-ResponseUri {
    param($Response)

    if ($null -eq $Response) { return $null }
    $base = $Response.BaseResponse
    if ($null -eq $base) { return $null }
    if ($base.ResponseUri) { return [string]$base.ResponseUri }
    if ($base.RequestMessage -and $base.RequestMessage.RequestUri) {
        return [string]$base.RequestMessage.RequestUri
    }
    return $null
}

function Test-DesktopRuntimeLine {
    param([string] $Line, [string] $Major = $DesktopMajor)
    return [bool]($Line -match "^Microsoft\.WindowsDesktop\.App\s+$Major\.")
}

function Test-WindowsDesktopRuntime {
    try {
        $runtimes = & dotnet --list-runtimes 2>$null
    } catch {
        return $false
    }
    foreach ($line in @($runtimes)) {
        if (Test-DesktopRuntimeLine -Line ([string]$line)) { return $true }
    }
    return $false
}

function Select-TerminalVAsset {
    param(
        [ValidateSet('auto', 'full', 'light')]
        [string] $Variant = 'auto',
        [bool] $HasRuntime = $false
    )
    if ($Variant -eq 'light') { return $LightAssetName }
    if ($Variant -eq 'full') { return $AssetName }
    if ($HasRuntime) { return $LightAssetName }
    return $AssetName
}

function Get-ReleaseTag {
    param([string] $Requested)

    if ($Requested -ne 'latest') {
        if ($Requested -notmatch '^v') { return "v$Requested" }
        return $Requested
    }

    try {
        $resp = Invoke-WebRequest -Uri "https://github.com/$Repository/releases/latest" -UseBasicParsing -Headers $UserAgent
        $uri = Get-ResponseUri $resp
        if ($uri -match '/releases/tag/(v?[A-Za-z0-9._-]+)') {
            $tag = $Matches[1]
            if ($tag -notmatch '^v') { $tag = "v$tag" }
            return $tag
        }

        $html = $resp.Content
        if ($html -is [byte[]]) { $html = [Text.Encoding]::UTF8.GetString($html) }
        if ([string]$html -match '/releases/tag/(v\d+\.\d+\.\d+)') {
            return $Matches[1]
        }
    } catch {
        Write-Verbose "could not resolve latest tag: $($_.Exception.Message)"
    }

    return 'latest'
}

function Save-ReleaseAsset {
    param(
        [Parameter(Mandatory = $true)][string] $Url,
        [Parameter(Mandatory = $true)][string] $Destination,
        [Parameter(Mandatory = $true)][string] $Name
    )

    # HttpWebRequest rather than Invoke-WebRequest: in Windows PowerShell 5.1
    # IWR buffers the body and its built-in bar makes a ~60 MB download crawl.
    # Streaming plus a throttled Write-Progress bar stays honest and fast.
    $request = [Net.HttpWebRequest]::Create($Url)
    $request.UserAgent = 'terminalv-installer'
    $request.Timeout = 60000
    $request.ReadWriteTimeout = 300000
    $request.AllowAutoRedirect = $true

    $response = $request.GetResponse()
    try {
        $total = $response.ContentLength
        $source = $response.GetResponseStream()
        $file = [IO.File]::Create($Destination)
        try {
            $buffer = New-Object byte[] 81920
            $received = 0L
            $started = [Diagnostics.Stopwatch]::StartNew()
            $lastReport = [Diagnostics.Stopwatch]::StartNew()
            while (($read = $source.Read($buffer, 0, $buffer.Length)) -gt 0) {
                $file.Write($buffer, 0, $read)
                $received += $read
                if ($lastReport.ElapsedMilliseconds -ge 150 -or ($total -gt 0 -and $received -ge $total)) {
                    $lastReport.Restart()
                    $percent = if ($total -gt 0) { [math]::Min(100, [int](100.0 * $received / $total)) } else { 0 }
                    $speed = if ($started.Elapsed.TotalSeconds -gt 0) { $received / 1MB / $started.Elapsed.TotalSeconds } else { 0 }
                    $totalMb = if ($total -gt 0) { $total / 1MB } else { $received / 1MB }
                    Write-Progress -Activity "Downloading $Name" `
                        -Status ("{0:N1} / {1:N1} MB   {2:N1} MB/s" -f ($received / 1MB), $totalMb, $speed) `
                        -PercentComplete $percent
                }
            }
        } finally {
            $file.Dispose()
            $source.Dispose()
            Write-Progress -Activity "Downloading $Name" -Completed
        }
    } finally {
        $response.Close()
    }
}

try {
    $tag = Get-ReleaseTag -Requested $Version
    $hasRuntime = Test-WindowsDesktopRuntime
    if ($Variant -eq 'light' -and -not $hasRuntime) {
        throw 'The light package needs the .NET desktop runtime, which was not found. Install the runtime or use -Variant full.'
    }
    $AssetName = Select-TerminalVAsset -Variant $Variant -HasRuntime $hasRuntime
    $variantName = if ($AssetName -eq $LightAssetName) { 'light' } else { 'full' }
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
    Save-ReleaseAsset -Url $zipUrl -Destination $tempZip -Name $AssetName

    $expected = $null
    $tempSha = Join-Path $tempRoot "$AssetName.sha256"
    try {
        Invoke-WebRequest -Uri $shaUrl -OutFile $tempSha -UseBasicParsing -Headers $UserAgent
        $shaBody = [IO.File]::ReadAllText($tempSha)
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
    # Records which kind of build this is, so `TerminalV update` keeps the variant.
    [IO.File]::WriteAllText((Join-Path $InstallDir $VariantMarker), $variantName)

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

    Install-TerminalVShortcuts -Executable $exe -NoShortcut:$NoShortcut -DesktopShortcut:$DesktopShortcut

    Write-Host ''
    Write-Host "TerminalV $tag ($variantName) installed: $exe" -ForegroundColor Green
    Write-Host ''
    Write-Host 'Launch:' -ForegroundColor White
    Write-Host '  TerminalV' -ForegroundColor Cyan
    Write-Host ''
    Write-Host "Docs: https://github.com/$Repository#readme" -ForegroundColor DarkGray
} finally {
    $ProgressPreference = $previousProgress
}
