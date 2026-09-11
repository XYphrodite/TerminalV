using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Win32;
using TerminalV.Data;
using TerminalV.Pty;
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
    private readonly string _cwd;
    private readonly int _buildNumber;
    private readonly bool _canUpdate;
    private readonly CancellationTokenSource _updateCts = new();
    private readonly AppDatabase _db = new();
    private int _updateBusy;
    private bool _disposed;

    public TerminalBridge(Dispatcher dispatcher, CoreWebView2 webView)
    {
        _dispatcher = dispatcher;
        _webView = webView;
        _shell = ShellResolver.Resolve();
        _cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _buildNumber = Environment.OSVersion.Version.Build;
        _canUpdate = AppVersion.CanSelfUpdate(Environment.ProcessPath);
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
            settings = _db.LoadSettings(),
            sessions = _db.LoadSessions(),
            fonts = SystemFonts()
        });

        if (_canUpdate)
        {
            _ = CheckUpdatesAsync(silent: true);
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

        switch (message.Type)
        {
            case "create":
                Create(message);
                break;
            case "write":
                if (message.Id is not null && message.Data is not null && _sessions.TryGetValue(message.Id, out var writing))
                {
                    writing.Write(message.Data);
                }
                break;
            case "resize":
                if (message.Id is not null && _sessions.TryGetValue(message.Id, out var resizing))
                {
                    resizing.Resize(message.Cols, message.Rows);
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
            case "update-check":
                _ = CheckUpdatesAsync(silent: false);
                break;
            case "update-apply":
                _ = ApplyUpdateAsync();
                break;
            case "persist-settings":
                PersistSettings(message.Data);
                break;
            case "persist-sessions":
                PersistSessions(message);
                break;
            case "ready":
                SendInit();
                break;
            case "pick-background":
                PickBackground();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _updateCts.Cancel();
        _updateCts.Dispose();
        _db.Dispose();
        foreach (var id in _sessions.Keys)
        {
            Kill(id);
        }
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
            using var source = new GitHubReleaseSource();
            var service = new SelfUpdateService(Environment.ProcessPath!, AppVersion.Current, source);
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
            using var source = new GitHubReleaseSource();
            var service = new SelfUpdateService(Environment.ProcessPath!, AppVersion.Current, source);
            var report = await service.ApplyAsync(_updateCts.Token).ConfigureAwait(false);
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
        var path = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var quoted = "\"" + path.Replace("\"", "\\\"") + "\"";
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping 127.0.0.1 -n 3 >nul & start \"\" " + quoted,
            UseShellExecute = false,
            CreateNoWindow = true
        });

        _dispatcher.BeginInvoke(() => Application.Current.Shutdown());
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

    private void Create(IncomingMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Id))
        {
            return;
        }

        Kill(message.Id);

        try
        {
            var cwd = !string.IsNullOrWhiteSpace(message.Cwd) && Directory.Exists(message.Cwd)
                ? message.Cwd
                : _cwd;
            var session = ConPtySession.Start(
                message.Id,
                _shell.CommandLine,
                cwd,
                Math.Max(message.Cols, 1),
                Math.Max(message.Rows, 1));

            session.Output += data => Post(new { type = "data", id = session.Id, data });
            session.Exited += code => Post(new { type = "exit", id = session.Id, code });

            if (!_sessions.TryAdd(session.Id, session))
            {
                session.Dispose();
            }
        }
        catch (Exception ex)
        {
            Post(new { type = "error", id = message.Id, message = ex.Message });
        }
    }

    private void Kill(string id)
    {
        if (_sessions.TryRemove(id, out var session))
        {
            session.Dispose();
        }
    }

    private void PersistSettings(string? json)
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
                _db.SaveSettings(settings);
            }
        }
        catch (JsonException)
        {
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
                return;
            }

            _db.SaveSessions(sessions);
        }
        catch (JsonException)
        {
        }
        catch (Microsoft.Data.Sqlite.SqliteException)
        {
        }
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
