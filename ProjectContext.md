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
- Ярлыки: установщик создаёт пользовательский «Пуск» по умолчанию, `-DesktopShortcut` добавляет рабочий стол, `-NoShortcut` запрещает оба. В настройках UI есть отдельное действие «Создать ярлыки» (также для ZIP). `Shell/ShortcutService.cs` создаёт `.lnk` через WSH на отдельном STA-потоке; пути берутся из Windows SpecialFolder, цель — только текущий `TerminalV.exe`. Мост принимает лишь выбор расположений и requestId, не пути/команды. Результат отдельный для каждого расположения; UI защищён от повторной отправки и устаревших ответов. Существующие ссылки на другие копии/с аргументами не перезаписываются, настройки своих ярлыков сохраняются. Иконка — `<exe>,0`, уведомление Shell адресное, без перезапуска Explorer или автозакрепления. При обычном запуске/самообновлении ярлыки не создаются.
- Проверки ярлыков: `dotnet run --project tests/TerminalV.Shell.Tests -c Release`, `powershell.exe -NoProfile -ExecutionPolicy Bypass -File tests/installer-shortcuts.tests.ps1` и `shortcuts-ui.fixture.html`. Native/installer тесты работают с реальными `.lnk` исключительно во временных каталогах и подменяют known folders/уведомления; браузер использует тестовый мост без PTY. Обе дополнительные проверки включены в `verify-package.ps1`.

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
- [x] Иконка приложения: голубая V с курсором на графитовом фоне, 9 размеров ICO

### Planned 📋

- [ ] Профили оболочек (cmd, WSL)
- [ ] Split-панели
- [ ] Настройки шрифта и темы

### Known Issues ⚠️

- Без WebView2 Runtime окно покажет ошибку и закроется.
- `wwwroot` должен лежать рядом с `TerminalV.exe` (`AppContext.BaseDirectory`).
- Однофайловая публикация не включает `wwwroot` внутрь бандла: каталог копируется рядом после `Publish`.

---

## Document Information

Иконка v0.5.4: `scripts/make-icon.ps1` рисует геометрическую голубую `V_` на графитовом фоне без зависимости от шрифта. Курсор выровнен по пикселям; `Assets/TerminalV.ico` содержит 32-битные кадры 16, 20, 24, 32, 40, 48, 64, 128 и 256 px. Один ресурс используется в EXE, WPF-окне и заголовке. Параметр `-PreviewPath` создаёт PNG с крупным знаком и реальными маленькими размерами на светлом/тёмном фоне; локальное превью — `artifacts/icon-preview.png`. Терминальные сессии и протокол host не менялись. Подробности выпуска — `docs/releases/v0.5.4.md`.

Автор v0.5.3: `ui/index.html` показывает «Автор: XYphrodite» под названием и версией; фиксированный адрес — `https://github.com/XYphrodite`. UI отменяет обычную навигацию и отправляет только `open-author`, включая средний клик; `TerminalBridge` открывает константный HTTPS-адрес через `UseShellExecute`, не принимает URL/команду из сообщения, освобождает объект Process и сообщает об ошибке запуска браузера. Нижняя часть sidebar остаётся в маленьком окне благодаря `min-height: 0`. `appearance-ui.fixture.html` проверяет ссылку, отсутствие PTY-ввода/создания/завершения сессий, независимость кнопки обновления, светлую/тёмную темы, узкий и свёрнутый sidebar. Превью: `artifacts/author-credit-preview.png`. Для этой UI-функции не нужен перезапуск фонового host; после установки достаточно перезапуска окна.

Быстрый запуск v0.5.2: `ui/src/launch-menu.js` отделяет запуск сохранённого профиля от редактора; `LaunchCatalog.cs` асинхронно обнаруживает Windows-оболочки и WSL. `wsl --list --quiet` читается как UTF-16, с таймаутом 5 секунд, без запуска Linux; служебные дистрибутивы Docker/Rancher скрыты. Имя WSL передаётся без кавычек и только после проверки символов (WSL #9792). `wslDistribution` хранится в сессии и SQLite; capability `wslLaunchSupported` запрещает запуск через несовместимый старый хост. WSL cwd не смешивается с Windows cwd; быстрый запуск/перезапуск открывает Linux home, редактор WSL и отслеживание Linux cwd пока не реализованы. Браузерная fixture `launch-menu` проверяет запуск без сохранения, редактирование, WSL, фокус, поздние ответы и узкое окно; скриншот — `TERMINALV_LAUNCH_SCREENSHOT`. PTY-интеграция WSL включается явно через `TERMINALV_TEST_WSL_DISTRIBUTION=Ubuntu`: только `/bin/sh -c` без профилей и записи файлов, без остановки дистрибутива. Рабочий TerminalV/host не запускать для тестов.

Окружение v0.5.2: `SessionEnvironment.cs` вызывает `CreateEnvironmentBlock` для текущего пользователя с `bInherit=false` (при `true` Windows сохраняет старый PATH), затем добавляет отсутствующие process-only переменные, кроме PATH. Unicode-блок сортируется без учёта регистра, завершается двойным NUL и передаётся в `CreateProcessW` с `CREATE_UNICODE_ENVIRONMENT`; оба нативных блока освобождаются через SafeHandle. Окружение хоста не меняется; кэш, запись в реестр и инъекция команд в живые сессии отсутствуют. `environmentRefreshSupported` в протоколе позволяет UI предупредить о старом host без его остановки. Для первого перехода пользователь сохраняет работу и перезагружает Windows. `EnvironmentTests.cs` проверяет stale PATH, Unicode, process-only переменные, PowerShell 5.1/7 и cmd, сохранность уже живой оболочки. `TERMINALV_TEST_CODEX_PATH` включает поиск конкретного установленного Codex через `Get-Command`, без запуска CLI. Пользовательские shell-профили в тестах не загружаются.

Оформление v0.5.1: `ui/src/chrome.css` — визуальный слой поверх базовых стилей терминала, `ui/src/icons.js` — локальные SVG без внешних ресурсов. Контекст рабочей области строится из активной сессии через `textContent`. Ширина свёрнутого сайдбара не оставляет искусственный отступ у одиночного терминала. `appearance-ui.fixture.html` проверяет темы, контекст, фокус, узкое окно, диалог вставки и пустое состояние. Для скриншота задайте `TERMINALV_DESIGN_SCREENSHOT`, для варианта — `TERMINALV_DESIGN_VIEW` (`dark`, `light`, `settings`, `compact`, `empty`); затем запустите `node --test --test-name-pattern=appearance tests/selection.test.js` из `ui` после сборки. Мост в этой fixture тестовый: реальные PTY и буфер обмена не используются.

Проверки релизного ZIP v0.5.2: 169 Node/браузерных, 31 PTY/транспортная (PowerShell 5.1/7, Codex PATH, cmd и установленная Ubuntu), 11 SQLite и 21 пакет/CLI/изолированное обновление — всего 232, без пропусков при заданных `TERMINALV_TEST_PWSH`, `TERMINALV_TEST_WSL_DISTRIBUTION` и `TERMINALV_TEST_CODEX_PATH`. Release-сборка успешна; неблокирующие сообщения npm allow-scripts для esbuild и Vite о JS-бандле чуть больше 500 kB. Превью меню: `artifacts/launch-menu-preview.png`. Финальный пакет `artifacts/release-v0.5.2/final/TerminalV-win-x64.zip` — 66 629 536 байт, SHA-256 `69dd8b8fd02b539f199b95993217dc24d67e23c934818640ade64176c6c69c8b`. Логи: `verification-cefb1e239100462e91a23002e431dfac/` рядом с ZIP. Release отключает `IncludeSourceRevisionInInformationalVersion`, чтобы EXE показывал точную версию `0.5.2`, а не SHA предыдущего коммита. Первый проверенный кандидат сохранён в родительском каталоге. WPF/WebView2 с рабочим host не запускался, установленное приложение не обновлялось. Подробности — `docs/releases/v0.5.2.md`.

Проверки релизного ZIP v0.5.3: 171 Node/браузерная, 31 PTY/транспортная (PowerShell 5.1/7, Codex PATH, cmd, Ubuntu), 11 SQLite и 21 пакет/CLI/изолированное обновление — всего 234, без пропусков. Архив `artifacts/release-v0.5.3/TerminalV-win-x64.zip` — 66 629 910 байт, SHA-256 `e9d5d3fe50e2500ac32030cb3de232474dfc6feb4146da32f3da05f40b9a3c0d`. Логи — `verification-720555eb70f740328d2f48c9bc3f3db6/` рядом с ZIP. UI-fixtures используют распакованный архив; внешний браузер, рабочий WPF/host и установленное приложение не запускались и не обновлялись. Release-сборка успешна; неблокирующие сообщения npm allow-scripts и Vite о размере JS сохранены. Подробности — `docs/releases/v0.5.3.md`.

Проверки релизного ZIP v0.5.4: 171 Node/браузерная, 31 PTY/транспортная (PowerShell 5.1/7, Codex PATH, cmd, Ubuntu), 11 SQLite и 21 пакет/CLI/изолированное обновление — всего 234, без пропусков. Дополнительно WPF-декодер проверил 9 размеров ICO; новая иконка подтверждена в EXE из распакованного архива, ProductVersion — `0.5.4`. ZIP `artifacts/release-v0.5.4/TerminalV-win-x64.zip` — 66 623 737 байт, SHA-256 `a5f225239b054ec2c7e54bec0cf0a9c8621ea055a37ae23a423ec3e25df8c966`; логи — `verification-5d5219c12be64ddbb5bbfa58090f887c/` рядом с ZIP. UI-fixtures используют распакованный архив, установленное приложение/host и пользовательская база не затрагивались; ручная проверка WPF-окна и панели задач в отдельной Windows-среде не проводилась. Подробности — `docs/releases/v0.5.4.md`.

Ярлыки и релиз v0.5.5: «Пуск» по умолчанию, рабочий стол через `-DesktopShortcut`, выбор расположений и кнопка «Создать ярлыки» в настройках для установленной/ZIP-копии. Панель «Оформление» переименована в «Настройки». `-NoShortcut` запрещает создание обоих ярлыков. Полный прогон релизного ZIP: 180 Node/браузерных + 31 PTY/транспортная + 11 SQLite + 21 пакет/CLI/обновление + 9 Windows-ярлыков + 8 сценариев установщика = 260, без пропусков. ProductVersion EXE и интерфейс — `0.5.5`. Архив `artifacts/release-v0.5.5/TerminalV-win-x64.zip` — 66 629 212 байт, SHA-256 `c6d3aa281e1201eb43ce1f78f61afaa9bb9535a646861d4037c1452fdcd1f6f2`. Логи — `verification-711b155c439e4688a23e78bfb63481de/` рядом с ZIP. Ярлыки тестировались в изолированных каталогах; рабочая установка, пользовательские ярлыки, база и host не изменялись. Перезапуск фонового host для этой функции не нужен. Подробности — `docs/releases/v0.5.5.md`.

Вставка v0.5.6: большая вставка в `muse` режется на чанки по 4000/8192 (`ui/src/write-chunk.js`, `SessionClient.Write`, `ConPtySession.Write`) с сохранением `bracketed paste`, кириллицы и суррогатов `😀`. Один `write` не блокирует `NamedPipe`/`ConPTY` и `WebView2` на секунды. UI-часть добавляет `write-chunk.test.js` (5 тестов), хост-часть — `Program.cs` (`large paste is chunked`, 100 КБ `<500ms`). Ожидаемый полный прогон `verify-package.ps1`: 185 UI + 32 PTY + 11 Data + 21 Package + 9 Shell + 8 installer = 266 без пропусков. Перезапуск хоста не нужен. Подробности — `docs/releases/v0.5.6.md`.

Вставка v0.5.7: даже 100 символов с диалогом в `muse` — `TerminalBridge` (`Task.Run` для `write`) и `ConPtySession` (очередь `Task.Run(ProcessWriteQueue)`) не блокируют WebView2 UI на `WriteFile`, `postWrite` через `queueMicrotask` не блокирует `xterm.triggerDataEvent`. Добавлен тест `100 chars with dialog via microtask` (6 UI, 33 PTY). Ожидаемо `186+33+11+21+9+8=268` без пропусков. Подробности — `docs/releases/v0.5.7.md`.

Вставка v0.5.8: `v0.5.7` уже выпущен — тот же неблокирующий paste (синхронный ввод `"a"`, асинхронный `ESC[200~` paste) перепакован как `0.5.8` для публикации без перезаписи `v0.5.7` (условный `isPaste || >4000` в `main.js`/`TerminalBridge.cs`). Подробности — `docs/releases/v0.5.8.md`.

Сохранение сессий v0.5.9: исправлен баг — видимая сессия теперь сохраняет `Buffer/Cwd` как скрытая, пустой чат после перезапуска устранён (`AppDatabase.SaveSessions` хранит все сессии, `SessionRecord.Hidden` — только видимость). Добавлен дюрабл-тест `visible sessions preserve buffer...`. Подробности — `docs/releases/v0.5.9.md`.

Ссылки v0.5.10: клик по URL в терминале теперь открывает внешний браузер. Ранее `WebLinksAddon` делал `window.open` внутри WebView2 (встроенный popup). Теперь `ui/src/main.js` передаёт `open-link` через `TerminalBridge` + `linkHandler`, хост открывает только `http/https` через `UseShellExecute`, `MainWindow` перехватывает `NewWindowRequested`/`NavigationStarting`. Подробности — `docs/releases/v0.5.10.md`.

Разделение v0.5.11: кнопка «Справа»/«Снизу» теперь вставляет новую сессию рядом с родительской в списке слева, а не в конец. Ранее `tabs.push` всегда добавлял в конец; теперь `insertAfter` вставляет после `splitFrom`. Дочерние панели отмечаются отступом и тонкой линией-веткой (`split-child`/`split-parent` из `isSplitChild`/`isSplitParent`), свёрнутый сайдбар без отступа. Подробности — `docs/releases/v0.5.11.md`.

Кнопка сессии v0.5.12: `⋯` (управление сессией) теперь всегда видна — `chrome.css` переопределяет `opacity:0`→`1` (в свёрнутом остаётся скрытой). Подробности — `docs/releases/v0.5.12.md`.

Свёрнутый сайдбар v0.5.13: в свёрнутом режиме `⋯`/`×` теперь появляются на `hover/active/focus` как оверлей (`position:absolute` справа), а не исчезают полностью. Подробности — `docs/releases/v0.5.13.md`.

Мобильный SSH v0.6.0: лёгкий MAUI Blazor Hybrid `src/TerminalV.Mobile` (Android/iOS/Windows) с `xterm.js` + `SSH.NET` + двойной коннектор (прямо к `sshd` и через шлюз `TerminalV` WebSocket). `WPF` остаётся fallback для локального `ConPTY`. Подробности — `docs/releases/v0.6.0.md`.

Вставка v0.6.1: фикс зависания вкладки при 4K многострочной вставке в `muse` со сплитом 4 (`ConPtySession` раздельны `_queueGate/_writerGate`, slow-write лог), `Diagnostics/Diag.cs` → `%LOCALAPPDATA%\TerminalV\diagnostics.log`, `SessionClient/SessionHost/TerminalBridge` логи gate contention, UI `diag` канал. Добавлен durable тест `concurrent 4k paste writes don't deadlock`. Подробности — `docs/releases/v0.6.1.md`.

Ресайз v0.6.2: `scheduleFit` 80ms → `rAF` 16ms, `diag` вынесен глобально, `applyFit` лог. Добавлен `ui/tests/resize.test.js` (198 UI тестов). Подробности — `docs/releases/v0.6.2.md`.

Перезагрузка v0.6.3: автоподъём обычных `shell` после ребута (`hidden || startupCommand` → `exited`, иначе `create`), `diag restore`. Обновлён `session-restore.test.js` 6/6 (204 UI). Подробности — `docs/releases/v0.6.3.md`.

Вставка v0.6.5: `Diag` асинхронный батч 50 строк — устранена «по строчке» пауза при вставке в `muse` (восстановлен фикс `v0.6.4` без пример-теста). Подробности — `docs/releases/v0.6.5.md`.

Вставка v0.6.6: паузы между чанками — `postWrite` 1 микрозадача на все чанки, добавлен `write-chunk 7/7` тест `paste batch`. Подробности — `docs/releases/v0.6.6.md`.

TUI v0.6.7: рваный верх `muse` после ресайза — `applyFit` для `tui-lock` `webgl.clearTexture()+refresh(true)`, `tui-refresh.test.js` 2/2. Подробности — `docs/releases/v0.6.7.md`.

Вставка v0.6.8: точная причина — `N` `queueMicrotask`+`Task.Run`+`Send` на чанк 4000 давали секунды на 60К; теперь 1 батч. `write-chunk 7/7`, `Pty 25/25` 5К `1ms`. Подробности — `docs/releases/v0.6.8.md`.

Окно v0.6.9: `WebView2` блокировал `WM_NCHITTEST` — добавлен `WindowFrame` хит-тест 6px/36px, все 4 стороны + углы + `maximized` без ресайза, `Pty 25/25` новый `hit-test`. Подробности — `docs/releases/v0.6.9.md`.

Сайдбар v0.6.10: в `collapsed` `×` скрыт `display:none !important`, только `⋯`. Подробности — `docs/releases/v0.6.10.md`.

Окно v0.6.11: `WebView2` (дочерний `HWND`, airspace) съедал `WM_NCHITTEST` на краях — контент вставлен на `6px` (`ContentRoot` `Margin="6,0,6,6"`, в `maximized` `0`), полоса WPF для ресайза открыта, `Pty` новый `airspace fix` тест. Подробности — `docs/releases/v0.6.11.md`.

Диагностика v0.6.12: `ConPtySession.Resize` логирует `HRESULT` отказа (`pty: Resize failed ... hr=...`) — был немой игнор, из-за чего отказ ConPTY на маленьких размерах неотличим от бага рендера. Подробности — `docs/releases/v0.6.12.md`.

Окно v0.6.13: хук `WM_GETMINMAXINFO` ронял `MinWidth/MinHeight` — добавлен `ptMinTrackSize` из `MinWidth/MinHeight × DPI` (`ComputeMinTrackSize`, `internal`, масштаб свежий на каждый вызов). Подробности — `docs/releases/v0.6.13.md`.

Окно v0.6.14: минимум опущен до 560×360 (только `MinWidth`/`MinHeight` в XAML, хук v0.6.13 подхватил сам). Подробности — `docs/releases/v0.6.14.md`.

Вставка v0.6.15: тексты длиннее 4000 символов сохраняются хостом в `%TEMP%\TerminalV\pastes\`, в терминал вставляется путь (`paste-file-store`/`paste-file-stored`, фолбэк на текст при ошибке/таймауте, чистка в `Closed`). Подробности — `docs/releases/v0.6.15.md`.

Сайдбар v0.6.16: в свёрнутом режиме `.tab-options` скрыт всегда (`display:none !important`, без re-show на hover/active/focus). Подробности — `docs/releases/v0.6.16.md`.

Скролл v0.6.17: в `tui-lock` колесо уходит приложению при включённом mouse-режиме (`wantsAppWheel` в `ui/src/tui-scroll.js`, `wheelTarget` в `main.js`). Подробности — `docs/releases/v0.6.17.md`.

Панель v0.6.18: высота элементов сессий (`sessionDensity`: `standard`/`minimal`, класс `session-minimal`, поле `AppSettings.SessionDensity`). Подробности — `docs/releases/v0.6.18.md`.

Сплиты v0.7.1: запрет вложенности (`MAX_SPLIT_LEVEL = 1` в `splitSession`, `canSplit` только для одиночной/корневой-первой панели). Подробности — `docs/releases/v0.7.1.md`.

Рендеринг v0.7.2: `hardwareRendering` (дефолт вкл, `applyRenderer`, гейт в `ensureWebgl`, поле `AppSettings.HardwareRendering`). Подробности — `docs/releases/v0.7.2.md`.

**Last Updated**: 2026-09-23
**Version**: 0.7.2
**Status**: Active  
**Repository**: `https://github.com/XYphrodite/TerminalV`  
**Workspace**: `C:\Repos\TerminalV`
