using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.JSInterop;
using TerminalV.Data;
using TerminalV.Ssh;

namespace TerminalV.Mobile.Host;

/// <summary>
/// Mobile analogue of TerminalBridge: speaks the same WebView message protocol as desktop
/// (post {type, id, ...} <-> handleHost) but maps to SshService (SSH.NET / Gateway / tsnet)
/// instead of ConPty. Persists settings/sessions/layouts/profiles via MobileDataStore (Preferences).
/// </summary>
internal sealed class MobileBridge : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly SshService _ssh;
    private readonly MobileDataStore _store;
    private IJSRuntime? _js;
    private readonly ConcurrentDictionary<string, ISshSession> _extra = new();
    private bool _disposed;

    public MobileBridge(SshService ssh, MobileDataStore store)
    {
        _ssh = ssh;
        _store = store;
        MobileJsInterop.Bridge = this;
    }

    public void SetJS(IJSRuntime js)
    {
        _js = js;
        MobileJsInterop.JS = js;
    }

    public void SendInit()
    {
        var settings = _store.LoadSettings();
        // Ensure gateway token exists like desktop
        if (settings.GatewayEnabled && string.IsNullOrEmpty(settings.GatewayToken))
        {
            settings.GatewayToken = GenerateToken();
            _store.SaveSettings(settings);
        }
        Post(new
        {
            type = "init",
            shellName = "SSH",
            buildNumber = 22621,
            version = AppInfo.Version,
            updateSupported = false,
            shortcutsSupported = false,
            settings,
            sessions = _store.LoadSessions(),
            layouts = _store.LoadLayouts(),
            profiles = _store.LoadProfiles(),
            liveIds = LiveIds(),
            cwdTrackingSupported = true,
            launchProfilesSupported = true,
            environmentRefreshSupported = true,
            fonts = Array.Empty<string>(),
            gateway = DescribeGateway(settings)
        });
    }

    public void Handle(string json)
    {
        IncomingMessage? message;
        try { message = JsonSerializer.Deserialize<IncomingMessage>(json, JsonOptions); }
        catch { return; }
        if (message?.Type is null) return;

        switch (message.Type)
        {
            case "ready":
                SendInit();
                break;
            case "create":
                HandleCreate(message);
                break;
            case "attach":
                if (message.Id is not null) TryAttach(message.Id);
                break;
            case "write":
                if (message.Id is not null && message.Data is not null)
                    HandleWrite(message.Id, message.Data);
                break;
            case "resize":
                if (message.Id is not null) HandleResize(message.Id, message.Cols, message.Rows);
                break;
            case "kill":
                if (message.Id is not null) HandleKill(message.Id);
                break;
            case "clipboard-read":
                HandleClipboardRead(message.RequestId);
                break;
            case "paste-file-store":
                HandlePasteFileStore(message.RequestId, message.Data);
                break;
            case "clipboard-write":
                if (!string.IsNullOrEmpty(message.Data))
                    _ = HandleClipboardWrite(message.Data!);
                break;
            case "bell":
                // mobile mute per-session handled in JS; native beep optional
                try { Microsoft.Maui.Storage.Preferences.Default.Get("bell", ""); } catch { }
                break;
            case "open-author":
                _ = OpenUri("https://github.com/XYphrodite");
                break;
            case "open-link":
                if (!string.IsNullOrWhiteSpace(message.Uri)) _ = OpenUri(message.Uri!);
                break;
            case "persist-settings":
                if (message.Data is not null) PersistSettingsRaw(message.Data);
                else if (message.Sessions is not null) { /* ignore */ }
                // For persist-settings desktop sends {type, data: settingsJson?} but our IncomingMessage.Data holds JSON string
                // Fallback: try to deserialize settings from raw json's "settings" field if present in original json
                break;
            case "persist-sessions":
                PersistSessions(message);
                break;
            case "persist-profiles":
                try
                {
                    _store.SaveProfiles(message.Profiles ?? throw new ArgumentException("Нет списка профилей."));
                    Post(new { type = "profiles-saved", requestId = message.RequestId, profiles = _store.LoadProfiles() });
                }
                catch (Exception ex) { Post(new { type = "profiles-saved", requestId = message.RequestId, error = ex.Message }); }
                break;
            case "list-launch-targets":
                _ = SendLaunchTargetsAsync(message.RequestId);
                break;
            case "create-shortcuts":
                Post(new { type = "shortcuts-created", requestId = message.RequestId, error = "Ярлыки не поддерживаются на мобильном." });
                break;
            case "pick-background":
                _ = PickBackgroundAsync();
                break;
            case "update-check":
            case "update-apply":
                Post(new { type = "update", status = "unsupported", message = "Обновление на мобильном через магазин." });
                break;
            case "diag":
                // log
                System.Diagnostics.Debug.WriteLine($"[mobile diag] {message.Data}");
                break;
        }

        // Persist-settings variant where desktop sends settings object in message.Data as JSON string
        if (message.Type == "persist-settings" && message.Data is not null)
        {
            // Already handled via PersistSettingsRaw if needed
        }
    }

    private void HandleCreate(IncomingMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Id)) return;
        try
        {
            var opts = BuildOptions(message.Cols, message.Rows, message.Cwd, message.Shell, message.StartupCommand);
            var session = _ssh.Create(message.Id!, opts);
            HookSession(session);
            _ = session.ConnectAsync();
        }
        catch (Exception ex)
        {
            Post(new { type = "error", id = message.Id, message = ex.Message });
        }
    }

    private void TryAttach(string id)
    {
        var s = _ssh.TryGet(id) ?? (_extra.TryGetValue(id, out var e) ? e : null);
        if (s != null)
        {
            // Re-ensure hooks
            HookSession(s);
        }
    }

    private void HandleWrite(string id, string data)
    {
        var s = _ssh.TryGet(id) ?? (_extra.TryGetValue(id, out var e) ? e : null);
        if (s != null)
        {
            try { s.Write(data); } catch (Exception ex) { Post(new { type = "error", id, message = ex.Message }); }
        }
        else
        {
            // session not found – desktop would buffer? For mobile just error
            Post(new { type = "error", id, message = "Сессия не найдена." });
        }
    }

    private void HandleResize(string id, int cols, int rows)
    {
        var s = _ssh.TryGet(id) ?? (_extra.TryGetValue(id, out var e) ? e : null);
        try { s?.Resize(cols, rows); } catch { }
    }

    private void HandleKill(string id)
    {
        var s = _ssh.TryGet(id);
        if (s != null)
        {
            _ = s.DisconnectAsync();
            s.Dispose();
        }
        _extra.TryRemove(id, out _);
    }

    private async void HandleClipboardRead(string? requestId)
    {
        if (string.IsNullOrEmpty(requestId)) return;
        try
        {
            var text = await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.GetTextAsync();
            Post(new { type = "clipboard-data", requestId, data = text ?? "" });
        }
        catch { Post(new { type = "clipboard-data", requestId, data = "" }); }
    }

    private void HandlePasteFileStore(string? requestId, string? data)
    {
        if (string.IsNullOrEmpty(requestId)) return;
        try
        {
            if (string.IsNullOrEmpty(data)) { Post(new { type = "paste-file-stored", requestId, path = (string?)null }); return; }
            var dir = Path.Combine(Path.GetTempPath(), "TerminalV", "pastes");
            Directory.CreateDirectory(dir);
            var path = Path.Combine(dir, $"paste-{Guid.NewGuid():N}.txt");
            File.WriteAllText(path, data, new System.Text.UTF8Encoding(false));
            Post(new { type = "paste-file-stored", requestId, path });
        }
        catch { Post(new { type = "paste-file-stored", requestId, path = (string?)null }); }
    }

    private async Task HandleClipboardWrite(string data)
    {
        try { await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.SetTextAsync(data); } catch { }
    }

    private async Task OpenUri(string uri)
    {
        try
        {
            uri = uri.Trim();
            if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed) || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps)) return;
            await Microsoft.Maui.ApplicationModel.Browser.Default.OpenAsync(parsed.AbsoluteUri, Microsoft.Maui.ApplicationModel.BrowserLaunchMode.SystemPreferred);
        }
        catch { }
    }

    private void PersistSettingsRaw(string json)
    {
        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is null) return;
            // Preserve token like desktop: empty token never wipes stored one
            if (string.IsNullOrEmpty(settings.GatewayToken))
            {
                try
                {
                    var stored = _store.LoadSettings();
                    if (!string.IsNullOrEmpty(stored.GatewayToken))
                        settings.GatewayToken = stored.GatewayToken;
                }
                catch { }
            }
            if (settings.GatewayPort is < 1 or > 65535) settings.GatewayPort = 5454;
            _store.SaveSettings(settings);
        }
        catch { }
    }

    private void PersistSessions(IncomingMessage message)
    {
        try
        {
            if (message.Sessions is not null) _store.SaveSessions(message.Sessions);
            if (message.Layouts is not null) _store.SaveLayouts(message.Layouts);
        }
        catch { }
    }

    private async Task SendLaunchTargetsAsync(string? requestId)
    {
        // On mobile there are no local shells; return empty list with maybe SSH option
        var targets = new object[]
        {
            new { id = "ssh", name = "SSH (настройки подключения)", kind = "ssh" }
        };
        Post(new { type = "launch-targets", requestId, targets });
        await Task.CompletedTask;
    }

    private async Task PickBackgroundAsync()
    {
        try
        {
            var result = await Microsoft.Maui.Storage.FilePicker.Default.PickAsync(new Microsoft.Maui.Storage.PickOptions { PickerTitle = "Выберите фон" });
            if (result == null) return;
            var destDir = Path.Combine(FileSystem.AppDataDirectory, "backgrounds");
            Directory.CreateDirectory(destDir);
            var dest = Path.Combine(destDir, result.FileName);
            using var src = await result.OpenReadAsync();
            using var dst = File.Create(dest);
            await src.CopyToAsync(dst);
            var virtualPath = $"https://tvdata.local/backgrounds/{result.FileName}";
            // For mobile, serve via file path; tell UI the path
            Post(new { type = "background-picked", path = dest });
        }
        catch { }
    }

    private SshConnectionOptions BuildOptions(int cols, int rows, string? cwd, string? shell, string? startupCommand)
    {
        // Read connection prefs (same keys as Home.razor Preferences)
        var host = Microsoft.Maui.Storage.Preferences.Default.Get("host", "100.119.48.15");
        var port = Microsoft.Maui.Storage.Preferences.Default.Get("port", 22);
        var username = Microsoft.Maui.Storage.Preferences.Default.Get("username", "local");
        var password = Microsoft.Maui.Storage.Preferences.Default.Get("password", "5454");
        var useGateway = Microsoft.Maui.Storage.Preferences.Default.Get("useGateway", false);
        var gatewayUrl = Microsoft.Maui.Storage.Preferences.Default.Get("gatewayUrl", "ws://100.119.48.15:5454");
        var gatewayToken = Microsoft.Maui.Storage.Preferences.Default.Get("gatewayToken", "");
        var useTailscale = Microsoft.Maui.Storage.Preferences.Default.Get("useTailscale", false);
        var tailscaleAuthKey = Microsoft.Maui.Storage.Preferences.Default.Get("tailscaleAuthKey", "");
        var tailscaleHostname = Microsoft.Maui.Storage.Preferences.Default.Get("tailscaleHostname", "terminalv-mobile");

        var opts = new SshConnectionOptions
        {
            Host = host.Trim(),
            Port = port,
            Username = username.Trim(),
            Password = password,
            Columns = Math.Clamp(cols <= 0 ? 80 : cols, 1, 1000),
            Rows = Math.Clamp(rows <= 0 ? 24 : rows, 1, 1000)
        };
        if (useGateway)
        {
            opts.GatewayUrl = string.IsNullOrWhiteSpace(gatewayUrl) ? null : gatewayUrl.Trim();
            opts.GatewayToken = string.IsNullOrWhiteSpace(gatewayToken) ? null : gatewayToken.Trim();
        }
        if (useTailscale && !useGateway)
        {
            opts.Tailscale.Enabled = true;
            opts.Tailscale.AuthKey = tailscaleAuthKey.Trim();
            opts.Tailscale.Hostname = string.IsNullOrWhiteSpace(tailscaleHostname) ? "terminalv-mobile" : tailscaleHostname.Trim();
        }
        // cwd/shell/startupCommand are snapshots for session record but not used for SSH transport
        // Keep for persistence parity
        opts.Validate();
        return opts;
    }

    private void HookSession(ISshSession session)
    {
        // Avoid double hook
        session.DataReceived -= OnData;
        session.ErrorReceived -= OnError;
        session.Closed -= OnClosed;
        session.DataReceived += OnData;
        session.ErrorReceived += OnError;
        session.Closed += OnClosed;
    }

    private void OnData(string id, string data, bool replay)
    {
        Post(new { type = "data", id, data, replay });
    }
    private void OnError(string id, string message) => Post(new { type = "error", id, message });
    private void OnClosed(string id, int? code) => Post(new { type = "exit", id, code });

    private string[] LiveIds()
    {
        try { return _ssh.List().Select(s => s.Id).ToArray(); } catch { return []; }
    }

    private object DescribeGateway(AppSettings settings)
    {
        return new { enabled = settings.GatewayEnabled, port = settings.GatewayPort, listening = false, status = settings.GatewayEnabled ? "Настройте через SSH подключение." : "Отключён." };
    }

    private void Post(object obj)
    {
        if (_disposed) return;
        var js = _js;
        if (js is null) return;
        try
        {
            var json = JsonSerializer.Serialize(obj, JsonOptions);
            MainThread.BeginInvokeOnMainThread(async () =>
            {
                try { await js.InvokeVoidAsync("__tvDispatch", json); } catch { }
            });
        }
        catch { }
    }

    private static string GenerateToken()
    {
        var bytes = RandomNumberGenerator.GetBytes(24);
        return Convert.ToBase64String(bytes).Replace('+', '-').Replace('/', '_').TrimEnd('=');
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        foreach (var kv in _extra) try { kv.Value.Dispose(); } catch { }
        _extra.Clear();
    }
}

internal static class MobileJsInterop
{
    public static MobileBridge? Bridge;
    public static IJSRuntime? JS;

    [JSInvokable]
    public static Task HandleMessage(string json)
    {
        Bridge?.Handle(json);
        return Task.CompletedTask;
    }
}

internal static class AppInfo
{
    public static string Version => "0.7.5";
}
