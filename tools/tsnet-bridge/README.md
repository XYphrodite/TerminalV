# tsnet-bridge — вшитый Tailscale для TerminalV.Mobile

`Go` либа `tsnet` (userspace `WireGuard`, `BSD`) без системного `TUN`/`VPNService`. Работает рядом с `Happ` — не занимает слот `VPN` на `Android`.

* `tsnet.go` — `Tsnet.Start(authKey,hostname,controlUrl,stateDir,verbosity)` + `Dial(host,port)` + `DialFD`
* `go.mod` — `tailscale.com v1.82.0`
* Сборка: `gomobile bind` → Java-класс `tsnet.Tsnet_` и `libgojni.so` в `AAR`

## Сборка AAR (на xeon, где есть Go + Android SDK)

```powershell
# Windows (xeon desktop-ib88isg)
.\tools\tsnet-bridge\build-aar.ps1 -AndroidSdk C:\AndroidSDK
# Linux/macOS
./tools/tsnet-bridge/build-aar.sh
```

Результат: `tools/tsnet-bridge/tsnet.aar` + копия `src/TerminalV.Mobile/Platforms/Android/libs/tsnet.aar` (подхватывается `MAUI` как `AndroidAarLibrary`).

Android-сборка требует AAR и останавливается с ошибкой, если его нет. PowerShell-скрипт по умолчанию собирает ARM64 и x86_64, как APK; для других архитектур задайте `-Target`. На остальных платформах остаётся `TailscaleStubConnector`.

Скрипты передают внешнему компоновщику `max-page-size=16384` и `common-page-size=16384`, чтобы библиотека из NDK r26 имела 16-КБ ELF-выравнивание. См. [требования Android к размеру страниц](https://developer.android.com/guide/practices/page-sizes#compile-r27).

После публикации проверяйте готовый APK, включая наличие Java-класса и обеих нативных библиотек:

```powershell
.\scripts\verify-mobile-apk.ps1 -Apk path\to\com.terminalv.mobile-Signed.apk
```

## Использование в MAUI

`Home.razor` → чек `Через вшитый Tailscale` → `TailscaleOptions{ Enabled, AuthKey, Hostname }` → `SshConnectionOptions.Tailscale` → `SshService.Create` → `TsnetSshSession` → `ITailscaleConnector.DialAsync(100.119.48.15,22)` → `SSH.NET ShellStream`.

Auth key: `admin.tailscale.com` → `Settings` → `Keys` → `Generate auth key` (ephemeral, reusable, теги `tag:mobile`).

`StateDir`: `Context.FilesDir/tsnet-state` на `Android`, очищается при `Stop`/`Uninstall`.
