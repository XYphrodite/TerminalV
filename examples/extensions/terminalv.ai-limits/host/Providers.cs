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
            if (!status.AuthOk || status.LimitsStale)
                status.Limits.Clear();
            else
                status.Limits.RemoveAll(l => l.ResetsAt is { } r && r <= now);
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
            if (status.LimitsStale)
                status.Limits.Clear();
            else
                status.Limits.RemoveAll(l => l.ResetsAt is { } r && r <= now);
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

        DateTimeOffset? expiresAt = null;
        string? email = null;
        if (json.TryGetProperty("tokens", out var tokens) && tokens.ValueKind == JsonValueKind.Object)
        {
            var accessToken = StringProp(tokens, "access_token");
            var idToken = StringProp(tokens, "id_token");
            expiresAt = ReadJwtExpiry(accessToken) ?? ReadJwtExpiry(idToken);
            email = ReadJwtString(idToken, "email") ?? ReadJwtString(accessToken, "email");
        }

        status.Account = email ?? mode;
        if (expiresAt is not null)
        {
            status.AuthOk = expiresAt > now;
            if (!status.AuthOk)
            {
                status.Detail = $"Токен истёк {expiresAt.Value.ToLocalTime():g} — нужен вход в `codex`.";
                return status;
            }
        }

        status.Detail = mode is null ? "Авторизация сохранена." : $"Режим авторизации: {mode}";
        return status;
    }

    static JsonElement? ParseJwtPayload(string? token)
    {
        if (string.IsNullOrWhiteSpace(token)) return null;
        var first = token.IndexOf('.');
        if (first < 0) return null;
        var second = token.IndexOf('.', first + 1);
        if (second <= first + 1) return null;
        var segment = token[(first + 1)..second].Replace('-', '+').Replace('_', '/');
        segment = (segment.Length % 4) switch
        {
            2 => segment + "==",
            3 => segment + "=",
            _ => segment
        };
        try
        {
            var bytes = Convert.FromBase64String(segment);
            using var doc = JsonDocument.Parse(bytes);
            return doc.RootElement.Clone();
        }
        catch { return null; }
    }

    static DateTimeOffset? ReadJwtExpiry(string? token)
    {
        if (ParseJwtPayload(token) is not { } payload) return null;
        if (payload.TryGetProperty("exp", out var exp) && exp.ValueKind == JsonValueKind.Number
            && exp.TryGetInt64(out var seconds) && seconds > 0)
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        return null;
    }

    static string? ReadJwtString(string? token, string name)
        => ParseJwtPayload(token) is { } payload ? StringProp(payload, name) : null;

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

    static readonly object MimoGate = new();
    static MimoQuota? _mimoQuota;
    static bool _mimoFetching;
    static readonly TimeSpan MimoMinInterval = TimeSpan.FromSeconds(60);
    static readonly TimeSpan MimoStaleAfter = TimeSpan.FromMinutes(30);

    sealed class MimoQuota
    {
        public DateTimeOffset FetchedAt { get; init; }
        public string? PlanName { get; init; }
        public List<LimitWindow> Limits { get; init; } = [];
        public bool CookieExpired { get; init; }
        public string? Error { get; init; }
    }

    static string? NormalizeCookieHeader(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var s = raw.Replace("\r", " ").Replace("\n", " ").Trim();
        if (s.StartsWith("cookie:", StringComparison.OrdinalIgnoreCase))
            s = s["cookie:".Length..].Trim();
        return string.IsNullOrWhiteSpace(s) ? null : s;
    }

    static ServiceStatus Mimo(IReadOnlyDictionary<string, string> overrides, DateTimeOffset now)
    {
        var binary = Resolve(overrides, "mimo.bin", Path.Combine(Home, ".mimocode", "bin", "mimo.exe"));
        var dataDir = Path.Combine(Home, ".local", "share", "mimocode");
        var authFile = Resolve(overrides, "mimo.auth", Path.Combine(dataDir, "auth.json"));
        var credStateFile = Path.Combine(dataDir, "credential-state.json");
        var cookieFile = Resolve(overrides, "mimo.cookieFile", Path.Combine(dataDir, "platform-cookie.txt"));
        var rawCookie = overrides.TryGetValue("mimo.cookie", out var c) && !string.IsNullOrWhiteSpace(c)
            ? c
            : Exists(cookieFile) ? File.ReadAllText(cookieFile) : null;
        var cookie = NormalizeCookieHeader(rawCookie);

        var installed = Exists(binary);
        var status = new ServiceStatus
        {
            Id = "mimo",
            Name = "MiMo",
            Installed = installed,
            Path = installed ? binary : null,
            CheckedAt = now
        };
        if (!installed)
        {
            status.Detail = "mimo.exe не найден.";
            return status;
        }

        // Check CLI credentials in ~/.local/share/mimocode/auth.json & credential-state.json
        var authJson = ReadJson(authFile);
        string? account = null;
        bool hasCliKey = false;
        if (authJson is { ValueKind: JsonValueKind.Object } root)
        {
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Value.ValueKind == JsonValueKind.Object
                    && StringProp(prop.Value, "key") is { Length: > 0 })
                {
                    hasCliKey = true;
                    if (prop.Value.TryGetProperty("metadata", out var meta) && meta.ValueKind == JsonValueKind.Object)
                    {
                        var baseUrl = StringProp(meta, "base_url");
                        var uid = StringProp(meta, "uid") ?? StringProp(meta, "user_id");
                        var tier = baseUrl?.Contains("token-plan", StringComparison.OrdinalIgnoreCase) == true
                            ? "Token Plan"
                            : prop.Name;
                        account = !string.IsNullOrWhiteSpace(uid) ? $"{uid} · {tier}" : tier;
                    }
                    else
                    {
                        account = prop.Name;
                    }
                    break;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(cookie))
        {
            status.Authenticated = true;
            status.AuthOk = true;
            MimoQuota? cached;
            bool start = false;
            lock (MimoGate)
            {
                cached = _mimoQuota;
                if (!_mimoFetching && (cached is null || now - cached.FetchedAt >= MimoMinInterval))
                    start = _mimoFetching = true;
            }
            if (start) _ = Task.Run(() => FetchMimoQuota(cookie));

            status.Account = cached?.PlanName ?? account ?? "Token Plan";
            if (cached is null)
            {
                status.Detail = "Загрузка лимитов Token Plan…";
                return status;
            }
            if (cached.CookieExpired)
            {
                status.AuthOk = hasCliKey;
                status.Detail = "Cookie platform.xiaomimimo.com истёк — обновите platform-cookie.txt.";
                return status;
            }
            if (cached.Limits.Count == 0)
            {
                status.Detail = cached.Error ?? "Лимиты Token Plan недоступны.";
                return status;
            }
            status.Limits.AddRange(cached.Limits.Where(l => l.ResetsAt is null || l.ResetsAt > now));
            var age = now - cached.FetchedAt;
            status.LimitsStale = age > MimoStaleAfter;
            if (status.LimitsStale) status.Limits.Clear();
            status.Detail = status.LimitsStale
                ? $"Кэш лимитов устарел: {(int)age.TotalMinutes}м назад."
                : "Лимиты из platform.xiaomimimo.com";
            return status;
        }

        if (hasCliKey)
        {
            status.Authenticated = true;
            status.AuthOk = true;
            status.Account = account;
            status.Detail = "Ключ активен. Для полоски квот нужен cookie platform.xiaomimimo.com.";
            return status;
        }

        if (ReadJson(credStateFile) is { ValueKind: JsonValueKind.Object } cs
            && cs.TryGetProperty("loggedOut", out var lo) && lo.ValueKind == JsonValueKind.Array
            && lo.GetArrayLength() > 0)
        {
            status.Authenticated = true;
            status.AuthOk = false;
            var last = lo[lo.GetArrayLength() - 1];
            if (last.ValueKind == JsonValueKind.Object && last.TryGetProperty("at", out var atProp)
                && atProp.ValueKind == JsonValueKind.Number && atProp.TryGetInt64(out var ms) && ms > 0)
            {
                var at = DateTimeOffset.FromUnixTimeMilliseconds(ms).ToLocalTime();
                status.Detail = $"Вышли из аккаунта {at:g} — нужен `mimo auth login`.";
            }
            else
            {
                status.Detail = "Сессия завершена — нужен `mimo auth login`.";
            }
            return status;
        }

        status.Authenticated = false;
        status.AuthOk = false;
        status.Detail = "Нет активных учётных данных (`mimo auth login`).";
        return status;
    }

    static void FetchMimoQuota(string cookie)
    {
        MimoQuota result;
        try
        {
            result = RunMimoQuota(cookie).GetAwaiter().GetResult();
        }
        catch (Exception ex)
        {
            result = new MimoQuota { FetchedAt = DateTimeOffset.UtcNow, Error = ex.Message };
        }
        lock (MimoGate)
        {
            _mimoQuota = result;
            _mimoFetching = false;
        }
        try { AgyUpdated?.Invoke(); } catch { }
    }

    static async Task<MimoQuota> RunMimoQuota(string cookie)
    {
        var now = DateTimeOffset.UtcNow;
        using var handler = new System.Net.Http.HttpClientHandler { AllowAutoRedirect = false };
        using var http = new System.Net.Http.HttpClient(handler) { Timeout = TimeSpan.FromSeconds(15) };
        http.DefaultRequestHeaders.TryAddWithoutValidation("Cookie", cookie);
        http.DefaultRequestHeaders.TryAddWithoutValidation("Accept-Language", "en");
        http.DefaultRequestHeaders.TryAddWithoutValidation("User-Agent", "TerminalV-AI-Limits/1.0");

        using var detailMsg = await http.GetAsync("https://platform.xiaomimimo.com/api/v1/tokenPlan/detail");
        if ((int)detailMsg.StatusCode is 401 or 403 or 301 or 302 or 307 or 308)
            return new MimoQuota { FetchedAt = now, CookieExpired = true };
        detailMsg.EnsureSuccessStatusCode();
        var detailResp = await detailMsg.Content.ReadAsStringAsync();

        using var usageMsg = await http.GetAsync("https://platform.xiaomimimo.com/api/v1/tokenPlan/usage");
        if ((int)usageMsg.StatusCode is 401 or 403 or 301 or 302 or 307 or 308)
            return new MimoQuota { FetchedAt = now, CookieExpired = true };
        usageMsg.EnsureSuccessStatusCode();
        var usageResp = await usageMsg.Content.ReadAsStringAsync();

        string? planName = null;
        DateTimeOffset? resetsAt = null;
        using (var detailDoc = JsonDocument.Parse(detailResp))
        {
            if (detailDoc.RootElement.TryGetProperty("code", out var codeProp)
                && codeProp.ValueKind == JsonValueKind.Number && codeProp.TryGetInt32(out var code) && code == 401)
                return new MimoQuota { FetchedAt = now, CookieExpired = true };

            if (detailDoc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object)
            {
                planName = StringProp(d, "planName") ?? StringProp(d, "planCode");
                var periodEnd = StringProp(d, "currentPeriodEnd");
                if (!string.IsNullOrWhiteSpace(periodEnd)
                    && DateTimeOffset.TryParse(periodEnd, System.Globalization.CultureInfo.InvariantCulture,
                        System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                        out var parsedEnd))
                {
                    resetsAt = parsedEnd;
                }
            }
        }

        var limits = new List<LimitWindow>();
        using (var usageDoc = JsonDocument.Parse(usageResp))
        {
            if (usageDoc.RootElement.TryGetProperty("data", out var d) && d.ValueKind == JsonValueKind.Object
                && d.TryGetProperty("usage", out var usage) && usage.ValueKind == JsonValueKind.Object
                && usage.TryGetProperty("items", out var items) && items.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in items.EnumerateArray())
                {
                    var name = StringProp(item, "name") ?? "total_token";
                    double? used = item.TryGetProperty("used", out var u) && u.ValueKind == JsonValueKind.Number && u.TryGetDouble(out var uv) ? uv : null;
                    double? limit = item.TryGetProperty("limit", out var l) && l.ValueKind == JsonValueKind.Number && l.TryGetDouble(out var lv) ? lv : null;
                    if (name == "compensation_total_token" && (limit is null or <= 0)) continue;
                    double? pct = item.TryGetProperty("percent", out var p) && p.ValueKind == JsonValueKind.Number && p.TryGetDouble(out var pv)
                        ? Math.Round(Math.Clamp(pv * 100.0, 0, 100), 1)
                        : (used is double u2 && limit is double l2 && l2 > 0 ? Math.Round(Math.Clamp(u2 / l2 * 100.0, 0, 100), 1) : null);

                    var title = name == "compensation_total_token"
                        ? "Доп. кредиты"
                        : used is double uBil && limit is double lBil && lBil >= 1_000_000_000
                            ? $"Месяц ({uBil / 1_000_000_000.0:0.#}B / {lBil / 1_000_000_000.0:0.#}B)"
                            : "Месяц";
                    limits.Add(new LimitWindow
                    {
                        Id = name,
                        Title = title,
                        Used = used,
                        Limit = limit,
                        UsedPercent = pct,
                        ResetsAt = resetsAt
                    });
                }
            }
        }

        return new MimoQuota
        {
            FetchedAt = now,
            PlanName = planName,
            Limits = limits,
            Error = limits.Count == 0 ? "Нет активного Token Plan." : null
        };
    }

    static readonly string[] WslDistros = ["Ubuntu", "Ubuntu-24.04", "Ubuntu-22.04", "Debian"];

    static ServiceStatus Muse(IReadOnlyDictionary<string, string> overrides, DateTimeOffset now)
    {
        var binary = Resolve(overrides, "muse.bin", Path.Combine(Home, "bin", "muse.bat"));
        var installed = Exists(binary);
        var status = new ServiceStatus
        {
            Id = "muse",
            Name = "Muse",
            Installed = installed,
            Path = installed ? binary : null,
            CheckedAt = now
        };
        if (!installed)
        {
            status.Detail = "muse.bat не найден.";
            return status;
        }

        var linuxUser = Environment.UserName.ToLowerInvariant();
        try
        {
            var batText = File.ReadAllText(binary);
            var idx = batText.IndexOf("/home/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
            {
                var rest = batText[(idx + "/home/".Length)..];
                var slash = rest.IndexOfAny(['/', '\\', ' ', '\r', '\n', '\t']);
                if (slash > 0) linuxUser = rest[..slash];
            }
        }
        catch { }

        string? configDir = null;
        if (overrides.TryGetValue("muse.configDir", out var customCfg) && !string.IsNullOrWhiteSpace(customCfg))
        {
            configDir = customCfg;
        }
        else
        {
            foreach (var distro in WslDistros)
            {
                var candidate = $@"\\wsl.localhost\{distro}\home\{linuxUser}\.config\muse";
                if (Directory.Exists(candidate))
                {
                    configDir = candidate;
                    break;
                }
            }
        }

        if (configDir is null)
        {
            status.Detail = "Конфиг ~/.config/muse в WSL не найден.";
            return status;
        }

        var authFile = Resolve(overrides, "muse.auth", Path.Combine(configDir, "auth.json"));
        var settingsFile = Path.Combine(configDir, "settings.json");

        string? model = null;
        if (ReadJson(settingsFile) is { ValueKind: JsonValueKind.Object } settings)
            model = StringProp(settings, "model");

        var authRoot = ReadJson(authFile);
        if (authRoot is not { ValueKind: JsonValueKind.Object } authObj
            || !authObj.TryGetProperty("providers", out var providers)
            || providers.ValueKind != JsonValueKind.Object)
        {
            status.Account = model;
            status.Detail = "Не авторизован в WSL — нужен `muse login`.";
            return status;
        }

        foreach (var prop in providers.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            var cred = prop.Value;
            var accessToken = StringProp(cred, "access_token");
            var apiKey = StringProp(cred, "api_key");
            if (string.IsNullOrWhiteSpace(accessToken) && string.IsNullOrWhiteSpace(apiKey))
                continue;

            status.Authenticated = true;
            var email = StringProp(cred, "user_email") ?? StringProp(cred, "user_full_name");
            status.Account = !string.IsNullOrWhiteSpace(email) && !string.IsNullOrWhiteSpace(model)
                ? $"{email} · {model}"
                : email ?? model ?? prop.Name;

            DateTimeOffset? expiresAt = null;
            foreach (var expKey in new[] { "expires_at", "expires_at_unix_s", "expires_at_ms" })
            {
                if (!cred.TryGetProperty(expKey, out var expProp)) continue;
                if (expProp.ValueKind == JsonValueKind.Number && expProp.TryGetInt64(out var n) && n > 0)
                {
                    expiresAt = n > 10_000_000_000L
                        ? DateTimeOffset.FromUnixTimeMilliseconds(n)
                        : DateTimeOffset.FromUnixTimeSeconds(n);
                    break;
                }
                if (expProp.ValueKind == JsonValueKind.String)
                {
                    expiresAt = ParseDate(expProp.GetString());
                    if (expiresAt is not null) break;
                }
            }

            if (expiresAt is not null && expiresAt <= now && string.IsNullOrWhiteSpace(apiKey))
            {
                status.AuthOk = false;
                status.Detail = $"Токен в WSL истёк {expiresAt.Value.ToLocalTime():g} — нужен `muse login`.";
                return status;
            }

            status.AuthOk = true;
            status.Detail = "Авторизован в WSL (Meta не отдаёт квоты).";
            return status;
        }

        status.Authenticated = false;
        status.AuthOk = false;
        status.Account = model;
        status.Detail = "Вышли из аккаунта в WSL — нужен `muse login`.";
        return status;
    }
}
