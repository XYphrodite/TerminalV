# terminalv.ai-limits

Пакет расширения TerminalV: лимиты и доступность AI-CLI в сайдбаре.

## Провайдеры

| id | Источник данных | Что показывает |
| --- | --- | --- |
| claude | `~/.claude.json` → `cachedUsageUtilization` | 5 часов / 7 дней, `resets_at` |
| kimi | `~/.kimi-code/statusline-tokens.json` → `official` | 5 часов / 7 дней |
| codex | `~/.codex/auth.json` | установлен / авторизован |
| grok | `~/.grok/bin`, `auth.json`, `version.json` | установлен / авторизован / версия |
| agy | `agy -p /usage --output-format json` (фоновый вызов) | 5 часов / 7 дней по группам моделей |
| mimo | `~/.mimocode\bin\mimo.exe` | установлен |
| muse | `~/bin/muse.bat` | установлен |

Сетевых запросов нет. Пути переопределяются через `setOverride` (`key`, `path`).

## Методы host

- `getState` — снимок
- `refresh` — принудительная проверка, событие `changed`
- `setInterval` `{seconds}` — период 30…3600 с (по умолчанию 300)
- `setOverride` `{key, path}` — свой путь к бинарю/конфигу

## Установка

```powershell
TerminalV add-extension https://github.com/XYphrodite/TerminalV/releases/download/<tag>/terminalv.ai-limits.zip
# или локальный zip
TerminalV add-extension .\terminalv.ai-limits.zip
```

Либо вручную: распаковать в `%LOCALAPPDATA%\TerminalV\extensions\terminalv.ai-limits\` и включить в настройках.

## Сборка

```powershell
dotnet build examples/extensions/terminalv.ai-limits/host/AiLimitsExtension.csproj -c Release -o artifacts/ai-limits-host
# скопировать extension.json и ui/ в пакет, host/ — из artifacts/ai-limits-host
```

Workflow `extensions` и release собирают `terminalv.ai-limits.zip`.
