#Requires -Version 5.1
<#
.SYNOPSIS
    Publish a self-contained win-x64 build and pack TerminalV-win-x64.zip.
#>
[CmdletBinding()]
param(
    [string] $Configuration = 'Release',
    [ValidatePattern('^[a-z0-9-]+$')]
    [string] $Runtime = 'win-x64',
    [string] $OutputDirectory = 'artifacts'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$root = Split-Path -Parent $PSScriptRoot
if ([System.IO.Path]::IsPathRooted($OutputDirectory)) {
    $packageDir = [System.IO.Path]::GetFullPath($OutputDirectory)
} else {
    $packageDir = [System.IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
}
$zipPath = Join-Path $packageDir "TerminalV-$Runtime.zip"
$shaPath = "$zipPath.sha256"
$staging = Join-Path $packageDir ('.publish-' + [guid]::NewGuid().ToString('N'))
$outDir = Join-Path $staging 'app'
$project = Join-Path $root 'src\TerminalV\TerminalV.csproj'
$comProject = Join-Path $root 'src\TerminalV.Com\TerminalV.Com.csproj'
$comDir = Join-Path $staging 'com'

if ((Test-Path -LiteralPath $zipPath) -or (Test-Path -LiteralPath $shaPath)) {
    throw 'A package already exists. Use -OutputDirectory with a new directory; previous artifacts are never removed.'
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

Write-Host "==> publishing console shim $comProject" -ForegroundColor Cyan
New-Item -ItemType Directory -Path $comDir -Force | Out-Null
dotnet publish $comProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained true `
    -p:PublishSingleFile=true `
    -p:PublishTrimmed=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $comDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish TerminalV.Com failed with exit code $LASTEXITCODE"
}

$comBuilt = Get-ChildItem -LiteralPath $comDir -Filter 'TerminalV.Com.exe' | Select-Object -First 1
if (-not $comBuilt) {
    throw 'TerminalV.Com.exe is missing from the shim publish output'
}

Copy-Item -LiteralPath $comBuilt.FullName -Destination (Join-Path $outDir 'TerminalV.com') -Force

if (-not (Test-Path -LiteralPath (Join-Path $outDir 'TerminalV.com'))) {
    throw 'TerminalV.com is missing from the publish output'
}

Write-Host "==> packing $zipPath" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $shaPath -Value "$hash  TerminalV-$Runtime.zip" -Encoding ascii

Write-Host ''
Write-Host "zip:  $zipPath" -ForegroundColor Green
Write-Host "app:  $outDir"
Write-Host "sha:  $hash"
Write-Host "size: $([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB"
