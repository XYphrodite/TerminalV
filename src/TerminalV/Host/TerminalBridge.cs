using System.Collections.Concurrent;
using System.Text.Json;
using System.Windows;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using TerminalV.Pty;

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
    private bool _disposed;

    public TerminalBridge(Dispatcher dispatcher, CoreWebView2 webView)
    {
        _dispatcher = dispatcher;
        _webView = webView;
        _shell = ShellResolver.Resolve();
        _cwd = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        _buildNumber = Environment.OSVersion.Version.Build;
    }

    public void SendInit()
    {
        Post(new
        {
            type = "init",
            shellName = _shell.DisplayName,
            buildNumber = _buildNumber
        });
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
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        foreach (var id in _sessions.Keys)
        {
            Kill(id);
        }
    }

    private void Create(IncomingMessage message)
    {
        if (string.IsNullOrWhiteSpace(message.Id))
        {
            return;
        }

        Kill(message.Id);

        try
        {
            var session = ConPtySession.Start(
                message.Id,
                _shell.CommandLine,
                _cwd,
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
