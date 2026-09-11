#Requires -Version 5.1
<#
.SYNOPSIS
    Publish a self-contained win-x64 build and pack TerminalV-win-x64.zip.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [string] $Runtime = 'win-x64'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
$outDir = Join-Path $root "artifacts\publish\$Runtime"
$zipPath = Join-Path $root "artifacts\TerminalV-$Runtime.zip"
$shaPath = Join-Path $root "artifacts\TerminalV-$Runtime.zip.sha256"
$project = Join-Path $root 'src\TerminalV\TerminalV.csproj'

if (Test-Path -LiteralPath (Join-Path $root 'artifacts')) {
    Remove-Item -LiteralPath (Join-Path $root 'artifacts') -Recurse -Force
}
New-Item -ItemType Directory -Path $outDir -Force | Out-Null

Write-Host "==> publishing $project" -ForegroundColor Cyan
dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $outDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed with exit code $LASTEXITCODE"
}

Get-ChildItem -LiteralPath $outDir -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in '.pdb', '.xml' } |
    Remove-Item -Force -ErrorAction SilentlyContinue

if (-not (Test-Path -LiteralPath (Join-Path $outDir 'TerminalV.exe'))) {
    throw 'TerminalV.exe is missing from the publish output'
}
if (-not (Test-Path -LiteralPath (Join-Path $outDir 'wwwroot\index.html'))) {
    throw 'wwwroot/index.html is missing from the publish output'
}

if (Test-Path -LiteralPath $zipPath) {
    Remove-Item -LiteralPath $zipPath -Force
}

Write-Host "==> packing $zipPath" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $shaPath -Value "$hash  TerminalV-$Runtime.zip" -Encoding ascii

Write-Host ''
Write-Host "zip:  $zipPath" -ForegroundColor Green
Write-Host "sha:  $hash"
Write-Host "size: $([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB"
