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

    static readonly object GrokGate = new();
    static GrokQuota? _grokQuota;
    static bool _grokFetching;
    static readonly TimeSpan GrokMinInterval = TimeSpan.FromSeconds(60);
    static readonly TimeSpan GrokStaleAfter = TimeSpan.FromMinutes(30);

    sealed class GrokQuota
    {
        public DateTimeOffset FetchedAt { get; init; }
        public string? Email { get; init; }
        public string? SubscriptionTier { get; init; }
        public List<LimitWindow> Limits { get; init; } = [];
        public string? Error { get; init; }
    }

    static ServiceStatus Grok(IReadOnlyDictionary<string, string> overrides, DateTimeOffset now)
    {
        var binary = Resolve(overrides, "grok.bin", Path.Combine(Home, ".grok", "bin", "grok.exe"));
        var auth = Resolve(overrides, "grok.auth", Path.Combine(Home, ".grok", "auth.json"));
        var versionFile = Resolve(overrides, "grok.version", Path.Combine(Home, ".grok", "version.json"));
        var settingsCache = Path.Combine(Home, ".grok", "settings_cache.json");
        var installed = Exists(binary);
        var status = new ServiceStatus
        {
            Id = "grok",
            Name = "Grok",
            Installed = installed,
            Path = installed ? binary : null,
            CheckedAt = now
        };
        string? verLabel = null;
        if (ReadJson(versionFile) is { } v && StringProp(v, "version") is { } ver)
            verLabel = $"v{ver}";

        if (!installed)
        {
            status.Detail = "grok.exe не найден.";
            return status;
        }

        var root = ReadJson(auth);
        if (root is null)
        {
            status.Detail = verLabel is null
                ? "~/.grok/auth.json не найден."
                : $"{verLabel} · нет авторизации";
            return status;
        }

        status.Authenticated = true;
        status.AuthOk = true;

        var cachedTier = ReadGrokCachedTier(settingsCache);

        GrokQuota? cached;
        bool start = false;
        lock (GrokGate)
        {
            cached = _grokQuota;
            if (!_grokFetching && (cached is null || now - cached.FetchedAt >= GrokMinInterval))
                start = _grokFetching = true;
        }
        if (start) _ = Task.Run(() => FetchGrokQuota(binary));

        var tier = cached?.SubscriptionTier ?? cachedTier;
        var email = cached?.Email;
        status.Account = (email, tier) switch
        {
            ({ Length: > 0 } e, { Length: > 0 } t) => $"{e} · {t}",
            ({ Length: > 0 } e, _) => e,
            (_, { Length: > 0 } t) => $"xAI OAuth · {t}",
            _ => "xAI OAuth"
        };

        if (cached is null)
        {
            status.Detail = verLabel is null ? "Загрузка лимитов…" : $"{verLabel} · Загрузка лимитов…";
            return status;
        }
        if (cached.Limits.Count == 0)
        {
            var err = cached.Error ?? "Лимиты недоступны.";
            status.Detail = verLabel is null ? err : $"{verLabel} · {err}";
            return status;
        }

        status.Limits.AddRange(cached.Limits);
        var age = now - cached.FetchedAt;
        status.LimitsStale = age > GrokStaleAfter;
        var detail = status.LimitsStale
            ? $"Кэш лимитов устарел: {(int)age.TotalMinutes}м назад."
            : cached.Error is null
                ? "Лимиты из `grok agent`"
                : $"Лимиты из `grok agent` (ошибка обновления: {cached.Error})";
        status.Detail = verLabel is null ? detail : $"{verLabel} · {detail}";
        return status;
    }

    static string? ReadGrokCachedTier(string path)
    {
        if (ReadJson(path) is not { } root) return null;
        var payload = StringProp(root, "payload");
        if (string.IsNullOrWhiteSpace(payload)) return null;
        try
        {
            using var doc = JsonDocument.Parse(payload);
            return StringProp(doc.RootElement, "subscription_tier_display");
        }
        catch { return null; }
    }

    static void FetchGrokQuota(string binary)
    {
        GrokQuota result;
        try
        {
            result = RunGrokQuota(binary).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            result = new GrokQuota { FetchedAt = DateTimeOffset.UtcNow, Error = ex.Message };
        }
        lock (GrokGate)
        {
            if (result.Limits.Count == 0 && _grokQuota is { Limits.Count: > 0 } previous)
            {
                _grokQuota = new GrokQuota
                {
                    FetchedAt = previous.FetchedAt,
                    Email = result.Email ?? previous.Email,
                    SubscriptionTier = result.SubscriptionTier ?? previous.SubscriptionTier,
                    Limits = previous.Limits,
                    Error = result.Error
                };
            }
            else
            {
                _grokQuota = result;
            }
            _grokFetching = false;
        }
        try { AgyUpdated?.Invoke(); } catch { }
    }

    static async Task<GrokQuota> RunGrokQuota(string binary)
    {
        var now = DateTimeOffset.UtcNow;
        var info = new System.Diagnostics.ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
            StandardInputEncoding = new System.Text.UTF8Encoding(false)
        };
        info.ArgumentList.Add("agent");
        info.ArgumentList.Add("stdio");

        using var process = System.Diagnostics.Process.Start(info)
            ?? throw new InvalidOperationException("Не удалось запустить grok.");
        try
        {
            _ = process.StandardError.ReadToEndAsync();
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":1,\"method\":\"initialize\",\"params\":{\"protocolVersion\":1,\"clientCapabilities\":{}}}");
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":2,\"method\":\"_x.ai/billing\",\"params\":{}}");
            await process.StandardInput.WriteLineAsync("{\"jsonrpc\":\"2.0\",\"id\":3,\"method\":\"_x.ai/auth/info\",\"params\":{}}");
            await process.StandardInput.FlushAsync();

            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            JsonElement? billingResult = null;
            JsonElement? authResult = null;
            string? rpcError = null;

            while (!cts.IsCancellationRequested && (billingResult is null || authResult is null))
            {
                var line = await process.StandardOutput.ReadLineAsync(cts.Token);
                if (line is null) break;
                if (string.IsNullOrWhiteSpace(line) || !line.TrimStart().StartsWith('{')) continue;
                try
                {
                    using var doc = JsonDocument.Parse(line);
                    var root = doc.RootElement;
                    if (!root.TryGetProperty("id", out var idProp) || idProp.ValueKind != JsonValueKind.Number
                        || !idProp.TryGetInt32(out var id))
                        continue;

                    if (id == 2)
                    {
                        if (root.TryGetProperty("result", out var res))
                            billingResult = res.Clone();
                        else if (root.TryGetProperty("error", out var err))
                            rpcError = StringProp(err, "message") ?? "Ошибка получения биллинга Grok.";
                    }
                    else if (id == 3 && root.TryGetProperty("result", out var res))
                    {
                        authResult = res.Clone();
                    }
                }
                catch { }
            }

            return ParseGrokQuota(billingResult, authResult, rpcError, now);
        }
        finally
        {
            try { process.Kill(entireProcessTree: true); } catch { }
        }
    }

    static GrokQuota ParseGrokQuota(JsonElement? billingResult, JsonElement? authResult, string? rpcError, DateTimeOffset now)
    {
        string? email = authResult is { ValueKind: JsonValueKind.Object } auth
            ? StringProp(auth, "email")
            : null;

        if (billingResult is not { ValueKind: JsonValueKind.Object } billing)
        {
            return new GrokQuota
            {
                FetchedAt = now,
                Email = email,
                Error = rpcError ?? "grok agent не вернул данные биллинга."
            };
        }

        var tier = StringProp(billing, "subscription_tier") ?? StringProp(billing, "subscriptionTier");
        var limits = new List<LimitWindow>();

        if (billing.TryGetProperty("config", out var config) && config.ValueKind == JsonValueKind.Object)
        {
            var periodTitle = "7 дней";
            DateTimeOffset? resetsAt = ParseDate(StringProp(config, "billingPeriodEnd"));
            if (config.TryGetProperty("currentPeriod", out var currentPeriod) && currentPeriod.ValueKind == JsonValueKind.Object)
            {
                var periodType = StringProp(currentPeriod, "type");
                periodTitle = periodType switch
                {
                    "USAGE_PERIOD_TYPE_WEEKLY" => "7 дней",
                    "USAGE_PERIOD_TYPE_MONTHLY" => "Месяц",
                    _ => "7 дней"
                };
                resetsAt = ParseDate(StringProp(currentPeriod, "end")) ?? resetsAt;
            }

            double? usedPercent = null;
            if (config.TryGetProperty("creditUsagePercent", out var cup) && cup.ValueKind == JsonValueKind.Number
                && cup.TryGetDouble(out var pct))
            {
                usedPercent = Math.Round(pct, 1);
            }

            var usedDollars = ReadCentDollars(config, "used");
            var limitDollars = ReadCentDollars(config, "monthlyLimit");
            if (limitDollars is > 0 && usedDollars is null)
                usedDollars = 0;
            if (usedPercent is null && usedDollars is double u && limitDollars is double l && l > 0)
                usedPercent = Math.Round(u / l * 100.0, 1);

            // Proto3 JSON omits creditUsagePercent when it equals 0.0.
            var hasPeriod = resetsAt is not null
                || config.TryGetProperty("currentPeriod", out _)
                || !string.IsNullOrWhiteSpace(tier);
            if (usedPercent is null && hasPeriod)
                usedPercent = 0;

            if (usedPercent is not null || limitDollars > 0)
            {
                limits.Add(new LimitWindow
                {
                    Id = "primary",
                    Title = periodTitle,
                    UsedPercent = usedPercent,
                    Used = usedDollars,
                    Limit = limitDollars,
                    ResetsAt = resetsAt
                });
            }

            var onDemandCap = ReadCentDollars(config, "onDemandCap");
            var onDemandUsed = ReadCentDollars(config, "onDemandUsed") ?? 0;
            if (onDemandCap is double cap && cap > 0)
            {
                limits.Add(new LimitWindow
                {
                    Id = "on_demand",
                    Title = $"Доп. лимит (${onDemandUsed:0.##}/${cap:0.##})",
                    Used = onDemandUsed,
                    Limit = cap,
                    UsedPercent = Math.Round(onDemandUsed / cap * 100.0, 1),
                    ResetsAt = resetsAt
                });
            }
        }

        return limits.Count == 0
            ? new GrokQuota
            {
                FetchedAt = now,
                Email = email,
                SubscriptionTier = tier,
                Error = rpcError ?? "Биллинг Grok не вернул активных лимитов."
            }
            : new GrokQuota
            {
                FetchedAt = now,
                Email = email,
                SubscriptionTier = tier,
                Limits = limits
            };
    }

    static double? ReadCentDollars(JsonElement parent, string name)
    {
        if (!parent.TryGetProperty(name, out var prop) || prop.ValueKind != JsonValueKind.Object)
            return null;
        if (prop.TryGetProperty("val", out var val) && val.ValueKind == JsonValueKind.Number
            && val.TryGetDouble(out var cents))
            return Math.Round(cents / 100.0, 2);
        return null;
    }

    /// <summary>Raised when a background agy quota fetch finishes, so the host can republish state.</summary>
    public static event Action? AgyUpdated;

    static readonly object AgyGate = new();
    static AgyQuota? _agyQuota;
    static bool _agyFetching;
    static readonly TimeSpan AgyMinInterval = TimeSpan.FromSeconds(60);
    static readonly TimeSpan AgyStaleAfter = TimeSpan.FromMinutes(30);

    sealed class AgyQuota
    {
        public DateTimeOffset FetchedAt { get; init; }
        public List<LimitWindow> Limits { get; init; } = [];
        public string? Error { get; init; }
    }

    static ServiceStatus Agi(IReadOnlyDictionary<string, string> overrides, DateTimeOffset now)
    {
        var binary = Resolve(overrides, "agy.bin", Path.Combine(LocalApp, "agy", "bin", "agy.exe"));
        var installed = Exists(binary);
        var status = new ServiceStatus
        {
            Id = "agy",
            Name = "agy",
            Installed = installed,
            Path = installed ? binary : null,
            CheckedAt = now
        };
        if (!installed)
        {
            status.Detail = "agy.exe не найден.";
            return status;
        }

        // `agy -p /usage` takes several seconds and hits the network, so it runs in the
        // background; the status shows the last successful snapshot meanwhile.
        AgyQuota? cached;
        bool start = false;
        lock (AgyGate)
        {
            cached = _agyQuota;
            if (!_agyFetching && (cached is null || now - cached.FetchedAt >= AgyMinInterval))
                start = _agyFetching = true;
        }
        if (start) _ = Task.Run(() => FetchAgyQuota(binary));

        status.Authenticated = true;
        status.AuthOk = true;
        if (cached is null)
        {
            status.Detail = "Загрузка лимитов…";
            return status;
        }
        if (cached.Limits.Count == 0)
        {
            status.AuthOk = false;
            status.Detail = cached.Error ?? "Лимиты недоступны.";
            return status;
        }
        status.Limits.AddRange(cached.Limits);
        var age = now - cached.FetchedAt;
        status.LimitsStale = age > AgyStaleAfter;
        status.Detail = status.LimitsStale
            ? $"Кэш лимитов устарел: {(int)age.TotalMinutes}м назад."
            : cached.Error is null
                ? "Лимиты из `agy /usage`"
                : $"Лимиты из `agy /usage` (последнее обновление не удалось: {cached.Error})";
        return status;
    }

    static void FetchAgyQuota(string binary)
    {
        AgyQuota result;
        try
        {
            result = RunAgyUsage(binary);
        }
        catch (Exception ex)
        {
            result = new AgyQuota { FetchedAt = DateTimeOffset.UtcNow, Error = ex.Message };
        }
        lock (AgyGate)
        {
            // Keep the previous good snapshot when a refresh fails.
            if (result.Limits.Count == 0 && _agyQuota is { Limits.Count: > 0 } previous)
                _agyQuota = new AgyQuota { FetchedAt = previous.FetchedAt, Limits = previous.Limits, Error = result.Error };
            else
                _agyQuota = result;
            _agyFetching = false;
        }
        try { AgyUpdated?.Invoke(); } catch { /* listener errors must not break the fetch */ }
    }

    static AgyQuota RunAgyUsage(string binary)
    {
        var info = new System.Diagnostics.ProcessStartInfo(binary)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8
        };
        info.ArgumentList.Add("-p");
        info.ArgumentList.Add("/usage");
        info.ArgumentList.Add("--output-format");
        info.ArgumentList.Add("json");
        using var process = System.Diagnostics.Process.Start(info)
            ?? throw new InvalidOperationException("Не удалось запустить agy.");
        process.StandardInput.Close();
        var stdout = process.StandardOutput.ReadToEndAsync();
        _ = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(TimeSpan.FromSeconds(45)))
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("agy /usage: таймаут.");
        }
        var output = stdout.GetAwaiter().GetResult();
        return ParseAgyUsage(output, DateTimeOffset.UtcNow);
    }

    static AgyQuota ParseAgyUsage(string output, DateTimeOffset now)
    {
        var start = output.IndexOf('{');
        if (start < 0) return new AgyQuota { FetchedAt = now, Error = "agy /usage не вернул JSON." };
        using var doc = JsonDocument.Parse(output[start..]);
        var root = doc.RootElement;
        if (!root.TryGetProperty("command", out var command)
            || !command.TryGetProperty("data", out var data)
            || !data.TryGetProperty("groups", out var groups)
            || groups.ValueKind != JsonValueKind.Array)
        {
            var response = StringProp(root, "response");
            return new AgyQuota
            {
                FetchedAt = now,
                Error = string.IsNullOrWhiteSpace(response) ? "Лимиты agy недоступны (нужен вход в agy?)." : response.Trim()
            };
        }

        var limits = new List<LimitWindow>();
        foreach (var group in groups.EnumerateArray())
        {
            var groupName = ShortGroupName(StringProp(group, "name"));
            if (!group.TryGetProperty("buckets", out var buckets) || buckets.ValueKind != JsonValueKind.Array)
                continue;
            foreach (var bucket in buckets.EnumerateArray())
            {
                var window = StringProp(bucket, "window");
                var windowTitle = window switch
                {
                    "5h" => "5 часов",
                    "weekly" => "7 дней",
                    _ => window ?? StringProp(bucket, "name") ?? "лимит"
                };
                var limit = new LimitWindow
                {
                    Id = StringProp(bucket, "id") ?? $"{groupName}-{window}",
                    Title = string.IsNullOrEmpty(groupName) ? windowTitle : $"{groupName} · {windowTitle}",
                    ResetsAt = ParseDate(StringProp(bucket, "reset_time"))
                };
                if (bucket.TryGetProperty("remaining_fraction", out var remaining)
                    && remaining.ValueKind == JsonValueKind.Number && remaining.TryGetDouble(out var fraction))
                    limit.UsedPercent = Math.Round(Math.Clamp(1 - fraction, 0, 1) * 100, 1);
                limits.Add(limit);
            }
        }
        return limits.Count == 0
            ? new AgyQuota { FetchedAt = now, Error = "agy /usage вернул пустой список лимитов." }
            : new AgyQuota { FetchedAt = now, Limits = limits };
    }

    static string ShortGroupName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        name = name.Trim();
        if (name.EndsWith(" models", StringComparison.OrdinalIgnoreCase))
            name = name[..^" models".Length];
        return name.Replace(" and ", "/", StringComparison.OrdinalIgnoreCase);
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
