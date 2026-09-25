using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.JSInterop;
using TerminalV.Data;
using TerminalV.Host;
using TerminalV.Ssh;

namespace TerminalV.Mobile.Host;

/// <summary>
/// Mobile analogue of TerminalBridge: speaks same WebView message protocol as desktop
/// (post type id etc to handleHost) but maps to SshService instead of ConPty.
/// Persists settings/sessions/layouts/profiles via MobileDataStore (Preferences).
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
    private readonly ITailscaleConnector? _account;
    private IJSRuntime? _js;
    private readonly ConcurrentDictionary<string, Task> _connecting = new();
    private readonly ConcurrentDictionary<string, SessionObservation> _observedSessions = new();
    private readonly object _connectionLock = new();
    private readonly SemaphoreSlim _initGate = new(1, 1);
    private HashSet<string> _remoteIds = new();
    private string _connectionFallback = "idle";
    private bool _connectionEnabled = true;
    private int _connectionEpoch;
    private int _checkingConnection;
    private string? _catalogJson;
    private bool _disposed;

    public MobileBridge(SshService ssh, MobileDataStore store, ITailscaleConnector? account = null)
    {
        _ssh = ssh;
        _store = store;
        _account = account;
    }

    public void SetJS(IJSRuntime js)
    {
        _js = js;
    }

    public void SendInit() => _ = SendInitAsync();

    public async Task SendInitAsync(bool reconnect = false, bool connect = true)
    {
        await _initGate.WaitAsync();
        try
        {
            if (_disposed) return;
            // App metadata must not wait for a remote gateway or Tailscale login.
            Post(new { type = "app-info", mobile = true, version = AppInfo.Version, updateSupported = false });
            lock (_connectionLock)
            {
                _connectionEpoch++;
                _connectionEnabled = connect;
                // Invalidate callbacks before disposing the old transports. Their
                // read loops may still report a close while replacements connect.
                if (reconnect || !connect) _observedSessions.Clear();
            }
            if (reconnect || !connect)
            {
                foreach (var session in _ssh.List()) _ssh.Remove(session.Id);
                _connecting.Clear();
            }
            var configured = HasConnectionSettings();
            SetConnectionFallback(!configured ? "unconfigured" : connect && UsesGateway() ? "connecting" : "idle");
            var settings = _store.LoadSettings();
            var sessions = _store.LoadSessions();
            // Keep gateway snapshots offline, including empty saved tabs. Legacy
            // gateways retain their existing live-id/cache reconciliation.
            var filterUnbufferedSessions = !UsesGateway();
            var sessionsAuthoritative = false;
            _catalogJson = null;
            string? connectionError = null;
            _remoteIds = new HashSet<string>();
            try
            {
                if (configured && connect && UsesGateway())
                {
                    var options = BuildOptions(80, 24, null, null, null);
                    if (options.GatewayTailnetIdentity)
                    {
                        await (_account ?? throw new InvalidOperationException("Вход через Tailscale недоступен.")).StartAsync(new TailscaleOptions());
                        options = BuildOptions(80, 24, null, null, null);
                    }
                    using var client = new GatewayControlClient(options.GatewayUrl!, options.GatewayToken,
                        tailnetIdentity: options.GatewayTailnetIdentity, dial: options.GatewayDial);
                    var catalog = await client.ListCatalogAsync();
                    if (catalog.Sessions is { } desktopSessions)
                    {
                        sessions = ApplyDesktopCatalog(desktopSessions, sessions);
                        sessionsAuthoritative = true;
                    }
                    else
                    {
                        filterUnbufferedSessions = true;
                        _remoteIds = catalog.Ids.ToHashSet();
                        foreach (var id in _remoteIds)
                        {
                            if (sessions.Any(s => s.Id == id)) continue;
                            sessions.Add(new SessionRecord { Id = id, Title = $"Сессия {id[..Math.Min(4, id.Length)]}", Shell = "ssh" });
                        }
                    }
                    SetConnectionFallback("connected");
                }
            }
            catch (Exception ex)
            {
                SetConnectionFallback("disconnected");
                connectionError = $"Ошибка подключения: {ex.Message}. Откройте подключение в списке сессий.";
            }
            var live = LiveIds().Concat(_remoteIds).Distinct().ToArray();
            var liveSet = new HashSet<string>(live);
            if (filterUnbufferedSessions)
                sessions = sessions.Where(s => liveSet.Contains(s.Id) || !string.IsNullOrEmpty(s.Buffer)).ToList();
            var layouts = PaneLayout.Normalize(_store.LoadLayouts(), sessions);
            _store.SaveSessions(sessions);
            _store.SaveLayouts(layouts);
            // The UI must create each terminal BEFORE attach can replay its output.
            Post(new
            {
                type = "init", mobile = true, reconnect, connectionError, sessionsAuthoritative,
                shellName = "SSH", buildNumber = 22621, version = AppInfo.Version,
                updateSupported = false, shortcutsSupported = false, settings, sessions, layouts,
                profiles = _store.LoadProfiles(), liveIds = live,
                cwdTrackingSupported = true, launchProfilesSupported = true,
                environmentRefreshSupported = true, fonts = Array.Empty<string>(),
                gateway = DescribeGateway(settings)
            });
        }
        finally { _initGate.Release(); }
    }

    private List<SessionRecord> ApplyDesktopCatalog(IReadOnlyList<GatewaySessionInfo> desktopSessions, List<SessionRecord> cachedSessions)
    {
        var ids = desktopSessions.Select(s => s.Id).ToHashSet();
        _store.ArchiveSessions(cachedSessions.Where(s => !ids.Contains(s.Id)));
        _remoteIds = desktopSessions.Where(s => s.Live).Select(s => s.Id).ToHashSet();
        var sessions = ReconcileCatalog(desktopSessions, cachedSessions);
        RetireMissingTransports(_remoteIds);
        _catalogJson = JsonSerializer.Serialize(desktopSessions, JsonOptions);
        return sessions;
    }

    private static List<SessionRecord> ReconcileCatalog(IReadOnlyList<GatewaySessionInfo> desktopSessions,
        List<SessionRecord> cachedSessions)
    {
        var cached = cachedSessions.ToLookup(s => s.Id);
        return desktopSessions.OrderBy(s => s.SortOrder).Select(desktop =>
        {
            // Buffers and working directories remain local snapshots for the same
            // desktop tab; membership and display metadata come from the desktop.
            var session = cached[desktop.Id].FirstOrDefault() ?? new SessionRecord { Id = desktop.Id, Shell = "ssh" };
            session.Title = desktop.Title;
            session.CustomTitle = desktop.CustomTitle;
            session.Hidden = desktop.Hidden;
            session.SortOrder = desktop.SortOrder;
            session.Active = desktop.Active;
            return session;
        }).ToList();
    }

    private void RetireMissingTransports(HashSet<string> liveIds)
    {
        // Invalidate callbacks before closing only the phone's WebSockets.
        // Removing a catalog entry must never kill the desktop shell.
        lock (_connectionLock)
            foreach (var id in _observedSessions.Keys.Where(id => !liveIds.Contains(id)))
                _observedSessions.TryRemove(id, out _);
        foreach (var id in _connecting.Keys.Where(id => !liveIds.Contains(id)))
            _connecting.TryRemove(id, out _);
        foreach (var session in _ssh.List().Where(s => !liveIds.Contains(s.Id)))
            _ssh.Remove(session.Id);
    }

    public async Task Handle(string json)
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
            case "connection-check":
                // A sidebar refresh must not hold the ordered terminal-input queue.
                _ = CheckConnectionAsync();
                break;
            case "create":
                HandleCreate(message);
                break;
            case "attach":
                if (message.Id is not null) TryAttach(message.Id, message.Cols, message.Rows);
                break;
            case "write":
                if (message.Id is not null && message.Data is not null)
                    await HandleWrite(message.Id, message.Data);
                break;
            case "resize":
                if (message.Id is not null) await HandleResize(message.Id, message.Cols, message.Rows);
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
            case "archive-sessions":
                if (message.Sessions is not null) _store.ArchiveSessions(message.Sessions);
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
            case "export":
                _ = HandleExportAsync(message.RequestId);
                break;
            case "import":
                _ = HandleImportAsync(message.RequestId);
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
        StartSession(message.Id, message.Cols, message.Rows, mirror: _remoteIds.Contains(message.Id));
    }

    private void StartSession(string id, int cols, int rows, bool mirror)
    {
        try
        {
            if (!HasConnectionSettings())
                throw new InvalidOperationException("Выберите компьютер в настройках подключения.");
            var options = BuildOptions(cols, rows, null, null, null);
            if (mirror) options.MirrorSessionId = id;
            lock (_connectionLock) _observedSessions.TryRemove(id, out _);
            var session = _ssh.Create(id, options);
            var observed = HookSession(session);
            _connecting[id] = ConnectSessionAsync(observed);
        }
        catch (Exception ex)
        {
            SetConnectionFallback("disconnected");
            ReportConnectionError(id, ex.Message);
        }
    }

    private async Task ConnectSessionAsync(SessionObservation observed)
    {
        var session = observed.Session;
        try
        {
            await session.ConnectAsync();
            lock (_connectionLock)
            {
                if (!IsCurrent(observed) || observed.State != "connecting") return;
                observed.State = session.State == SshSessionState.Connected ? "connected" : "idle";
                PublishConnectionState();
            }
        }
        catch (Exception ex)
        {
            lock (_connectionLock)
            {
                if (!IsCurrent(observed)) return;
                observed.State = "disconnected";
                _connectionFallback = "disconnected";
                PublishConnectionState();
                ReportConnectionError(session.Id, ex.Message);
            }
        }
    }

    private void ReportConnectionError(string id, string message)
    {
        Post(new { type = "error", id, message });
        Post(new { type = "data", id, data = $"\r\n\x1b[31mОшибка подключения: {message}\x1b[0m\r\nОткройте подключение в списке сессий.\r\n" });
    }

    private void TryAttach(string id, int cols, int rows)
    {
        var session = _ssh.TryGet(id);
        if (session is null || !HasHealthyObservation(id))
            StartSession(id, cols, rows, mirror: _remoteIds.Contains(id));
        else HookSession(session);
    }

    private bool HasHealthyObservation(string id)
    {
        var session = _ssh.TryGet(id);
        lock (_connectionLock)
            return _observedSessions.TryGetValue(id, out var observed) &&
                ReferenceEquals(session, observed.Session) &&
                observed.State is "connected" or "connecting" &&
                observed.Session.State is SshSessionState.Connected or SshSessionState.Connecting;
    }

    private async Task HandleWrite(string id, string data)
    {
        ISshSession? session = null;
        try
        {
            if (_connecting.TryGetValue(id, out var connecting)) await connecting;
            session = _ssh.TryGet(id) ?? throw new InvalidOperationException("Сессия не найдена.");
            await session.WriteAsync(data);
        }
        catch (Exception ex)
        {
            if (session is not null && !RecordSessionFailure(session)) return;
            Post(new { type = "error", id, message = ex.Message });
        }
    }

    private async Task HandleResize(string id, int cols, int rows)
    {
        try
        {
            if (_connecting.TryGetValue(id, out var connecting)) await connecting;
            if (_ssh.TryGet(id) is { } session) await session.ResizeAsync(cols, rows);
        }
        catch (Exception ex) { Post(new { type = "error", id, message = ex.Message }); }
    }

    private void HandleKill(string id)
    {
        _connecting.TryRemove(id, out _);
        lock (_connectionLock) _observedSessions.TryRemove(id, out _);
        _ssh.Remove(id);
        SetConnectionFallback("idle");
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

    private async Task HandleExportAsync(string? requestId)
    {
        try
        {
            var dto = new ExportDto
            {
                Version = 1,
                ExportedAt = DateTime.UtcNow,
                AppSettings = _store.LoadSettings(),
                Sessions = _store.LoadSessions(),
                ArchivedSessions = _store.LoadArchivedSessions(),
                Layouts = _store.LoadLayouts(),
                Profiles = _store.LoadProfiles(),
                Connection = new ExportConnection
                {
                    Host = Microsoft.Maui.Storage.Preferences.Default.Get("host", "100.119.48.15"),
                    Port = Microsoft.Maui.Storage.Preferences.Default.Get("port", 22),
                    Username = Microsoft.Maui.Storage.Preferences.Default.Get("username", "local"),
                    Password = Microsoft.Maui.Storage.Preferences.Default.Get("password", "5454"),
                    UseGateway = Microsoft.Maui.Storage.Preferences.Default.Get("useGateway", false),
                    GatewayUrl = Microsoft.Maui.Storage.Preferences.Default.Get("gatewayUrl", "ws://100.119.48.15:5454"),
                    GatewayToken = Microsoft.Maui.Storage.Preferences.Default.Get("gatewayToken", ""),
                    UseTailscale = Microsoft.Maui.Storage.Preferences.Default.Get("useTailscale", false),
                    TailscaleAuthKey = Microsoft.Maui.Storage.Preferences.Default.Get("tailscaleAuthKey", ""),
                    TailscaleHostname = Microsoft.Maui.Storage.Preferences.Default.Get("tailscaleHostname", "terminalv-mobile")
                }
            };
            var json = JsonSerializer.Serialize(dto, new JsonSerializerOptions { WriteIndented = true, PropertyNamingPolicy = JsonNamingPolicy.CamelCase });
            var fileName = $"terminalv-export-{DateTime.Now:yyyyMMdd-HHmmss}.json";
            var filePath = Path.Combine(FileSystem.CacheDirectory, fileName);
            await File.WriteAllTextAsync(filePath, json);
            // Share via system share sheet (Save to Downloads / Drive / Telegram)
            try
            {
                await Microsoft.Maui.ApplicationModel.DataTransfer.Share.Default.RequestAsync(new Microsoft.Maui.ApplicationModel.DataTransfer.ShareFileRequest
                {
                    Title = "Экспорт TerminalV",
                    File = new Microsoft.Maui.ApplicationModel.DataTransfer.ShareFile(filePath)
                });
            }
            catch { }
            Post(new { type = "exported", requestId, path = filePath, message = "Экспорт готов — выбери куда сохранить." });
        }
        catch (Exception ex)
        {
            Post(new { type = "exported", requestId, error = ex.Message });
        }
    }

    private async Task HandleImportAsync(string? requestId)
    {
        try
        {
            var result = await Microsoft.Maui.Storage.FilePicker.Default.PickAsync(new Microsoft.Maui.Storage.PickOptions
            {
                PickerTitle = "Выбери JSON для импорта",
                FileTypes = new FilePickerFileType(new Dictionary<DevicePlatform, IEnumerable<string>> { [DevicePlatform.Android] = new[] { "application/json", "text/json" }, [DevicePlatform.iOS] = new[] { "public.json" }, [DevicePlatform.WinUI] = new[] { ".json" } })
            });
            if (result == null)
            {
                Post(new { type = "imported", requestId, error = "Отменено." });
                return;
            }
            string json;
            using (var stream = await result.OpenReadAsync())
            using (var reader = new StreamReader(stream))
                json = await reader.ReadToEndAsync();

            var dto = JsonSerializer.Deserialize<ExportDto>(json, JsonOptions);
            if (dto == null) throw new InvalidDataException("Пустой файл.");
            if (dto.Version != 1) throw new InvalidDataException($"Неподдерживаемая версия {dto.Version}.");

            // Replace all (recommended)
            if (dto.AppSettings != null) _store.SaveSettings(dto.AppSettings);
            if (dto.Sessions != null) _store.SaveSessions(dto.Sessions);
            if (dto.ArchivedSessions != null) _store.ArchiveSessions(dto.ArchivedSessions);
            if (dto.Layouts != null) _store.SaveLayouts(dto.Layouts);
            if (dto.Profiles != null) _store.SaveProfiles(dto.Profiles);
            if (dto.Connection != null)
            {
                var c = dto.Connection;
                Microsoft.Maui.Storage.Preferences.Default.Set("host", c.Host ?? "100.119.48.15");
                Microsoft.Maui.Storage.Preferences.Default.Set("port", c.Port);
                Microsoft.Maui.Storage.Preferences.Default.Set("username", c.Username ?? "local");
                Microsoft.Maui.Storage.Preferences.Default.Set("password", c.Password ?? "");
                Microsoft.Maui.Storage.Preferences.Default.Set("useGateway", c.UseGateway);
                Microsoft.Maui.Storage.Preferences.Default.Set("gatewayUrl", c.GatewayUrl ?? "ws://100.119.48.15:5454");
                Microsoft.Maui.Storage.Preferences.Default.Set("gatewayToken", c.GatewayToken ?? "");
                Microsoft.Maui.Storage.Preferences.Default.Set("useTailscale", c.UseTailscale);
                Microsoft.Maui.Storage.Preferences.Default.Set("tailscaleAuthKey", c.TailscaleAuthKey ?? "");
                Microsoft.Maui.Storage.Preferences.Default.Set("tailscaleHostname", c.TailscaleHostname ?? "terminalv-mobile");
            }

            Post(new { type = "imported", requestId, message = "Импорт выполнен — перезагрузка." });
            // Push new init so UI refreshes without restart
            SendInit();
        }
        catch (Exception ex)
        {
            Post(new { type = "imported", requestId, error = ex.Message });
        }
    }

    private SshConnectionOptions BuildOptions(int cols, int rows, string? cwd, string? shell, string? startupCommand)
    {
        var appSettings = _store.LoadSettings();
        var host = Microsoft.Maui.Storage.Preferences.Default.Get("host", "100.119.48.15");
        var port = Microsoft.Maui.Storage.Preferences.Default.Get("port", 22);
        var username = Microsoft.Maui.Storage.Preferences.Default.Get("username", "local");
        var password = Microsoft.Maui.Storage.Preferences.Default.Get("password", "5454");
        var useGatewayPref = Microsoft.Maui.Storage.Preferences.Default.Get("useGateway", false);
        var useGateway = useGatewayPref;
        var gatewayUrlPref = Microsoft.Maui.Storage.Preferences.Default.Get("gatewayUrl", "");
        var gatewayUrl = !string.IsNullOrWhiteSpace(gatewayUrlPref) ? gatewayUrlPref : (appSettings.GatewayEnabled ? $"ws://{host}:{appSettings.GatewayPort}" : "ws://100.119.48.15:5454");
        var gatewayTokenPref = Microsoft.Maui.Storage.Preferences.Default.Get("gatewayToken", "");
        var gatewayToken = gatewayTokenPref;
        var useTailscale = Microsoft.Maui.Storage.Preferences.Default.Get("useTailscale", false);
        var tailscaleAuthKey = Microsoft.Maui.Storage.Preferences.Default.Get("tailscaleAuthKey", "");
        var tailscaleHostname = Microsoft.Maui.Storage.Preferences.Default.Get("tailscaleHostname", "terminalv-mobile");

        var opts = new SshConnectionOptions
        {
            Host = host.Trim(),
            Port = port,
            Username = username.Trim(),
            Password = password,
            // A failed connection must be visible and retryable by the UI; an
            // invisible transport reconnect would replay output into an exited tab.
            AutoReconnect = false,
            Columns = Math.Clamp(cols <= 0 ? 80 : cols, 1, 1000),
            Rows = Math.Clamp(rows <= 0 ? 24 : rows, 1, 1000)
        };
        if (useGateway)
        {
            opts.GatewayUrl = string.IsNullOrWhiteSpace(gatewayUrl) ? null : gatewayUrl.Trim();
            opts.GatewayToken = string.IsNullOrWhiteSpace(gatewayToken) ? null : gatewayToken.Trim();
            if (Microsoft.Maui.Storage.Preferences.Default.Get("tailnetGateway", false))
            {
                opts.GatewayTailnetIdentity = true;
                opts.GatewayDial = _account is null ? null : _account.DialAsync;
                opts.Password = null; opts.Username = ""; opts.Host = ""; opts.GatewayToken = null;
            }
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

    private sealed class SessionObservation(ISshSession session)
    {
        public ISshSession Session { get; } = session;
        public string State { get; set; } = session.State == SshSessionState.Connected ? "connected" : "connecting";
    }

    private SessionObservation HookSession(ISshSession session)
    {
        lock (_connectionLock)
        {
            var id = session.Id;
            if (_observedSessions.TryGetValue(id, out var existing) && ReferenceEquals(existing.Session, session))
                return existing;
            var observed = new SessionObservation(session);
            _observedSessions[id] = observed;
            if (session is GatewaySshSession gateway)
            {
                gateway.TerminalSizeChanged += (cols, rows) =>
                {
                    lock (_connectionLock)
                        if (IsCurrent(observed)) Post(new { type = "terminal-size", id, cols, rows });
                };
                if (gateway.TerminalColumns > 0 && gateway.TerminalRows > 0)
                    Post(new { type = "terminal-size", id, cols = gateway.TerminalColumns, rows = gateway.TerminalRows });
            }
            session.DataReceived += data =>
            {
                lock (_connectionLock)
                    if (IsCurrent(observed)) Post(new { type = "data", id, data, replay = false });
            };
            session.ErrorReceived += message =>
            {
                lock (_connectionLock)
                {
                    if (!IsCurrent(observed)) return;
                    observed.State = "disconnected";
                    _connectionFallback = "disconnected";
                    PublishConnectionState();
                    Post(new { type = "error", id, message });
                }
            };
            session.Closed += code =>
            {
                lock (_connectionLock)
                {
                    if (!IsCurrent(observed)) return;
                    _observedSessions.TryRemove(id, out _);
                    // An explicit shell exit says the gateway is still reachable.
                    // A transport close with no exit code means the link was lost.
                    _connectionFallback = code.HasValue
                        ? session.Options.UseGateway ? "connected" : "idle"
                        : "disconnected";
                    PublishConnectionState();
                    Post(new { type = "exit", id, code });
                }
            };
            PublishConnectionState();
            return observed;
        }
    }

    private bool IsCurrent(SessionObservation observed) => !_disposed &&
        _observedSessions.TryGetValue(observed.Session.Id, out var current) && ReferenceEquals(current, observed);

    private bool RecordSessionFailure(ISshSession session)
    {
        lock (_connectionLock)
        {
            if (!_observedSessions.TryGetValue(session.Id, out var observed) || !ReferenceEquals(observed.Session, session))
                return false;
            observed.State = "disconnected";
            _connectionFallback = "disconnected";
            PublishConnectionState();
            return true;
        }
    }

    private static bool UsesGateway() => Microsoft.Maui.Storage.Preferences.Default.Get("useGateway", false);

    private static bool HasConnectionSettings() => !string.IsNullOrWhiteSpace(
        Microsoft.Maui.Storage.Preferences.Default.Get(UsesGateway() ? "gatewayUrl" : "host", ""));

    private static string ConnectionName()
    {
        var preferences = Microsoft.Maui.Storage.Preferences.Default;
        if (!UsesGateway()) return preferences.Get("host", "").Trim();
        if (preferences.Get("tailnetGateway", false))
        {
            var name = preferences.Get("tailnetComputerName", "").Trim();
            if (name.Length > 0) return name;
        }
        // Only a hostname is presentation data; credentials, query strings and
        // tokens from a manually entered URL must never reach this summary.
        return Uri.TryCreate(preferences.Get("gatewayUrl", ""), UriKind.Absolute, out var uri) ? uri.Host : "";
    }

    private void SetConnectionFallback(string state)
    {
        lock (_connectionLock)
        {
            _connectionFallback = state;
            PublishConnectionState();
        }
    }

    private void PublishConnectionState()
    {
        // Called under _connectionLock so callbacks retain their ordering.
        var state = !HasConnectionSettings() ? "unconfigured"
            : !_connectionEnabled ? "idle"
            : _observedSessions.Values.Any(s => s.State == "connected") ? "connected"
            : _observedSessions.Values.Any(s => s.State == "connecting") ? "connecting"
            : _connectionFallback;
        Post(new { type = "connection-state", name = ConnectionName(), state });
    }

    private async Task CheckConnectionAsync()
    {
        if (_disposed || _initGate.CurrentCount == 0 || Interlocked.Exchange(ref _checkingConnection, 1) != 0) return;
        try
        {
            int epoch;
            lock (_connectionLock)
            {
                PublishConnectionState();
                if (!_connectionEnabled || !HasConnectionSettings() || !UsesGateway()) return;
                epoch = _connectionEpoch;
            }
            var state = "disconnected";
            GatewaySessionCatalog? catalog = null;
            try
            {
                var options = BuildOptions(80, 24, null, null, null);
                using var client = new GatewayControlClient(options.GatewayUrl!, options.GatewayToken,
                    timeout: TimeSpan.FromSeconds(5), tailnetIdentity: options.GatewayTailnetIdentity, dial: options.GatewayDial);
                catalog = await client.ListCatalogAsync();
                state = "connected";
            }
            catch { /* A failed probe is status, not a terminal error or a shell restart. */ }
            // The probe runs without blocking terminal input or a reconnect. Its
            // result may only change membership while holding the same gate as
            // init, and only if that connection generation is still current.
            await _initGate.WaitAsync();
            try
            {
                lock (_connectionLock)
                {
                    if (_disposed || epoch != _connectionEpoch || !_connectionEnabled) return;
                }
                if (catalog?.Sessions is { } desktopSessions &&
                    (JsonSerializer.Serialize(desktopSessions, JsonOptions) != _catalogJson ||
                        desktopSessions.Any(session => session.Live && !HasHealthyObservation(session.Id))))
                {
                    var sessions = ApplyDesktopCatalog(desktopSessions, _store.LoadSessions());
                    var layouts = PaneLayout.Normalize(_store.LoadLayouts(), sessions);
                    _store.SaveSessions(sessions);
                    _store.SaveLayouts(layouts);
                    Post(new { type = "mobile-session-catalog", sessionsAuthoritative = true,
                        sessions, layouts, liveIds = _remoteIds.ToArray() });
                }
                lock (_connectionLock)
                {
                    if (_disposed) return;
                    _connectionFallback = state;
                    PublishConnectionState();
                }
            }
            finally { _initGate.Release(); }
        }
        finally { Volatile.Write(ref _checkingConnection, 0); }
    }

    private string[] LiveIds()
    {
        try { return _ssh.List().Where(s => s.State is SshSessionState.Connected or SshSessionState.Connecting).Select(s => s.Id).ToArray(); } catch { return []; }
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

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        lock (_connectionLock) _observedSessions.Clear();
        _ssh.Dispose();
    }
}

internal sealed class ExportDto
{
    public int Version { get; set; }
    public DateTime ExportedAt { get; set; }
    public AppSettings? AppSettings { get; set; }
    public List<SessionRecord>? Sessions { get; set; }
    public List<SessionRecord>? ArchivedSessions { get; set; }
    public List<PaneLayout>? Layouts { get; set; }
    public List<LaunchProfile>? Profiles { get; set; }
    public ExportConnection? Connection { get; set; }
}

internal sealed class ExportConnection
{
    public string? Host { get; set; }
    public int Port { get; set; }
    public string? Username { get; set; }
    public string? Password { get; set; }
    public bool UseGateway { get; set; }
    public string? GatewayUrl { get; set; }
    public string? GatewayToken { get; set; }
    public bool UseTailscale { get; set; }
    public string? TailscaleAuthKey { get; set; }
    public string? TailscaleHostname { get; set; }
}

internal static class AppInfo
{
    public static string Version => Microsoft.Maui.ApplicationModel.AppInfo.Current.VersionString;
}
