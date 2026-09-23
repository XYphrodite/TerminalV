#Requires -Version 5.1
<#
.SYNOPSIS
    Publish self-contained and framework-dependent win-x64 builds and pack
    TerminalV-win-x64.zip and TerminalV-win-x64-light.zip.
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
try { [Console]::OutputEncoding = [System.Text.Encoding]::UTF8; [Console]::InputEncoding = [System.Text.Encoding]::UTF8 } catch {}
try { $OutputEncoding = [System.Text.Encoding]::UTF8 } catch {}
try { chcp 65001 >$null } catch {}

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

# A framework-dependent ("light") twin of the same build: needs the .NET desktop
# runtime on the machine, so the installer picks it only when that runtime is present.
$lightOutDir = Join-Path $staging 'app-light'
$lightComDir = Join-Path $staging 'com-light'
New-Item -ItemType Directory -Path $lightOutDir -Force | Out-Null
New-Item -ItemType Directory -Path $lightComDir -Force | Out-Null

Write-Host "==> publishing light $project" -ForegroundColor Cyan
dotnet publish $project `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $lightOutDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish (light) failed with exit code $LASTEXITCODE"
}

Get-ChildItem -LiteralPath $lightOutDir -Recurse -File -ErrorAction SilentlyContinue |
    Where-Object { $_.Extension -in '.pdb', '.xml' } |
    Remove-Item -Force -ErrorAction SilentlyContinue

if (-not (Test-Path -LiteralPath (Join-Path $lightOutDir 'TerminalV.exe'))) {
    throw 'TerminalV.exe is missing from the light publish output'
}
if (-not (Test-Path -LiteralPath (Join-Path $lightOutDir 'wwwroot\index.html'))) {
    throw 'wwwroot/index.html is missing from the light publish output'
}

Write-Host "==> publishing light console shim $comProject" -ForegroundColor Cyan
dotnet publish $comProject `
    -c $Configuration `
    -r $Runtime `
    --self-contained false `
    -p:PublishSingleFile=true `
    -p:DebugType=none `
    -p:DebugSymbols=false `
    -o $lightComDir

if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish TerminalV.Com (light) failed with exit code $LASTEXITCODE"
}

$lightComBuilt = Get-ChildItem -LiteralPath $lightComDir -Filter 'TerminalV.Com.exe' | Select-Object -First 1
if (-not $lightComBuilt) {
    throw 'TerminalV.Com.exe is missing from the light shim publish output'
}

Copy-Item -LiteralPath $lightComBuilt.FullName -Destination (Join-Path $lightOutDir 'TerminalV.com') -Force

if (-not (Test-Path -LiteralPath (Join-Path $lightOutDir 'TerminalV.com'))) {
    throw 'TerminalV.com is missing from the light publish output'
}

Write-Host "==> packing $zipPath" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $outDir '*') -DestinationPath $zipPath -CompressionLevel Optimal

$hash = (Get-FileHash -LiteralPath $zipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $shaPath -Value "$hash  TerminalV-$Runtime.zip" -Encoding ascii

$lightZipPath = Join-Path $packageDir "TerminalV-$Runtime-light.zip"
$lightShaPath = "$lightZipPath.sha256"
if ((Test-Path -LiteralPath $lightZipPath) -or (Test-Path -LiteralPath $lightShaPath)) {
    throw 'A light package already exists. Use -OutputDirectory with a new directory; previous artifacts are never removed.'
}

Write-Host "==> packing $lightZipPath" -ForegroundColor Cyan
Compress-Archive -Path (Join-Path $lightOutDir '*') -DestinationPath $lightZipPath -CompressionLevel Optimal

$lightHash = (Get-FileHash -LiteralPath $lightZipPath -Algorithm SHA256).Hash.ToLowerInvariant()
Set-Content -LiteralPath $lightShaPath -Value "$lightHash  TerminalV-$Runtime-light.zip" -Encoding ascii

Write-Host ''
Write-Host "zip:  $zipPath" -ForegroundColor Green
Write-Host "app:  $outDir"
Write-Host "sha:  $hash"
Write-Host "size: $([math]::Round((Get-Item $zipPath).Length / 1MB, 1)) MB"
Write-Host ''
Write-Host "zip:  $lightZipPath" -ForegroundColor Green
Write-Host "app:  $lightOutDir"
Write-Host "sha:  $lightHash"
Write-Host "size: $([math]::Round((Get-Item $lightZipPath).Length / 1MB, 1)) MB"
