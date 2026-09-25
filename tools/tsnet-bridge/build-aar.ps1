# Собирает Go AAR (Android) для вшитого tsnet - userspace Tailscale без системного VPN.
# Запускать на xeon (Windows) где есть Go + gomobile + Android SDK. На gamer без Go - собирается stub.
# Результат: tools/tsnet-bridge/tsnet.aar -> копируется в src/TerminalV.Mobile/Platforms/Android/libs/
param(
    [string]$AndroidSdk = "$env:LOCALAPPDATA\Android\Sdk",
    [string]$OutputAAR = "$PSScriptRoot\tsnet.aar",
    [string]$Target = "android/arm64,android/amd64"
)

Set-StrictMode -Version Latest
$ErrorActionPreference = "Stop"

Write-Host "==> tsnet-bridge: проверка Go"
if (-not (Get-Command go -ErrorAction SilentlyContinue)) { throw "Go не найден. Установи: winget install GoLang.Go" }
go version

Write-Host "==> gomobile"
if (-not (Get-Command gomobile -ErrorAction SilentlyContinue)) {
    Write-Host "gomobile не найден - ставлю"
    go install golang.org/x/mobile/cmd/gomobile@latest
    go install golang.org/x/mobile/cmd/gobind@latest
    $goBin = "$(go env GOPATH)\bin"
    $env:Path = "$goBin;$env:Path"
}
gomobile version
if ($LASTEXITCODE -ne 0) { gomobile init }

if (-not (Test-Path $AndroidSdk)) {
    Write-Host "WARN: Android SDK не найден в $AndroidSdk - gomobile всё равно попробует собрать, но может упасть."
    Write-Host "Установи: Visual Studio Installer -> Mobile development with .NET -> Android SDK, или dotnet workload install android"
}

$oldLoc = Get-Location
Set-Location $PSScriptRoot
    if ($AndroidSdk) { $env:ANDROID_HOME = $AndroidSdk; $env:ANDROID_SDK_ROOT = $AndroidSdk; Write-Host "==> ANDROID_HOME=$env:ANDROID_HOME" }
    # NDK: gomobile requires ndk-bundle or ANDROID_NDK_HOME, prefer 26.x
    if (-not $env:ANDROID_NDK_HOME) {
        $ndkRoot = Join-Path $AndroidSdk "ndk"
        if (Test-Path $ndkRoot) {
            $latestNdk = Get-ChildItem $ndkRoot -Directory | Sort-Object Name -Descending | Select-Object -First 1
            if ($latestNdk) { $env:ANDROID_NDK_HOME = $latestNdk.FullName; Write-Host "==> ANDROID_NDK_HOME=$env:ANDROID_NDK_HOME" }
        }
        # ensure ndk-bundle symlink for gomobile fallback
        $bundle = Join-Path $AndroidSdk "ndk-bundle"
        if (-not (Test-Path $bundle) -and $env:ANDROID_NDK_HOME -and (Test-Path $env:ANDROID_NDK_HOME)) {
            try { cmd /c mklink /D "$bundle" "$env:ANDROID_NDK_HOME" 2>$null | Out-Null; Write-Host "==> ndk-bundle -> $env:ANDROID_NDK_HOME" } catch {}
        }
    }
    Write-Host "==> go mod tidy"
    go mod tidy
    if ($LASTEXITCODE -ne 0) { throw "go mod tidy failed" }

    Write-Host "==> gomobile bind -target $Target -androidapi 21 -ldflags=-checklinkname=0 -o $OutputAAR org.terminalv.tsnet"
    # gomobile maps package tsnet + type Tsnet to Java class tsnet.Tsnet_.
    # anet (Android net.Interfaces fix) requires -checklinkname=0 on Go 1.23+
    if (Test-Path $OutputAAR) { Remove-Item $OutputAAR -Force }
    # NDK r26 needs explicit ELF alignment for devices with 16 KB memory pages.
    $linkerFlags = '-checklinkname=0 -extldflags=-Wl,-z,max-page-size=16384,-z,common-page-size=16384'
    gomobile bind -target $Target -androidapi 21 -ldflags $linkerFlags -o $OutputAAR ./...
    if ($LASTEXITCODE -ne 0) { throw "gomobile bind failed" }

    if (-not (Test-Path $OutputAAR)) { throw "AAR не создан: $OutputAAR" }
    $size = (Get-Item $OutputAAR).Length
    Write-Host "==> OK $OutputAAR $size bytes"

    $destDir = "$PSScriptRoot\..\..\src\TerminalV.Mobile\Platforms\Android\libs"
    New-Item -ItemType Directory -Force -Path $destDir | Out-Null
    Copy-Item $OutputAAR "$destDir\tsnet.aar" -Force
    Write-Host "==> Скопирован в $destDir\tsnet.aar"

    # iOS framework (опционально, требует macOS)
    if ($false) { # iOS skip on Windows - requires macOS
        # original: $IsMacOS or uname Darwin
        Write-Host "==> gomobile bind -target ios -o Tsnet.xcframework"
        gomobile bind -target ios -o "$PSScriptRoot\Tsnet.xcframework" ./...
        Write-Host "==> iOS framework готов"
    } else {
        Write-Host "==> iOS пропускаем (требует macOS)"
    }
Set-Location $oldLoc
