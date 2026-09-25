param(
    [Parameter(Mandatory = $true)][string]$Apk,
    [string]$MobileRoot = "$PSScriptRoot\..\src\TerminalV.Mobile",
    [string[]]$Architectures = @('arm64-v8a', 'x86_64')
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = [IO.Compression.ZipFile]::OpenRead((Resolve-Path -LiteralPath $Apk).Path)
try {
    foreach ($name in @('index.html', 'js/mobile-bridge.js', 'css/app.css', 'assets/mobile.js', 'assets/mobile.css')) {
        $entry = $zip.GetEntry("assets/wwwroot/$name")
        if (!$entry) { throw "Missing APK resource: $name" }
        $stream = $entry.Open()
        $hasher = [Security.Cryptography.SHA256]::Create()
        try { $actual = [BitConverter]::ToString($hasher.ComputeHash($stream)).Replace('-', '') }
        finally { $stream.Dispose(); $hasher.Dispose() }
        $expected = (Get-FileHash -LiteralPath (Join-Path $MobileRoot "wwwroot/$name") -Algorithm SHA256).Hash
        if ($actual -ne $expected) { throw "Stale APK resource: $name" }
        Write-Output "PASS resource $name"
    }
    foreach ($abi in $Architectures) {
        $entry = $zip.GetEntry("lib/$abi/libgojni.so")
        if (!$entry -or $entry.Length -eq 0) { throw "Missing embedded Tailscale native library for $abi" }
        Write-Output "PASS embedded Tailscale native library: $abi"
    }
    $foundBinding = $false
    foreach ($entry in $zip.Entries) {
        if ($entry.FullName -notmatch '^classes\d*\.dex$') { continue }
        $stream = $entry.Open()
        $reader = New-Object IO.StreamReader($stream, [Text.Encoding]::ASCII)
        try { $text = $reader.ReadToEnd() }
        finally { $reader.Dispose(); $stream.Dispose() }
        if ($text.Contains('Ltsnet/Tsnet_;')) { $foundBinding = $true; break }
    }
    if (!$foundBinding) { throw 'Missing tsnet.Tsnet_ Java binding in APK DEX files' }
    Write-Output 'PASS tsnet.Tsnet_ Java binding'
} finally { $zip.Dispose() }
Get-FileHash -LiteralPath $Apk -Algorithm SHA256
