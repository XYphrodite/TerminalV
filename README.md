# TerminalV

Терминал для Windows с **вертикальными вкладками**. Каждая вкладка — отдельная сессия PowerShell через ConPTY, экран рисует xterm.js.

## Установка

В PowerShell:

```powershell
irm https://raw.githubusercontent.com/XYphrodite/TerminalV/main/install.ps1 | iex
```

Скрипт скачивает последний релиз, проверяет SHA-256 из описания релиза, распаковывает в `%LOCALAPPDATA%\Programs\TerminalV`, добавляет каталог в пользовательский PATH и создаёт ярлык в меню Пуск. Права администратора не нужны.

Другой каталог или конкретная версия:

```powershell
& ([scriptblock]::Create((irm https://raw.githubusercontent.com/XYphrodite/TerminalV/main/install.ps1))) -InstallDir 'D:\TerminalV' -Version v0.1.0
```

Параметры: `-InstallDir`, `-Version` (по умолчанию latest), `-NoPath`, `-NoShortcut`.

Ручная установка: zip `TerminalV-win-x64.zip` со [страницы релизов](https://github.com/XYphrodite/TerminalV/releases). Сборка self-contained, отдельно ставить .NET не нужно. Нужен [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) — на Windows 11 он уже есть вместе с Edge.

Запуск после установки:

```powershell
TerminalV
```

Установленная копия сама проверяет GitHub Releases при старте. Если есть более новая версия, слева появляется кнопка **Обновить**. Клик по номеру версии внизу сайдбара запускает проверку вручную. Скачанный zip сверяется с SHA-256 из релиза, новый `TerminalV.exe` проверяется через `--help` до замены и после; при сбое остаётся прежняя сборка. Сборка 0.1.0 обновление ещё не умеет — её один раз ставят заново через `irm`.

Из исходников после `dotnet build`: `src\TerminalV\bin\Debug\net10.0-windows\TerminalV.exe`.

## Документация

- [Как пользоваться](docs/usage.md) — вкладки, клавиши, копирование
- [ProjectContext.md](ProjectContext.md) — архитектура и устройство кода

## Сборка из исходников

Нужны .NET 10 SDK, Node.js и npm.

```powershell
dotnet build
dotnet run --project src/TerminalV
```

Релизная папка (self-contained `win-x64`, один exe + `wwwroot`):

```powershell
.\scripts\publish.ps1
```

Артефакты: `artifacts/TerminalV-win-x64.zip` и `.sha256`.

Проверка ConPTY без окна:

```powershell
.\src\TerminalV\bin\Debug\net10.0-windows\TerminalV.exe --smoke
```

## Что умеет v0.2.1

- Вертикальный список вкладок слева
- PowerShell 7 (`pwsh`), иначе Windows PowerShell
- Несколько независимых сессий
- Переименование вкладки (двойной клик)
- Копирование и вставка, в том числе правой кнопкой
- Самообновление из GitHub Releases (проверка при старте)

## Лицензия

[MIT](LICENSE)
