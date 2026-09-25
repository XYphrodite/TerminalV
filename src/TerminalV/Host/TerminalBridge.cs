using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Media;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using SelfUpdateKit;
using TerminalV.Data;
using TerminalV.Diagnostics;
using TerminalV.Extensions;
using TerminalV.Pty;
using TerminalV.Shell;
using TerminalV.Update;

namespace TerminalV.Host;

internal sealed class TerminalBridge : IDisposable
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly Dispatcher _dispatcher;
    private readonly CoreWebView2 _webView;
    private readonly ConcurrentDictionary<string, ConPtySession> _sessions = new();
    private readonly ShellInfo _shell;
    private readonly int _buildNumber;
    private readonly bool _canUpdate;
    private readonly CancellationTokenSource _updateCts = new();
    private readonly AppDatabase _db = new();
    private readonly SessionClient _host = new();
    private readonly ExtensionManager? _extensions;
    private readonly string? _extensionsError;
    private int _updateBusy;
    private int _catalogBusy;
    private int _shortcutsBusy;
    private readonly ShortcutService _shortcuts = new(Environment.ProcessPath);
    private readonly Stopwatch _bellClock = Stopwatch.StartNew();
    private long _lastBellMs = -2000;
    private bool _disposed;
    private readonly Func<object>? _gatewayStatus;
    private string? _flushRequestId;
    private TaskCompletionSource<bool>? _flushCompletion;

    public event Action? SettingsChanged;
    public event Action? RestartRequested;

    public TerminalBridge(Dispatcher dispatcher, CoreWebView2 webView, Func<object>? gatewayStatus = null)
    {
        _dispatcher = dispatcher;
        _webView = webView;
        _gatewayStatus = gatewayStatus;
        _shell = ShellResolver.Resolve();
        _buildNumber = Environment.OSVersion.Version.Build;
        _canUpdate = AppVersion.CanSelfUpdate(Environment.ProcessPath);
        _host.Data += (id, data, replay) => Post(new { type = "data", id, data, replay });
        _host.Exited += (id, code) => Post(new { type = "exit", id, code });
        _host.DirectoryChanged += (id, cwd, notice) => Post(new { type = "cwd", id, cwd, notice });
        _host.Error += (id, message) => Post(new { type = "error", id, message });
        try
        {
            _extensions = new ExtensionManager(
                Path.Combine(AppPaths.Root, "extensions"), Path.Combine(AppPaths.Root, "extension-data"),
                Post, (id, message) => Diag.Log("extensions", id, message));
            foreach (var (name, directory) in _extensions.UiMappings)
                _webView.SetVirtualHostNameToFolderMapping(name, directory, CoreWebView2HostResourceAccessKind.Allow);
        }
        catch (Exception ex)
        {
            _extensionsError = ex.Message;
            if (_extensions is not null) _ = _extensions.StopAsync();
            Diag.Log("extensions", "Extension manager initialization failed", ex.ToString());
        }
    }

    public void SendInit()
    {
        Post(new
        {
            type = "init",
            shellName = _shell.DisplayName,
            buildNumber = _buildNumber,
            version = AppVersion.Informational,
            updateSupported = _canUpdate,
            shortcutsSupported = _shortcuts.Supported,
            extensionsSupported = _extensions is not null && _extensionsError is null,
            extensionsError = _extensionsError,
            settings = _db.LoadSettings(),
            sessions = _db.LoadSessions(),
            layouts = _db.LoadLayouts(),
            profiles = _db.LoadProfiles(),
            liveIds = LiveIds(),
            cwdTrackingSupported = _host.CwdTrackingSupported,
            launchProfilesSupported = _host.LaunchProfilesSupported,
            environmentRefreshSupported = _host.EnvironmentRefreshSupported,
            fonts = SystemFonts(),
            gateway = _gatewayStatus?.Invoke()
        });

        if (_canUpdate)
        {
            _ = CheckUpdatesAsync(silent: true);
        }
    }

    public async Task FlushAsync()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(TerminalBridge));
        if (_flushCompletion is not null) throw new InvalidOperationException("Сохранение уже выполняется.");

        var requestId = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _flushRequestId = requestId;
        _flushCompletion = completion;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try
        {
            // Executing JS only queues WebMessages. Wait for the corresponding
            // database commit, not an arbitrary delay after ExecuteScriptAsync.
            var result = await _webView.ExecuteScriptAsync(
                $"typeof window.terminalvFlush === 'function' ? (window.terminalvFlush({JsonSerializer.Serialize(requestId)}), true) : false")
                .WaitAsync(timeout.Token);
            if (result == "false") return; // The interface has not loaded yet.
            await completion.Task.WaitAsync(timeout.Token);
        }
        catch (OperationCanceledException) when (timeout.IsCancellationRequested)
        {
            throw new TimeoutException("Интерфейс не подтвердил сохранение сессий за 10 секунд.");
        }
        finally
        {
            _flushRequestId = null;
            _flushCompletion = null;
        }
    }

    public void Handle(string json)
    {
        IncomingMessage? message;
        try
        {
            message = JsonSerializer.Deserialize<IncomingMessage>(json, JsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (message?.Type is null)
        {
            return;
        }

        if (message.Type.StartsWith("extensions:", StringComparison.Ordinal))
        {
            if (_extensions is not null && _extensionsError is null)
                _ = Task.Run(() => _extensions.HandleAsync(message.Type, message.ExtensionId, message.RequestId,
                    message.Method, message.Args, message.Enabled));
            else
                Post(new { type = "extensions:result", requestId = message.RequestId, extensionId = message.ExtensionId,
                    error = _extensionsError ?? "Extensions are unavailable." });
            return;
        }

        switch (message.Type)
        {
            case "create":
                Create(message);
                break;
            case "attach":
                if (message.Id is not null && _host.Ensure())
                {
                    _host.Attach(message.Id);
                }
                break;
            case "write":
                if (message.Id is not null && message.Data is not null)
                {
                    // Don't block WebView2's WebMessageReceived (UI thread) on ConPTY backpressure.
                    // Even 100 chars with bracketed paste (muse) can stall if the TUI hasn't drained.
                    // Keep typing ("a") synchronous for tests; bracketed paste goes async.
                    var isPaste = message.Data.Contains("\u001b[200~");
                    var swBridge = Stopwatch.StartNew();
                    if (isPaste || message.Data.Length > 4000)
                    {
                        var writeId = message.Id;
                        var writeData = message.Data;
                        Diag.Log("bridge", $"write async id={writeId} len={writeData.Length} isPaste={isPaste}", null);
                        _ = Task.Run(() =>
                        {
                            var sw = Stopwatch.StartNew();
                            try
                            {
                                if (_host.Ensure())
                                    _host.Write(writeId, writeData);
                                else if (_sessions.TryGetValue(writeId, out var writing))
                                    writing.Write(writeData);
                            }
                            finally
                            {
                                sw.Stop();
                                if (sw.ElapsedMilliseconds > 30)
                                    Diag.Log("bridge", $"write async done id={writeId} ms={sw.ElapsedMilliseconds}", null);
                            }
                        });
                    }
                    else
                    {
                        Diag.Log("bridge", $"write sync id={message.Id} len={message.Data.Length}", null);
                        var sw = Stopwatch.StartNew();
                        if (_host.Ensure())
                            _host.Write(message.Id, message.Data);
                        else if (_sessions.TryGetValue(message.Id, out var writing))
                            writing.Write(message.Data);
                        sw.Stop();
                        if (sw.ElapsedMilliseconds > 30)
                            Diag.Log("bridge", $"write sync done id={message.Id} ms={sw.ElapsedMilliseconds}", null);
                    }
                    swBridge.Stop();
                }
                break;
            case "resize":
                if (message.Id is not null)
                {
                    if (_host.Ensure())
                    {
                        _host.Resize(message.Id, message.Cols, message.Rows);
                    }
                    else if (_sessions.TryGetValue(message.Id, out var resizing))
                    {
                        resizing.Resize(message.Cols, message.Rows);
                    }
                }
                break;
            case "kill":
                if (message.Id is not null)
                {
                    Kill(message.Id);
                }
                break;
            case "clipboard-read":
                PostClipboard(message.RequestId);
                break;
            case "paste-file-store":
                StorePasteFile(message.RequestId, message.Data);
                break;
            case "clipboard-write":
                if (!string.IsNullOrEmpty(message.Data))
                {
                    _dispatcher.BeginInvoke(() =>
                    {
                        try
                        {
                            Clipboard.SetText(message.Data);
                        }
                        catch
                        {
                        }
                    });
                }
                break;
            case "bell":
                // A final native guard; the UI applies per-session mute/focus policy.
                var now = _bellClock.ElapsedMilliseconds;
                if (now - _lastBellMs >= 2000)
                {
                    _lastBellMs = now;
                    SystemSounds.Beep.Play();
                }
                break;
            case "open-author":
                _dispatcher.BeginInvoke(() =>
                {
                    // A fixed HTTPS destination, not an arbitrary URL/command from the WebView.
                    const string authorUrl = "https://github.com/XYphrodite";
                    try
                    {
                        using var browser = Process.Start(new ProcessStartInfo(authorUrl) { UseShellExecute = true });
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        MessageBox.Show("Не удалось открыть браузер. Профиль автора: " + authorUrl,
                            "TerminalV", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                });
                break;
            case "open-link":
                _dispatcher.BeginInvoke(() =>
                {
                    var uri = message.Uri;
                    if (string.IsNullOrWhiteSpace(uri))
                    {
                        return;
                    }

                    uri = uri.Trim();
                    // Only http(s) is allowed to be opened externally; anything else is ignored.
                    if (!Uri.TryCreate(uri, UriKind.Absolute, out var parsed)
                        || (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
                    {
                        return;
                    }

                    try
                    {
                        using var browser = Process.Start(new ProcessStartInfo(parsed.AbsoluteUri) { UseShellExecute = true });
                    }
                    catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
                    {
                        MessageBox.Show("Не удалось открыть браузер. Ссылка: " + parsed.AbsoluteUri,
                            "TerminalV", MessageBoxButton.OK, MessageBoxImage.Warning);
                    }
                });
                break;
            case "update-check":
                _ = CheckUpdatesAsync(silent: false);
                break;
            case "update-apply":
                _ = ApplyUpdateAsync();
                break;
            case "persist-settings":
                PersistSettings(message.Data, message.RequestId);
                break;
            case "persist-sessions":
                PersistSessions(message);
                break;
            case "flush-skipped":
                CompleteFlush(message.RequestId);
                break;
            case "persist-profiles":
                try
                {
                    _db.SaveProfiles(message.Profiles ?? throw new ArgumentException("Нет списка профилей."));
                    Post(new { type = "profiles-saved", requestId = message.RequestId, profiles = _db.LoadProfiles() });
                }
                catch (Exception ex) when (ex is ArgumentException or Microsoft.Data.Sqlite.SqliteException)
                {
                    Post(new { type = "profiles-saved", requestId = message.RequestId, error = ex.Message });
                }
                break;
            case "list-launch-targets":
                _ = SendLaunchTargetsAsync(message.RequestId);
                break;
            case "create-shortcuts":
                _ = CreateShortcutsAsync(message);
                break;
            case "ready":
                SendInit();
                break;
            case "diag":
                if (message.Data is not null)
                    Diag.Log("ui", message.Data, message.Id);
                break;
            case "pick-background":
                PickBackground();
                break;
        }
    }

    public Task StopExtensionsAsync() => _extensions?.StopAsync() ?? Task.CompletedTask;

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _ = StopExtensionsAsync();
        _updateCts.Cancel();
        _updateCts.Dispose();
        _db.Dispose();
        _host.Dispose();
        foreach (var id in _sessions.Keys)
        {
            if (_sessions.TryRemove(id, out var session))
            {
                session.Dispose();
            }
        }
    }

    private async Task CreateShortcutsAsync(IncomingMessage message)
    {
        if (Interlocked.Exchange(ref _shortcutsBusy, 1) == 1)
        {
            Post(new { type = "shortcuts-created", requestId = message.RequestId, error = "Ярлыки уже создаются. Дождитесь завершения." });
            return;
        }
        try
        {
            var results = await _shortcuts.CreateAsync(message.StartMenu, message.Desktop).ConfigureAwait(false);
            Post(new { type = "shortcuts-created", requestId = message.RequestId, results });
        }
        catch (Exception error)
        {
            Post(new { type = "shortcuts-created", requestId = message.RequestId, error = error.Message });
        }
        finally { Interlocked.Exchange(ref _shortcutsBusy, 0); }
    }

    private async Task CheckUpdatesAsync(bool silent)
    {
        if (!_canUpdate)
        {
            if (!silent)
            {
                Post(new
                {
                    type = "update",
                    status = "unsupported",
                    message = "Обновление доступно только для установленной копии TerminalV."
                });
            }

            return;
        }

        try
        {
            var options = TerminalVUpdate.Options(TerminalVUpdate.InstalledVariant(Environment.ProcessPath));
            using var source = new GitHubReleaseSource(options);
            var service = new SelfUpdateService(Environment.ProcessPath!, AppVersion.Current, source, options);
            var report = await service.CheckAsync(_updateCts.Token).ConfigureAwait(false);
            if (report.Status == SelfUpdateStatus.AlreadyCurrent)
            {
                if (!silent)
                {
                    PostUpdate("current", report);
                }

                return;
            }

            PostUpdate("available", report);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex) when (silent)
        {
            Debug.WriteLine(ex);
        }
        catch (Exception ex)
        {
            Post(new { type = "update", status = "error", message = ex.Message });
        }
    }

    private async Task ApplyUpdateAsync()
    {
        if (!_canUpdate)
        {
            Post(new
            {
                type = "update",
                status = "unsupported",
                message = "Обновление доступно только для установленной копии TerminalV."
            });
            return;
        }

        if (Interlocked.Exchange(ref _updateBusy, 1) == 1)
        {
            return;
        }

        Post(new { type = "update", status = "downloading", current = AppVersion.Informational });
        try
        {
            var options = TerminalVUpdate.Options(TerminalVUpdate.InstalledVariant(Environment.ProcessPath));
            using var source = new GitHubReleaseSource(options);
            var service = new SelfUpdateService(Environment.ProcessPath!, AppVersion.Current, source, options);
            var report = await service.UpdateAsync(new SelfUpdateRequest(), _updateCts.Token).ConfigureAwait(false);
            if (report.Status == SelfUpdateStatus.AlreadyCurrent)
            {
                PostUpdate("current", report);
                return;
            }

            PostUpdate("restarting", report);
            Restart();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Post(new { type = "update", status = "error", message = ex.Message });
        }
        finally
        {
            Interlocked.Exchange(ref _updateBusy, 0);
        }
    }

    private void Restart()
    {
        // The window owns saving and closing. Application.Shutdown() bypasses
        // its asynchronous cancellation; launching here could race a failed save.
        _dispatcher.BeginInvoke(() => { if (!_disposed) RestartRequested?.Invoke(); });
    }

    private void PostUpdate(string status, SelfUpdateReport report) =>
        Post(new
        {
            type = "update",
            status,
            current = report.Installed.ToString(),
            latest = report.Release.ToString(),
            tag = report.Tag
        });

    private string[] LiveIds()
    {
        try
        {
            return _host.Ensure() ? _host.List() : [];
        }
        catch
        {
            return [];
        }
    }

    private void Create(IncomingMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Id))
        {
            return;
        }

        try
        {
            var isWsl = message.Shell == "wsl";
            if (isWsl && message.Cwd is not null)
                throw new ArgumentException("Быстрый запуск WSL открывает домашнюю папку Linux.");
            var cwd = WorkingDirectory.Resolve(isWsl ? null : message.Cwd);
            if (message.Shell is not null && cwd.Notice is not null)
                throw new DirectoryNotFoundException("Папка профиля недоступна. Запуск отменён: " + message.Cwd);
            if (!isWsl) Post(new { type = "cwd", id = message.Id, cwd = cwd.Path, notice = cwd.Notice });
            if (_host.Ensure())
            {
                _host.Create(message.Id, Math.Max(message.Cols, 1), Math.Max(message.Rows, 1),
                    isWsl || message.Cwd is null ? null : cwd.Path, message.Shell, message.StartupCommand, message.WslDistribution);
                return;
            }

            if (message.Shell is not null && _sessions.ContainsKey(message.Id))
                throw new InvalidOperationException("Сессия уже работает. Повторный запуск отменён.");
            var shell = ShellResolver.Resolve(isWsl || (message.Cwd is null && message.Shell is null) ? null : cwd.Path, message.Shell,
                message.Shell is null ? null : message.StartupCommand, message.WslDistribution);
            KillLocal(message.Id);
            ConPtySession.Start(
                message.Id,
                shell.CommandLine,
                cwd.Path,
                Math.Max(message.Cols, 1),
                Math.Max(message.Rows, 1), session =>
                {
                    session.Output += data => Post(new { type = "data", id = session.Id, data });
                    session.DirectoryChanged += path => { if (!isWsl) Post(new { type = "cwd", id = session.Id, cwd = path }); };
                    session.Exited += code => Post(new { type = "exit", id = session.Id, code });
                    _sessions[session.Id] = session;
                });
        }
        catch (Exception ex)
        {
            Post(new { type = "error", id = message.Id, message = ex.Message });
        }
    }

    private async Task SendLaunchTargetsAsync(string? requestId)
    {
        if (Interlocked.Exchange(ref _catalogBusy, 1) == 1)
        {
            Post(new { type = "launch-targets", requestId, error = "Список уже обновляется. Повторите попытку через несколько секунд." });
            return;
        }
        try
        {
            var catalog = await LaunchCatalog.DiscoverAsync(_updateCts.Token).ConfigureAwait(false);
            Post(new { type = "launch-targets", requestId, targets = catalog.Targets, notice = catalog.Notice });
        }
        catch (OperationCanceledException) { }
        catch (Exception ex) { Post(new { type = "launch-targets", requestId, error = ex.Message }); }
        finally { Interlocked.Exchange(ref _catalogBusy, 0); }
    }

    private void Kill(string id)
    {
        if (_host.Ensure())
        {
            _host.Kill(id);
        }

        KillLocal(id);
    }

    private void KillLocal(string id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            session.Dispose();
        }
    }

    private void PersistSettings(string? json, string? requestId)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(json, JsonOptions);
            if (settings is not null)
            {
                // An empty token never wipes the stored one: the desktop UI
                // always round-trips the real value, and clearing the token
                // would silently open the LAN listener to everyone.
                if (string.IsNullOrEmpty(settings.GatewayToken))
                {
                    try
                    {
                        var stored = _db.LoadSettings();
                        if (!string.IsNullOrEmpty(stored.GatewayToken))
                        {
                            settings.GatewayToken = stored.GatewayToken;
                        }
                    }
                    catch
                    {
                    }
                }

                if (settings.GatewayPort is < 1 or > 65535)
                {
                    settings.GatewayPort = 5454;
                }

                _db.SaveSettings(settings);
                SettingsChanged?.Invoke();
            }
        }
        catch (Exception ex) when (ex is JsonException or Microsoft.Data.Sqlite.SqliteException)
        {
            Diag.Log("persistence", "Settings save failed", ex.ToString());
            CompleteFlush(requestId, ex);
        }
    }

    private void PersistSessions(IncomingMessage message)
    {
        try
        {
            List<SessionRecord>? sessions = message.Sessions;
            if (sessions is null && !string.IsNullOrWhiteSpace(message.Data))
            {
                sessions = JsonSerializer.Deserialize<List<SessionRecord>>(message.Data, JsonOptions);
            }

            if (sessions is null)
            {
                CompleteFlush(message.RequestId, new InvalidOperationException("Не получены данные сессий для сохранения."));
                return;
            }

            _db.SaveSessions(sessions, message.Layouts);
            CompleteFlush(message.RequestId);
        }
        catch (Exception ex) when (ex is JsonException or Microsoft.Data.Sqlite.SqliteException or ArgumentException or InvalidOperationException)
        {
            Diag.Log("persistence", "Session save failed", ex.ToString());
            CompleteFlush(message.RequestId, ex);
        }
    }

    private void CompleteFlush(string? requestId, Exception? error = null)
    {
        if (requestId is null || requestId != _flushRequestId) return;
        if (error is null) _flushCompletion?.TrySetResult(true);
        else _flushCompletion?.TrySetException(error);
    }

    private void PickBackground()
    {
        _dispatcher.BeginInvoke(() =>
        {
            var dialog = new OpenFileDialog
            {
                Title = "Фон сессии",
                Filter = "Изображения|*.png;*.jpg;*.jpeg;*.webp;*.bmp;*.gif|Все файлы|*.*"
            };
            if (dialog.ShowDialog() != true)
            {
                return;
            }

            try
            {
                var url = _db.ImportBackground(dialog.FileName);
                Post(new { type = "background-picked", path = url });
            }
            catch (Exception ex)
            {
                Post(new { type = "update", status = "error", message = ex.Message });
            }
        });
    }

    private static List<string> SystemFonts()
    {
        try
        {
            return Fonts.SystemFontFamilies
                .Select(font => font.Source)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .OrderBy(name => name, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return
            [
                "Cascadia Code",
                "Cascadia Mono",
                "Consolas",
                "Courier New",
                "Segoe UI"
            ];
        }
    }

    private void PostClipboard(string? requestId)
    {
        _dispatcher.BeginInvoke(() =>
        {
            string data;
            try
            {
                data = Clipboard.ContainsText() ? Clipboard.GetText() : "";
            }
            catch
            {
                data = "";
            }

            Post(new { type = "clipboard-data", requestId, data });
        });
    }

    private void StorePasteFile(string? requestId, string? data)
    {
        if (string.IsNullOrEmpty(data))
        {
            Post(new { type = "paste-file-stored", requestId, error = "empty clipboard" });
            return;
        }

        _ = Task.Run(() =>
        {
            try
            {
                var path = PasteFileStore.Save(data);
                Diag.Log("bridge", $"paste-file stored len={data.Length} path={path}", null);
                Post(new { type = "paste-file-stored", requestId, path });
            }
            catch (Exception ex)
            {
                Diag.Log("bridge", $"paste-file store failed: {ex.Message}", null);
                Post(new { type = "paste-file-stored", requestId, error = ex.Message });
            }
        });
    }

    private void Post(object payload)
    {
        if (_disposed)
        {
            return;
        }

        var json = JsonSerializer.Serialize(payload, JsonOptions);
        _dispatcher.BeginInvoke(() =>
        {
            if (_disposed)
            {
                return;
            }

            try
            {
                _webView.PostWebMessageAsJson(json);
            }
            catch (ObjectDisposedException)
            {
            }
            catch (InvalidOperationException)
            {
            }
        });
    }
}
