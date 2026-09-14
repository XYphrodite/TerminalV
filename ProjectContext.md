# TerminalV - Project Context

## Project Overview

**TerminalV** — десктопный терминал для Windows с вертикальными вкладками. Каждая вкладка запускает отдельный процесс оболочки через ConPTY; вывод рисует xterm.js внутри WebView2. Цель — привычный PowerShell без горизонтальной полоски вкладок.

**Platform**: Windows 10 1809+ / Windows 11 x64, `.NET 10` (`net10.0-windows`)  
**Language**: `C#`, JavaScript, CSS, PowerShell (установка и релиз)  
**Domain**: инструменты разработчика, эмулятор терминала

---

## Technical Stack

### Runtime и инструменты

| Слой | Технология | Заметки |
|------|------------|---------|
| Хост | WPF, `net10.0-windows` | `TerminalV.exe`, `WinExe` |
| Терминал | ConPTY (`CreatePseudoConsole`) | Настоящая консоль Windows, не пайпы |
| Отрисовка | WebView2 + xterm.js | UI вкладок и VT-эмуляция в Chromium |
| Сборка UI | Vite 6, npm | Бандл в `src/TerminalV/wwwroot` |
| Дистрибуция | GitHub Releases + `install.ps1` | Self-contained `win-x64`, SHA-256 в notes |

Отдельно ставить .NET на машине пользователя не нужно. Нужен WebView2 Runtime (идёт с Edge).

### Key Dependencies

Версии из `TerminalV.csproj` и `ui/package.json`.

| Пакет | Версия | Назначение |
|-------|--------|------------|
| `Microsoft.Web.WebView2` | `1.0.4191.47` | Хост Chromium для UI и xterm.js |
| `@xterm/xterm` | `5.5.x` | Эмуляция терминала, буфер, ввод |
| `@xterm/addon-fit` | `0.10.x` | Подгон cols/rows под размер панели |
| `@xterm/addon-web-links` | `0.12.x` | Клики по URL в выводе |
| `@xterm/addon-webgl` | `0.18.x` | WebGL-рендер, fallback на canvas |
| `vite` | `6.x` | Сборка `ui/` в `wwwroot` |

P/Invoke к `kernel32` для ConPTY написан вручную, отдельных native-пакетов нет.

---

## Architecture Overview

Хост на C# владеет окном, процессами и каналами ConPTY. Renderer — статическая страница в WebView2. Обмен идёт JSON-сообщениями через `PostWebMessageAsJson` / `chrome.webview.postMessage`.

```
                    GitHub Releases
              TerminalV-win-x64.zip + SHA-256
                           |
                      install.ps1
                           v
                 %LOCALAPPDATA%\Programs\TerminalV
                           |
                    TerminalV.exe (WPF)
                           |
          +----------------+----------------+
          |                                 |
   MainWindow / WebView2              Pty / ConPtySession
   virtual host terminalv.local         CreatePseudoConsole
          |                                 |
          v                                 v
   ui bundle (xterm.js)  <--- JSON --->  powershell / pwsh
   vertical tabs + panes     create/write/resize/kill
                             data / exit / error
```

### Design Principles

1. **Не писать VT-парсер.** Отрисовка и ввод — xterm.js, хост только гоняет байты.
2. **Одна вкладка — один процесс.** Закрытие вкладки убивает ConPTY и дерево процессов.
3. **UI не в WPF.** Вкладки, оверлеи и шорткаты живут в `ui/`, чтобы менять внешний вид без пересборки хоста (кроме `dotnet build`, который гоняет Vite).
4. **Релиз проверяется хешем.** `install.ps1` сравнивает SHA-256 архива с строкой в notes релиза.

---

## Project Components

### 1. **TerminalV (WPF host)**
**Type**: приложение (`WinExe`)  
**Location**: `src/TerminalV/`  
**Purpose**: окно, WebView2, жизненный цикл сессий

- `App.xaml.cs` — обычный запуск окна или `--smoke` без UI (проверка ConPTY).
- `MainWindow` — тёмное окно, mapping `https://terminalv.local` → `wwwroot`, мост сообщений.
- `Host/TerminalBridge` — разбор JSON, создание/запись/resize/kill сессий, буфер обмена.
- `Host/IncomingMessage` — DTO входящих сообщений.

**Dependencies**: `Microsoft.Web.WebView2`, `Pty`.

### 2. **Pty**
**Type**: код внутри хоста  
**Location**: `src/TerminalV/Pty/`  
**Purpose**: ConPTY и запуск оболочки

| Тип | Ответственность |
|-----|-----------------|
| `ConPtySession` | Пайпы, `CreatePseudoConsole`, `CreateProcess`, чтение/запись, resize, dispose |
| `NativeMethods` | P/Invoke `kernel32` (ConPTY, process attributes) |
| `ShellResolver` | `pwsh` → Windows PowerShell, override `TERMINALV_SHELL` |

`UpdateProcThreadAttribute` для `PROC_THREAD_ATTRIBUTE_PSEUDOCONSOLE` передаёт **сам handle** (`HPCON`), не указатель на него. Концы пайпов, отданные в `CreatePseudoConsole`, закрываются только после `CreateProcess`.

### 3. **ui**
**Type**: фронтенд (Vite)  
**Location**: `ui/`  
**Purpose**: вертикальные вкладки и xterm.js

- `ui/src/main.js` — вкладки, шорткаты, copy/paste, overlay «процесс завершился».
- `ui/src/pane-layout.js` — деревья расположений (до восьми сессий), нормализация, геометрия и навигация.
- `ui/src/pane-view.js` — размещение уже созданных DOM-терминалов и разделители; перестановка/resize не пересоздают xterm или ConPTY.
- `ui/src/styles.css` — тёмная тема, сайдбар 228px.
- Сборка кладётся в `src/TerminalV/wwwroot` (в git не коммитится).

### 4. **Update**
**Type**: код внутри хоста  
**Location**: `src/TerminalV/Update/`  
**Purpose**: проверка и установка релизов, как `agent-sync update`

- Репозиторий зашит: `XYphrodite/TerminalV`. Другой origin задать нельзя.
- `GitHubReleaseSource` и `install.ps1` качают файлы с `github.com/releases/.../download`, без REST API (лимит 60 запросов/час его не касается).
- `SelfUpdateService` сверяет SHA-256 zip и наличие EXE/COM/index.html, гоняет `TerminalV.exe --help` до замены и после, переименовывает текущий exe в `.old-*`. Ошибка проверки или атомарной записи pending-маркера откатывает EXE; повторная установка поверх pending запрещена.
- `wwwroot` и `TerminalV.com` подменяются при следующем старте (`PendingUpdateApplier`). Ошибка замены COM возвращает UI в прежнее состояние и сохраняет возможность повторить попытку. Маркер принимается только для `<install>/.terminalv-update-<GUID>/payload`; переходы через junction/symlink запрещены. Очистка ограничена распознаваемыми именами staging/retired, не произвольным родителем строки из маркера.
- Фоновая проверка после `init`; кнопка в сайдбаре ставит обновление и перезапускает процесс.
- Подкоманда хоста `TerminalV update` / `TerminalV update --check` (`Cli/UpdateCommand.cs`): без окна, при открытом GUI не убивает сессии.

**Dependencies**: `HttpClient`, `System.IO.Compression`, GitHub Releases API.

### 5. **Дистрибуция**
**Type**: скрипты  
**Location**: `install.ps1`, `scripts/publish.ps1`, `scripts/verify-package.ps1`

**Purpose**: self-contained zip и установка через `irm | iex`

- `scripts/publish.ps1` — `dotnet publish` win-x64 single-file + COM + `wwwroot`, zip, SHA-256. Свежий staging для каждой сборки, без удаления `artifacts`; существующий ZIP/хеш не перезаписываются. `-OutputDirectory` выбирает новый каталог пакета.
- `scripts/verify-package.ps1` — Release-сборка, тесты локального ZIP/CLI/обновления, ConPTY и SQLite, затем браузерные тесты с `TERMINALV_TEST_APP_ROOT`, указывающим на UI из архива. `TerminalV.Package.Tests` использует локальный `IReleaseSource` и временные копии файлов, без сети, рабочей базы, GUI и пользовательского host pipe.
- `install.ps1` — ASCII-only, совместим с Windows PowerShell 5.1 и `irm | iex`.

---

## Directory Structure

```
TerminalV/
├── src/TerminalV/          # WPF-хост
│   ├── Cli/                # TerminalV update / --help
│   ├── Host/               # JSON-мост с WebView2
│   ├── Pty/                # ConPTY
│   ├── App.xaml(.cs)
│   └── MainWindow.xaml(.cs)
├── ui/                     # исходники вкладок и xterm
│   ├── src/main.js
│   └── src/styles.css
├── docs/usage.md           # клавиши и вкладки
├── scripts/publish.ps1     # релизный zip
├── install.ps1             # irm | iex
├── ProjectContext.md
├── README.md
└── TerminalV.slnx
```

---

## Protocol (WebView2)

Renderer → host:

| `type` | Поля | Действие |
|--------|------|----------|
| `create` | `id`, `cols`, `rows` | Запустить оболочку |
| `write` | `id`, `data` | Записать во вход ConPTY |
| `resize` | `id`, `cols`, `rows` | `ResizePseudoConsole` |
| `kill` | `id` | Закрыть сессию |
| `clipboard-read` / `clipboard-write` | `requestId`, `data` | Буфер обмена Windows |

Host → renderer: `init`, `data`, `exit`, `error`, `clipboard-data`, `update`.

Дополнительно renderer → host: `update-check`, `update-apply`.

`persist-sessions` передаёт `sessions` и `layouts`: лес бинарных деревьев с листьями `{sessionId}` и разделениями `{axis: "columns" | "rows", ratio, first, second}`. Расположение сохраняется в ключе `layouts` таблицы `settings` в одной транзакции со списком сессий; `init` возвращает его отдельно от настроек оформления. `Data/PaneLayout` отбрасывает неизвестные, скрытые и повторные ссылки, а оставшиеся сессии открывает отдельно. Сессия по-прежнему имеет один ID и один процесс; несколько связанных сессий могут быть видимы одновременно, но фокус ввода только один. Профильные сессии без живого процесса не запускаются автоматически при восстановлении расположения.

---

## Build Configuration

### Development

```powershell
dotnet build
dotnet run --project src/TerminalV
```

Перед компиляцией target `BuildUi` делает `npm install` и `npm run build` в `ui/`.

### Release

```powershell
.\scripts\publish.ps1
```

Результат: `artifacts/TerminalV-win-x64.zip` и `artifacts/TerminalV-win-x64.zip.sha256`.

### Smoke

```powershell
.\src\TerminalV\bin\Debug\net10.0-windows\TerminalV.exe --smoke
```

Код 0 и файл `smoke-result.txt` со строкой `ok`, если ConPTY ответил на `echo TERMINALV_SMOKE_OK`.

---

## Development Status

### Implemented Features ✅

- [x] Вертикальные вкладки и несколько сессий PowerShell
- [x] ConPTY: ввод, вывод, resize, завершение процесса
- [x] Копирование / вставка, правый клик
- [x] Переименование вкладки, индикатор активности
- [x] `irm` установщик и self-contained релиз win-x64
- [x] `--smoke` для проверки ConPTY
- [x] Самообновление: проверка GitHub Releases при старте, SHA-256, замена exe, откат, перезапуск

### Planned 📋

- [ ] Профили оболочек (cmd, WSL)
- [ ] Split-панели
- [ ] Настройки шрифта и темы
- [ ] Иконка приложения

### Known Issues ⚠️

- Без WebView2 Runtime окно покажет ошибку и закроется.
- `wwwroot` должен лежать рядом с `TerminalV.exe` (`AppContext.BaseDirectory`).
- Однофайловая публикация не включает `wwwroot` внутрь бандла: каталог копируется рядом после `Publish`.

---

## Document Information

Оформление v0.5.1: `ui/src/chrome.css` — визуальный слой поверх базовых стилей терминала, `ui/src/icons.js` — локальные SVG без внешних ресурсов. Контекст рабочей области строится из активной сессии через `textContent`. Ширина свёрнутого сайдбара не оставляет искусственный отступ у одиночного терминала. `appearance-ui.fixture.html` проверяет темы, контекст, фокус, узкое окно, диалог вставки и пустое состояние. Для скриншота задайте `TERMINALV_DESIGN_SCREENSHOT`, для варианта — `TERMINALV_DESIGN_VIEW` (`dark`, `light`, `settings`, `compact`, `empty`); затем запустите `node --test --test-name-pattern=appearance tests/selection.test.js` из `ui` после сборки. Мост в этой fixture тестовый: реальные PTY и буфер обмена не используются.

**Last Updated**: 2026-09-14
**Version**: 0.5.1
**Status**: Active  
**Repository**: `https://github.com/XYphrodite/TerminalV`  
**Workspace**: `C:\Repos\TerminalV`
