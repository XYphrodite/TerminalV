using System.Text.Json;
using System.Text.Json.Serialization;

namespace AiLimits;

internal static class Json
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };
}

internal sealed class LimitWindow
{
    public string Id { get; set; } = "";
    public string Title { get; set; } = "";
    public double? UsedPercent { get; set; }
    public double? Used { get; set; }
    public double? Limit { get; set; }
    public DateTimeOffset? ResetsAt { get; set; }
}

internal sealed class ServiceStatus
{
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public bool Installed { get; set; }
    public bool Authenticated { get; set; }
    // AuthOk = credentials look usable right now (not just present on disk).
    public bool AuthOk { get; set; }
    // LimitsStale = the limit snapshot is too old to trust.
    public bool LimitsStale { get; set; }
    public string? Path { get; set; }
    public string? Account { get; set; }
    public string? Detail { get; set; }
    public List<LimitWindow> Limits { get; set; } = [];
    public DateTimeOffset CheckedAt { get; set; }
}

/// <summary>Reads local CLI state files. No network calls, no secrets leave the host.</summary>
internal static class Providers
{
    public static ServiceStatus[] Collect(IReadOnlyDictionary<string, string> overrides)
    {
        var now = DateTimeOffset.UtcNow;
        return
        [
            Guard("claude", "Claude", () => Claude(overrides, now), now),
            Guard("codex", "Codex", () => Codex(overrides, now), now),
            Guard("grok", "Grok", () => Grok(overrides, now), now),
            Guard("agy", "agy", () => Agi(overrides, now), now),
            Guard("mimo", "MiMo", () => Mimo(overrides, now), now),
            Guard("kimi", "Kimi", () => Kimi(overrides, now), now),
            Guard("muse", "Muse", () => Muse(overrides, now), now)
        ];
    }

    static ServiceStatus Guard(string id, string name, Func<ServiceStatus> probe, DateTimeOffset now)
    {
        try
        {
            return probe();
        }
        catch (Exception ex)
        {
            return new ServiceStatus
            {
                Id = id,
                Name = name,
                Detail = ex.Message,
                CheckedAt = now
            };
        }
    }

    static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
    static string LocalApp => Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

    static string Resolve(IReadOnlyDictionary<string, string> overrides, string key, string fallback)
        => overrides.TryGetValue(key, out var custom) && !string.IsNullOrWhiteSpace(custom) ? custom : fallback;

    static bool Exists(string? path) => !string.IsNullOrWhiteSpace(path) && File.Exists(path);

    static JsonElement? ReadJson(string? path)
    {
        if (!Exists(path)) return null;
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(path!));
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    static string? StringProp(JsonElement element, string name)
        => element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() : null;

    static DateTimeOffset? ParseDate(string? raw)
        => DateTimeOffset.TryParse(raw, out var value) ? value : null;

    static ServiceStatus Claude(IReadOnlyDictionary<string, string> overrides, DateTimeOffset now)
    {
        var binary = Resolve(overrides, "claude.bin", Path.Combine(Home, ".local", "bin", "claude.exe"));
        var config = Resolve(overrides, "claude.config", Path.Combine(Home, ".claude.json"));
        var credentials = Resolve(overrides, "claude.credentials", Path.Combine(Home, ".claude", ".credentials.json"));
        var status = new ServiceStatus
        {
            Id = "claude",
            Name = "Claude",
            Installed = Exists(binary),
            Path = Exists(binary) ? binary : null,
            CheckedAt = now
        };

        // OAuth freshness from Claude Code's credential store.
        var expiresAt = ReadOAuthExpiry(credentials);
        if (expiresAt is not null)
        {
            status.Authenticated = true;
            status.AuthOk = expiresAt > now;
            if (!status.AuthOk)
                status.Detail = $"OAuth истёк {expiresAt.Value.ToLocalTime():g} — нужен `claude login`.";
        }

        var root = ReadJson(config);
        if (root is null)
        {
            if (status.Detail is null) status.Detail = "Конфиг ~/.claude.json не найден.";
            return status;
        }
        var json = root.Value;
        if (json.TryGetProperty("oauthAccount", out var account))
        {
            status.Authenticated = true;
            status.Account = StringProp(account, "emailAddress");
            if (status.AuthOk != false && expiresAt is null) status.AuthOk = true;
        }
        if (json.TryGetProperty("cachedUsageUtilization", out var cache)
            && cache.TryGetProperty("utilization", out var util))
        {
            AddPercentWindow(status, util, "five_hour", "5 часов");
            AddPercentWindow(status, util, "seven_day", "7 дней");
            DateTimeOffset? fetchedAt = null;
            if (cache.TryGetProperty("fetchedAtMs", out var fetched) && fetched.ValueKind == JsonValueKind.Number
                && fetched.TryGetInt64(out var ms) && ms > 0)
                fetchedAt = DateTimeOffset.FromUnixTimeMilliseconds(ms);
            if (fetchedAt is not null)
            {
                var age = now - fetchedAt.Value;
                status.LimitsStale = age > TimeSpan.FromHours(12);
                var ageLabel = age.TotalHours >= 1
                    ? $"{(int)age.TotalHours}ч {age.Minutes}м"
                    : $"{Math.Max(1, (int)age.TotalMinutes)}м";
                status.Detail = status.AuthOk == false
                    ? status.Detail
                    : status.LimitsStale
                        ? $"Кэш лимитов устарел: {ageLabel} назад ({fetchedAt.Value.ToLocalTime():g})."
                        : $"Кэш лимитов от {fetchedAt.Value.ToLocalTime():g} ({ageLabel} назад).";
            }
            else
            {
                status.LimitsStale = true;
                status.Detail = "Кэш лимитов без времени — данные ненадёжны.";
            }
            // Token expiry unknown (0/missing) + old cache => do not look "ok".
            if (expiresAt is null && status.LimitsStale && status.Detail is not null
                && !status.Detail.Contains("login", StringComparison.OrdinalIgnoreCase))
            {
                status.AuthOk = false;
                status.Detail += " Авторизация не подтверждена — нужен `claude login`.";
            }
        }
        else if (status.Detail is null)
        {
            status.Detail = "Данные лимитов ещё не закэшированы.";
        }
        return status;
    }

    static DateTimeOffset? ReadOAuthExpiry(string? credentialsPath)
    {
        var root = ReadJson(credentialsPath);
        if (root is not { } json) return null;
        if (!json.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind is not JsonValueKind.Object)
            return null;
        var access = Expiry(oauth, "expiresAt");
        var refresh = Expiry(oauth, "refreshTokenExpiresAt");
        if (access is null) return refresh;
        if (refresh is null) return access;
        return access > refresh ? access : refresh;
    }

    static DateTimeOffset? Expiry(JsonElement oauth, string name)
    {
        if (!oauth.TryGetProperty(name, out var exp) || exp.ValueKind != JsonValueKind.Number
            || !exp.TryGetInt64(out var ms) || ms <= 0)
            return null;
        return DateTimeOffset.FromUnixTimeMilliseconds(ms);
    }

    static void AddPercentWindow(ServiceStatus status, JsonElement util, string key, string title)
    {
        if (!util.TryGetProperty(key, out var window) || window.ValueKind is not JsonValueKind.Object) return;
        var limit = new LimitWindow { Id = key, Title = title };
        if (window.TryGetProperty("utilization", out var used) && used.ValueKind == JsonValueKind.Number
            && used.TryGetDouble(out var percent))
            limit.UsedPercent = percent;
        limit.ResetsAt = ParseDate(StringProp(window, "resets_at"));
        if (window.TryGetProperty("used_dollars", out var ud) && ud.ValueKind == JsonValueKind.Number
            && ud.TryGetDouble(out var usedDollars))
            limit.Used = usedDollars;
        if (window.TryGetProperty("limit_dollars", out var ld) && ld.ValueKind == JsonValueKind.Number
            && ld.TryGetDouble(out var limitDollars))
            limit.Limit = limitDollars;
        status.Limits.Add(limit);
    }

    static ServiceStatus Kimi(IReadOnlyDictionary<string, string> overrides, DateTimeOffset now)
    {
        var binary = Resolve(overrides, "kimi.bin", Path.Combine(Home, ".kimi-code", "bin", "kimi.exe"));
        var statusFile = Resolve(overrides, "kimi.status", Path.Combine(Home, ".kimi-code", "statusline-tokens.json"));
        var status = new ServiceStatus
        {
            Id = "kimi",
            Name = "Kimi",
            Installed = Exists(binary),
            Path = Exists(binary) ? binary : null,
            CheckedAt = now
        };
        var root = ReadJson(statusFile);
        if (root is null)
        {
            status.Detail = "Файл statusline-tokens.json не найден.";
            return status;
        }
        var json = root.Value;
        if (json.TryGetProperty("official", out var official) && official.ValueKind is not JsonValueKind.Null)
        {
            status.Authenticated = true;
            status.AuthOk = true;
            status.Limits.Add(RatioWindow(official, "h5_used", "h5_limit", "h5_reset", "5 часов"));
            status.Limits.Add(RatioWindow(official, "wk_used", "wk_limit", "wk_reset", "7 дней"));
            status.Detail = "Лимиты из statusline-tokens.json";
            if (json.TryGetProperty("ts", out var ts) && ts.ValueKind == JsonValueKind.Number
                && ts.TryGetDouble(out var unix) && unix > 0)
            {
                var fetchedAt = DateTimeOffset.FromUnixTimeSeconds((long)unix);
                var age = now - fetchedAt;
                status.LimitsStale = age > TimeSpan.FromHours(12);
                if (status.LimitsStale)
                    status.Detail = $"Кэш лимитов устарел: {(int)age.TotalHours}ч {age.Minutes}м назад.";
            }
        }
        else
        {
            status.Detail = "Официальные лимиты ещё не записаны.";
        }
        return status;
    }

    static LimitWindow RatioWindow(JsonElement source, string usedKey, string limitKey, string resetKey, string title)
    {
        var window = new LimitWindow { Id = title, Title = title };
        if (source.TryGetProperty(usedKey, out var used) && used.ValueKind == JsonValueKind.Number
            && used.TryGetDouble(out var usedValue))
            window.Used = usedValue;
        if (source.TryGetProperty(limitKey, out var limit) && limit.ValueKind == JsonValueKind.Number
            && limit.TryGetDouble(out var limitValue))
        {
            window.Limit = limitValue;
            if (limitValue > 0 && window.Used is double usedValue2)
                window.UsedPercent = Math.Round(usedValue2 / limitValue * 100, 1);
        }
        window.ResetsAt = ParseDate(StringProp(source, resetKey));
        return window;
    }

    static ServiceStatus Codex(IReadOnlyDictionary<string, string> overrides, DateTimeOffset now)
    {
        var binary = Resolve(overrides, "codex.bin",
            Path.Combine(Home, ".codex", ".sandbox-bin", "codex.exe"));
        var auth = Resolve(overrides, "codex.auth", Path.Combine(Home, ".codex", "auth.json"));
        var status = new ServiceStatus
        {
            Id = "codex",
            Name = "Codex",
            Installed = Exists(binary),
            Path = Exists(binary) ? binary : null,
            CheckedAt = now
        };
        var root = ReadJson(auth);
        if (root is null)
        {
            status.Detail = "~/.codex/auth.json не найден.";
            return status;
        }
        status.Authenticated = true;
        status.AuthOk = true;
        var json = root.Value;
        var mode = StringProp(json, "auth_mode");
        status.Account = mode;
        status.Detail = mode is null ? "Авторизация сохранена." : $"Режим авторизации: {mode}";
        return status;
    }

    static ServiceStatus Grok(IReadOnlyDictionary<string, string> overrides, DateTimeOffset now)
    {
        var binary = Resolve(overrides, "grok.bin", Path.Combine(Home, ".grok", "bin", "grok.exe"));
        var auth = Resolve(overrides, "grok.auth", Path.Combine(Home, ".grok", "auth.json"));
        var versionFile = Resolve(overrides, "grok.version", Path.Combine(Home, ".grok", "version.json"));
        var status = new ServiceStatus
        {
            Id = "grok",
            Name = "Grok",
            Installed = Exists(binary),
            Path = Exists(binary) ? binary : null,
            CheckedAt = now
        };
        var version = ReadJson(versionFile);
        if (version is { } v)
        {
            var ver = StringProp(v, "version");
            if (ver is not null) status.Detail = $"v{ver}";
        }
        var root = ReadJson(auth);
        if (root is not null)
        {
            status.Authenticated = true;
            status.AuthOk = true;
            status.Account = "xAI OAuth";
        }
        else
        {
            status.Detail = string.IsNullOrWhiteSpace(status.Detail)
                ? "~/.grok/auth.json не найден."
                : status.Detail + " · нет авторизации";
        }
        return status;
    }

    static ServiceStatus Agi(IReadOnlyDictionary<string, string> overrides, DateTimeOffset now)
    {
        var binary = Resolve(overrides, "agy.bin", Path.Combine(LocalApp, "agy", "bin", "agy.exe"));
        var installed = Exists(binary);
        return new ServiceStatus
        {
            Id = "agy",
            Name = "agy",
            Installed = installed,
            Authenticated = installed,
            AuthOk = installed,
            Path = installed ? binary : null,
            Detail = installed ? "CLI установлен." : "agy.exe не найден.",
            CheckedAt = now
        };
    }

    static ServiceStatus Mimo(IReadOnlyDictionary<string, string> overrides, DateTimeOffset now)
    {
        var binary = Resolve(overrides, "mimo.bin", Path.Combine(Home, ".mimocode", "bin", "mimo.exe"));
        var installed = Exists(binary);
        return new ServiceStatus
        {
            Id = "mimo",
            Name = "MiMo",
            Installed = installed,
            Authenticated = installed,
            AuthOk = installed,
            Path = installed ? binary : null,
            Detail = installed ? "CLI установлен. Статистика — через `mimo stats`." : "mimo.exe не найден.",
            CheckedAt = now
        };
    }

    static ServiceStatus Muse(IReadOnlyDictionary<string, string> overrides, DateTimeOffset now)
    {
        var binary = Resolve(overrides, "muse.bin", Path.Combine(Home, "bin", "muse.bat"));
        var installed = Exists(binary);
        return new ServiceStatus
        {
            Id = "muse",
            Name = "Muse",
            Installed = installed,
            Authenticated = installed,
            AuthOk = installed,
            Path = installed ? binary : null,
            Detail = installed ? "WSL-обёртка установлена." : "muse.bat не найден.",
            CheckedAt = now
        };
    }
}
