using System.Text.Json;
using TerminalV.Extensibility;

namespace AiLimits;

public sealed class AiLimitsExtension : ITerminalVExtension
{
    private IExtensionContext _context = null!;
    private CancellationTokenSource? _stop;
    private Task _loop = Task.CompletedTask;
    private readonly object _gate = new();
    private Dictionary<string, string> _overrides = new();
    private ServiceStatus[] _snapshot = [];
    private int _intervalSeconds = 300;

    public Task ActivateAsync(IExtensionContext context, CancellationToken lifetime)
    {
        _context = context;
        _stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime);
        _overrides = LoadOverrides(context);
        if (int.TryParse(context.Storage.Get("intervalSeconds"), out var interval) && interval >= 30)
            _intervalSeconds = interval;
        _snapshot = Providers.Collect(_overrides);
        Providers.AgyUpdated += OnAgyUpdated;
        _loop = RunLoopAsync(_stop.Token);
        context.Log($"AI Limits activated with {_snapshot.Length} providers.");
        return Task.CompletedTask;
    }

    public Task<JsonElement?> InvokeAsync(string method, JsonElement? args, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        switch (method)
        {
            case "getState":
                return Task.FromResult<JsonElement?>(SerializeState());
            case "refresh":
                lock (_gate)
                {
                    _overrides = LoadOverrides(_context);
                    _snapshot = Providers.Collect(_overrides);
                }
                _context.Publish("changed", StateObject());
                return Task.FromResult<JsonElement?>(SerializeState());
            case "setInterval":
            {
                var seconds = _intervalSeconds;
                if (args is { } argsValue && argsValue.TryGetProperty("seconds", out var value) && value.TryGetInt32(out var s))
                    seconds = s;
                if (seconds < 30) seconds = 30;
                if (seconds > 3600) seconds = 3600;
                _intervalSeconds = seconds;
                _context.Storage.Set("intervalSeconds", seconds.ToString());
                return Task.FromResult<JsonElement?>(SerializeState());
            }
            case "setOverride":
            {
                string? key = null;
                string? path = null;
                if (args is { } argsValue)
                {
                    if (argsValue.TryGetProperty("key", out var k) && k.ValueKind == JsonValueKind.String)
                        key = k.GetString();
                    if (argsValue.TryGetProperty("path", out var p) && p.ValueKind == JsonValueKind.String)
                        path = p.GetString();
                }
                if (string.IsNullOrWhiteSpace(key)) throw new ArgumentException("key is required.");
                if (string.IsNullOrWhiteSpace(path)) _context.Storage.Set($"path.{key}", null);
                else _context.Storage.Set($"path.{key}", path);
                lock (_gate)
                {
                    _overrides = LoadOverrides(_context);
                    _snapshot = Providers.Collect(_overrides);
                }
                _context.Publish("changed", StateObject());
                return Task.FromResult<JsonElement?>(SerializeState());
            }
            default:
                throw new ArgumentException($"Unknown method: {method}");
        }
    }

    private void OnAgyUpdated()
    {
        if (_stop is null || _stop.IsCancellationRequested) return;
        ServiceStatus[] next;
        lock (_gate) next = Providers.Collect(_overrides);
        lock (_gate) _snapshot = next;
        _context.Publish("changed", StateObject());
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(_intervalSeconds), cancellationToken).ConfigureAwait(false);
                ServiceStatus[] next;
                lock (_gate) next = Providers.Collect(_overrides);
                lock (_gate) _snapshot = next;
                _context.Publish("changed", StateObject());
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
        catch (Exception ex) { _context.Log(ex.Message); }
    }

    private static Dictionary<string, string> LoadOverrides(IExtensionContext context)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var key in new[]
        {
            "claude.bin", "claude.config", "codex.bin", "codex.auth",
            "grok.bin", "grok.auth", "grok.version", "agy.bin",
            "mimo.bin", "kimi.bin", "kimi.status", "muse.bin"
        })
        {
            var value = context.Storage.Get($"path.{key}");
            if (!string.IsNullOrWhiteSpace(value)) map[key] = value;
        }
        return map;
    }

    private object StateObject()
    {
        ServiceStatus[] snapshot;
        lock (_gate) snapshot = _snapshot;
        return new
        {
            intervalSeconds = _intervalSeconds,
            checkedAt = snapshot.Length > 0 ? snapshot[0].CheckedAt : DateTimeOffset.UtcNow,
            services = snapshot.Select(Map).ToArray()
        };
    }

    private static object Map(ServiceStatus status) => new
    {
        id = status.Id,
        name = status.Name,
        installed = status.Installed,
        authenticated = status.Authenticated,
        authOk = status.AuthOk,
        limitsStale = status.LimitsStale,
        path = status.Path,
        account = status.Account,
        detail = status.Detail,
        checkedAt = status.CheckedAt,
        limits = status.Limits.Select(l => new
        {
            id = l.Id,
            title = l.Title,
            usedPercent = l.UsedPercent,
            used = l.Used,
            limit = l.Limit,
            resetsAt = l.ResetsAt
        }).ToArray()
    };

    private JsonElement SerializeState()
        => JsonSerializer.SerializeToElement(StateObject(), Json.Options);

    public async ValueTask DisposeAsync()
    {
        if (_stop is null) return;
        Providers.AgyUpdated -= OnAgyUpdated;
        await _stop.CancelAsync();
        try { await _loop; } catch { /* loop logs its own errors */ }
        _stop.Dispose();
        _stop = null;
    }
}
