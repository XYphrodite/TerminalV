# Расширения TerminalV

Desktop API v1 поддерживает гибридные пакеты: C# DLL для фоновой логики и ES-модуль для интерфейса. Можно поставлять только C# или только JS. Встроенного монитора лимитов нет; его можно реализовать отдельным пакетом через этот API.

## Установка и управление

1. В настройках откройте **Расширения → Открыть папку расширений**.
2. Поместите пакет в `%LOCALAPPDATA%\TerminalV\extensions\publisher.name\`. Имя папки должно совпадать с `id`.
3. Перезапустите TerminalV: пакет появится в списке выключенным.
4. Включите его и перезапустите приложение. Отключение тоже применяется после перезапуска; до этого расширение продолжает работать.

Для обновления или удаления закройте приложение, замените или удалите папку пакета и запустите его снова. Настройки и данные сохраняются отдельно в `%LOCALAPPDATA%\TerminalV\extension-data\`, вне каталога установки и `wwwroot`. При замене пакета с тем же `id` сохраняется его состояние включения. Не меняйте файлы работающего расширения.

DLL и JS выполняются с правами TerminalV. `AssemblyLoadContext` разделяет зависимости, но не создаёт песочницу; JS имеет доступ к DOM и WebView bridge. Устанавливайте доверенные пакеты. Декларативных разрешений, магазина, проверки подписей, автоматического обновления и поддержки расширений VS Code в этой версии нет. API доступен только на Windows Desktop; мобильный клиент расширения не загружает.

## Структура пакета

```text
publisher.name/
  extension.json
  host/
    MyExtension.dll
    MyExtension.deps.json
    ...зависимости DLL...
  ui/
    main.js
    ...JS/CSS/ресурсы...
```

```json
{
  "id": "publisher.name",
  "name": "My extension",
  "version": "1.0.0",
  "apiVersion": 1,
  "description": "Описание расширения",
  "host": { "assembly": "host/MyExtension.dll", "type": "MyExtension.Entry" },
  "ui": "ui/main.js"
}
```

`host` и `ui` необязательны по отдельности; нужен хотя бы один. Поддерживается только `apiVersion: 1`. Пути относительные, с `/`, без `..`, абсолютных путей и ссылок/junctions в пути к точкам входа. UI получает собственный virtual host, отображающий только папку `ui/`; используйте относительные импорты. Для npm-зависимостей соберите расширение в браузерный ES-модуль заранее. Общего Node.js runtime нет.

## C# SDK

Контракт — отдельный проект `src/TerminalV.Extensibility`, целевая платформа `net10.0`. Расширение может использовать `net10.0-windows`, если нужны Windows API. Публичный класс с конструктором без аргументов реализует `ITerminalVExtension`:

```csharp
Task ActivateAsync(IExtensionContext context, CancellationToken lifetime);
Task<JsonElement?> InvokeAsync(string method, JsonElement? args, CancellationToken cancellationToken);
ValueTask DisposeAsync();
```

`ActivateAsync` должен быстро вернуть управление: запустите фоновые задачи отдельно, передайте им `lifetime`, сохраните Task и дождитесь его в `DisposeAsync`. HTTP, таймеры и работа с файлами — обычные .NET API в DLL. Нельзя обращаться к WPF из этих обработчиков: они выполняются в пуле потоков. При закрытии TerminalV отменяет `lifetime` и вызывает `DisposeAsync`.

`InvokeAsync` получает запросы из JS именно этого расширения. Обработчики могут выполняться параллельно; синхронизируйте общее состояние. Активация ограничена 10 секундами, запрос — 30 секундами, завершение активного расширения — 3 секундами. Таймаут прекращает ожидание, но не может принудительно остановить зависший код внутри процесса. Обрабатывайте cancellation и ошибки собственных фоновых задач. Обычная ошибка активации отражается в настройках и не останавливает запуск других расширений; фатальные ошибки и зависание JS способны затронуть приложение.

| Контекст | Назначение |
| --- | --- |
| `Id` | Идентификатор пакета |
| `DataDirectory` | Постоянная папка файлов расширения |
| `Storage.Get(key)` / `Set(key, value)` | Строковые значения SQLite, отдельная область для каждого расширения |
| `Secrets.Get(key)` / `Set(key, value)` | SQLite + DPAPI CurrentUser; доступны через SDK только из C# |
| `Publish(eventName, object)` | JSON-событие в JS своего расширения |
| `Log(message)` | Диагностический журнал TerminalV |

`Set(key, null)` удаляет значение. Ключи — 1–200 символов, значения — до 1 048 576 символов. Для объектов сериализуйте JSON. Секреты не попадают в каталог расширений или `init`; отправляйте в UI только нужные публичные поля. DPAPI не имеет fallback в открытый текст. Это удобный API хранения, а не изоляция от других доверенных DLL.

Задайте `<EnableDynamicLoading>true</EnableDynamicLoading>` в проекте расширения. Ссылайтесь на SDK с `<Private>false</Private>` и `<ExcludeAssets>runtime</ExcludeAssets>`; SDK предоставляет TerminalV. Доставляйте DLL, `.deps.json` и зависимости вместе. Этот подход следует [модели загрузки плагинов .NET](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support).

## JS API

```js
export async function activate(api) {
  const panel = api.ui.createSidebarPanel({ id: "status", title: "Состояние" });
  const render = state => { panel.element.textContent = state.message; };
  api.host.on("changed", render);
  render(await api.host.invoke("getState"));
}
```

| API | Назначение |
| --- | --- |
| `host.invoke(method, args?)` | Promise с результатом C# `InvokeAsync` или ошибкой |
| `host.on(event, listener)` | Подписка на события, возвращает disposable |
| `storage.get/set/delete` | Асинхронный доступ к обычному `Storage` того же пакета |
| `ui.createSidebarPanel({id, title})` | Панель в сайдбаре |
| `ui.createSettingsPanel({id, title})` | Панель внутри настроек расширений |
| `ui.showNotification(text)` | Уведомление с названием расширения |
| `commands.register(id, title, handler)` | Команда; кнопка отображается в настройках |
| `commands.execute(id, ...args)` | Вызов команды своего пакета |
| `signal` | AbortSignal срока жизни UI |
| `subscriptions` | Массив cleanup-функций или объектов с `dispose()` |

Панели возвращают `{element, setBadge(value), dispose()}`. `element` — контейнер для собственного DOM. CSS подключайте из модуля через `new URL('./style.css', import.meta.url)` и `<link>`; добавьте удаление `<link>` в `subscriptions`. Ограничивайте селекторы корнем собственной панели. Сайдбар скрывает панели при сворачивании списка сессий.

Ресурсы, созданные через API панелей, команд и событий, регистрируются для очистки автоматически. `activate` может также вернуть cleanup-функцию или disposable. UI-активация ограничена 15 секундами. Для своих таймеров регистрируйте `clearInterval` в `subscriptions`, для DOM listeners используйте `{ signal: api.signal }`. Не запускайте побочных действий на верхнем уровне модуля. Ошибки активации и обработчиков host-событий показываются в настройках; отклонения Promise из своих DOM-обработчиков обрабатывайте самостоятельно.

События не сохраняются для последующего воспроизведения. После подписки запросите актуальное состояние у C# через `getState` (или свой метод). Повторный `init` не активирует JS второй раз. `extensions:result` сопоставляется с запросом по `requestId` и `extensionId`; поздний ответ после таймаута игнорируется. Обычные данные хранилища доступны C# и JS, секреты через JS API не выдаются.

## Рабочий пример

`examples/extensions/terminalv.hello` содержит счётчик, панель, команду, сохранение состояния и фоновое событие C# каждые 30 секунд. Он не устанавливается автоматически.

Соберите пример в CI или на машине для сборок:

```powershell
dotnet build examples/extensions/terminalv.hello/host/HelloExtension.csproj -c Release -o artifacts/hello-host
```

Создайте пакет `terminalv.hello`: скопируйте `extension.json` и `ui/` из примера, а содержимое `artifacts/hello-host/` — в его `host/`. Установите пакет по шагам выше. Workflow `extensions` собирает пример, приложение и запускает тесты на Windows; также публикует ZIP примера как CI artifact.
