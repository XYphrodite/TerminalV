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

Установленная копия сама проверяет GitHub Releases при старте. Если есть более новая версия, слева появляется кнопка **Обновить**. Из любой оболочки, в том числе из вкладки TerminalV:

```powershell
TerminalV update --check
TerminalV update
```

Если окно уже открыто, команда ставит файлы и просит закрыть его — сессии сами не убиваются. Скачанный zip сверяется с SHA-256, новый exe отвечает на `--help` до замены и после. Сборка 0.1.0 обновление не умеет — её один раз ставят через `irm`.

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

Регрессионные проверки интерфейса (нужен установленный Edge или Chrome):

```powershell
npm.cmd --prefix ui test
```

Изолированные проверки интеграции рабочей папки с PowerShell и настоящим ConPTY:

```powershell
dotnet run --project tests/TerminalV.Pty.Tests -c Release
```

Тесты не загружают пользовательские профили и не подключаются к рабочему фоновому процессу TerminalV. Они запускают Windows PowerShell 5.1 и, если доступен, PowerShell 7 из `Program Files\PowerShell\7`. Для portable PowerShell 7 можно задать путь к `pwsh.exe` через `TERMINALV_TEST_PWSH`; при его отсутствии эта часть явно пропускается.

Проверки миграции базы, групп, скрытых сессий и отключения звука на временных SQLite-файлах (рабочая база пользователя не используется):

```powershell
dotnet run --project tests/TerminalV.Data.Tests -c Release
```

## Что умеет v0.4.18

- Вертикальный список вкладок слева
- PowerShell 7 (`pwsh`), иначе Windows PowerShell
- Несколько независимых сессий
- Переименование вкладки (двойной клик)
- Копирование и вставка, в том числе правой кнопкой
- Предупреждение с предпросмотром перед многострочной вставкой
- Самообновление: кнопка в сайдбаре и `TerminalV update`
- Темы, шрифт, фон-фото, сворачиваемый список сессий, масштаб как в VS Code
- Сессии сохраняются в SQLite

## Лицензия

[MIT](LICENSE)
