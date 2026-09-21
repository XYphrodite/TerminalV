# tsnet-bridge — вшитый Tailscale для TerminalV.Mobile

`Go` либа `tsnet` (userspace `WireGuard`, `BSD`) без системного `TUN`/`VPNService`. Работает рядом с `Happ` — не занимает слот `VPN` на `Android`.

* `tsnet.go` — `Tsnet.Start(authKey,hostname,controlUrl,stateDir,verbosity)` + `Dial(host,port)` + `DialFD`
* `go.mod` — `tailscale.com v1.82.0`
* Сборка: `gomobile bind -target android` → `org.terminalv.tsnet.Tsnet` `AAR`

## Сборка AAR (на xeon, где есть Go + Android SDK)

```powershell
# Windows (xeon desktop-ib88isg)
.\tools\tsnet-bridge\build-aar.ps1
# Linux/macOS
./tools/tsnet-bridge/build-aar.sh
```

Результат: `tools/tsnet-bridge/tsnet.aar` + копия `src/TerminalV.Mobile/Platforms/Android/libs/tsnet.aar` (подхватывается `MAUI` как `AndroidAarLibrary`).

Без `Go`/`gomobile` сборка `TerminalV.Mobile` идёт со `stub` (`TailscaleStubConnector`) — в UI покажет хинт `собери AAR на xeon`, но не упадёт. На `Android` без `AAR` `Dial` бросит `PlatformNotSupportedException` с инструкцией.

## Использование в MAUI

`Home.razor` → чек `Через вшитый Tailscale` → `TailscaleOptions{ Enabled, AuthKey, Hostname }` → `SshConnectionOptions.Tailscale` → `SshService.Create` → `TsnetSshSession` → `ITailscaleConnector.DialAsync(100.119.48.15,22)` → `SSH.NET ShellStream`.

Auth key: `admin.tailscale.com` → `Settings` → `Keys` → `Generate auth key` (ephemeral, reusable, теги `tag:mobile`).

`StateDir`: `Context.FilesDir/tsnet-state` на `Android`, очищается при `Stop`/`Uninstall`.
