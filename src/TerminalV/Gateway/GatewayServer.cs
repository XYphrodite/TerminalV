using System.IO;
using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TerminalV.Ssh;

namespace TerminalV.Gateway;

/// <summary>
/// Mirror gateway: listens on ws://0.0.0.0:port and exposes the desktop's
/// live ConPTY sessions to remote clients (TerminalV.Mobile).
/// Wire protocol: <see cref="GatewayProtocol"/>.
/// </summary>
internal sealed class GatewayServer : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly IGatewaySessionBackend _backend;
    private readonly int _port;
    private readonly string? _token;
    private HttpListener? _listener;
    private CancellationTokenSource? _cts;
    private Task? _loop;
    private bool _disposed;

    public GatewayServer(IGatewaySessionBackend backend, int port, string? token = null)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        _port = port;
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
    }

    public int Port => _port;
    public bool IsRunning => _loop is { IsCompleted: false };
    public string Status { get; private set; } = "Остановлен.";

    public object Describe() => new { enabled = true, port = _port, listening = IsRunning, status = Status };

    public void Start()
    {
        if (_disposed)
            throw new ObjectDisposedException(nameof(GatewayServer));
        if (IsRunning)
            return;

        var listener = new HttpListener();
        listener.Prefixes.Add($"http://*:{_port}/");
        try
        {
            listener.Start();
        }
        catch (HttpListenerException ex)
        {
            Status = "Нет прав на прослушивание порта. Запустите TerminalV от администратора или разрешите URL: netsh http add urlacl url=http://*:" + _port + "/ user=" + Environment.UserDomainName + "\\" + Environment.UserName;
            throw new InvalidOperationException(Status, ex);
        }

        _listener = listener;
        _cts = new CancellationTokenSource();
        _loop = Task.Run(() => AcceptLoopAsync(_cts.Token));
        Status = $"Слушает ws://0.0.0.0:{_port}.";
    }

    public void Stop()
    {
        try { _cts?.Cancel(); } catch { }
        try { _listener?.Stop(); } catch { }
        try { _loop?.Wait(TimeSpan.FromSeconds(2)); } catch { }
        _listener?.Close();
        _listener = null;
        _cts?.Dispose();
        _cts = null;
        _loop = null;
        Status = "Остановлен.";
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        Stop();
    }

    private async Task AcceptLoopAsync(CancellationToken token)
    {
        var listener = _listener;
        if (listener is null)
            return;
        while (!token.IsCancellationRequested)
        {
            HttpListenerContext context;
            try
            {
                context = await listener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException) { break; }
            catch (ObjectDisposedException) { break; }
            _ = Task.Run(() => HandleAsync(context, token));
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken token)
    {
        if (!context.Request.IsWebSocketRequest)
        {
            context.Response.StatusCode = 426;
            var hint = Encoding.UTF8.GetBytes("TerminalV mirror gateway. Use WebSocket.");
            try
            {
                context.Response.OutputStream.Write(hint, 0, hint.Length);
                context.Response.Close();
            }
            catch { }
            return;
        }

        if (!Authorized(context.Request))
        {
            context.Response.StatusCode = 401;
            try { context.Response.Close(); } catch { }
            return;
        }

        WebSocketContext wsContext;
        try
        {
            wsContext = await context.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
        }
        catch
        {
            try { context.Response.Close(); } catch { }
            return;
        }

        await ServeAsync(wsContext.WebSocket, token).ConfigureAwait(false);
    }

    private bool Authorized(HttpListenerRequest request)
    {
        if (_token is null)
            return true;
        var header = request.Headers["Authorization"];
        if (header == "Bearer " + _token)
            return true;
        return request.QueryString["token"] == _token;
    }

    private async Task ServeAsync(WebSocket ws, CancellationToken serverToken)
    {
        var sendGate = new SemaphoreSlim(1, 1);
        var bound = new HashSet<string>();
        var boundGate = new object();
        string? defaultId = null;

        bool IsBound(string id)
        {
            lock (boundGate) return bound.Contains(id);
        }

        void Bind(string id)
        {
            lock (boundGate) { bound.Add(id); }
            defaultId = id;
        }

        void Unbind(string id)
        {
            lock (boundGate) { bound.Remove(id); }
        }

        async Task SendAsync(string json)
        {
            var bytes = Encoding.UTF8.GetBytes(json);
            await sendGate.WaitAsync(serverToken).ConfigureAwait(false);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(bytes, WebSocketMessageType.Text, true, serverToken).ConfigureAwait(false);
            }
            catch { }
            finally { sendGate.Release(); }
        }

        void OnData(string id, string data)
        {
            if (IsBound(id)) _ = SendAsync(GatewayProtocol.DataMessage(id, data));
        }

        void OnExit(string id, uint code)
        {
            if (IsBound(id))
            {
                Unbind(id);
                _ = SendAsync(GatewayProtocol.ExitMessage(id, code));
            }
        }

        void OnCwd(string id, string cwd)
        {
            if (IsBound(id)) _ = SendAsync(JsonSerializer.Serialize(new { type = "cwd", id, cwd }, Json));
        }

        void OnError(string id, string message)
        {
            if (IsBound(id) || defaultId is null) _ = SendAsync(GatewayProtocol.ErrorMessage(id, message));
        }

        _backend.Data += OnData;
        _backend.Exited += OnExit;
        _backend.DirectoryChanged += OnCwd;
        _backend.Error += OnError;

        try
        {
            var first = await ReceiveAsync(ws, serverToken).ConfigureAwait(false);
            if (first is null || first.Value.binary)
            {
                await CloseAsync(ws).ConfigureAwait(false);
                return;
            }

            var hello = Encoding.UTF8.GetString(first.Value.bytes);
            if (!GatewayProtocol.TryParseHandshake(hello, out var handshake) || handshake is null)
            {
                await SendAsync(GatewayProtocol.ErrorMessage(null, "Ожидался handshake {type:\"connect\"}.")).ConfigureAwait(false);
                await CloseAsync(ws).ConfigureAwait(false);
                return;
            }

            if (!handshake.Control)
            {
                if (!string.IsNullOrWhiteSpace(handshake.SessionId))
                {
                    var id = handshake.SessionId;
                    if (IsLive(id))
                    {
                        try
                        {
                            // Bind first: the backend may replay the snapshot
                            // synchronously inside Attach.
                            Bind(id);
                            _backend.Attach(id);
                            await SendAsync(GatewayProtocol.AttachedReply(id)).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            Unbind(id);
                            await SendAsync(GatewayProtocol.ErrorMessage(id, ex.Message)).ConfigureAwait(false);
                        }
                    }
                    else
                    {
                        await SendAsync(GatewayProtocol.ErrorMessage(id, "Сессия завершилась. Обновите список.")).ConfigureAwait(false);
                    }
                }
                else
                {
                    var id = Guid.NewGuid().ToString("N");
                    try
                    {
                        Bind(id);
                        _backend.Create(id, handshake.Cols, handshake.Rows, null, null, null, null);
                        await SendAsync(GatewayProtocol.AttachedReply(id)).ConfigureAwait(false);
                    }
                    catch (Exception ex)
                    {
                        Unbind(id);
                        await SendAsync(GatewayProtocol.ErrorMessage(null, ex.Message)).ConfigureAwait(false);
                    }
                }
            }

            while (ws.State == WebSocketState.Open && !serverToken.IsCancellationRequested)
            {
                var frame = await ReceiveAsync(ws, serverToken).ConfigureAwait(false);
                if (frame is null)
                    break;
                if (frame.Value.binary)
                {
                    if (defaultId is not null)
                        SafeBackend(() => _backend.Write(defaultId, Encoding.UTF8.GetString(frame.Value.bytes)),
                            id => SendAsync(GatewayProtocol.ErrorMessage(id, "Запись не удалась.")));
                    continue;
                }

                await DispatchAsync(Encoding.UTF8.GetString(frame.Value.bytes), Bind, Unbind,
                    () => defaultId, SendAsync).ConfigureAwait(false);
            }
        }
        catch (WebSocketException) { }
        catch (OperationCanceledException) { }
        finally
        {
            _backend.Data -= OnData;
            _backend.Exited -= OnExit;
            _backend.DirectoryChanged -= OnCwd;
            _backend.Error -= OnError;
            sendGate.Dispose();
            await CloseAsync(ws).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(string text, Action<string> bind,
        Action<string> unbind, Func<string?> defaultId, Func<string, Task> send)
    {
        string? type;
        try
        {
            using var doc = JsonDocument.Parse(text);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object ||
                !root.TryGetProperty("type", out var t) || t.GetString() is not { } tt)
                return;
            type = tt;

            string? Id() => root.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString() : null;
            int Num(string name, int fallback) =>
                root.TryGetProperty(name, out var n) && n.TryGetInt32(out var v) ? Math.Clamp(v, 1, 1000) : fallback;

            switch (type)
            {
                case GatewayProtocol.List:
                    await send(GatewayProtocol.SessionsReply(SafeLiveIds())).ConfigureAwait(false);
                    break;
                case GatewayProtocol.Attach:
                    if (Id() is { } attachId)
                    {
                        if (!IsLive(attachId))
                        {
                            await send(GatewayProtocol.ErrorMessage(attachId, "Сессия завершилась. Обновите список.")).ConfigureAwait(false);
                        }
                        else
                        {
                            try
                            {
                                bind(attachId);
                                _backend.Attach(attachId);
                                await send(GatewayProtocol.AttachedReply(attachId)).ConfigureAwait(false);
                            }
                            catch (Exception ex)
                            {
                                unbind(attachId);
                                await send(GatewayProtocol.ErrorMessage(attachId, ex.Message)).ConfigureAwait(false);
                            }
                        }
                    }
                    break;
                case GatewayProtocol.Detach:
                    if (Id() is { } detachId) unbind(detachId);
                    break;
                case GatewayProtocol.Create:
                    {
                        var id = Id();
                        if (string.IsNullOrWhiteSpace(id)) id = Guid.NewGuid().ToString("N");
                        string? cwd = root.TryGetProperty("cwd", out var cwdEl) && cwdEl.ValueKind == JsonValueKind.String ? cwdEl.GetString() : null;
                        string? shell = root.TryGetProperty("shell", out var sh) && sh.ValueKind == JsonValueKind.String ? sh.GetString() : null;
                        string? cmd = root.TryGetProperty("startupCommand", out var sc) && sc.ValueKind == JsonValueKind.String ? sc.GetString() : null;
                        string? wsl = root.TryGetProperty("wslDistribution", out var w) && w.ValueKind == JsonValueKind.String ? w.GetString() : null;
                        try
                        {
                            bind(id);
                            _backend.Create(id, Num("cols", 80), Num("rows", 24), cwd, shell, cmd, wsl);
                            await send(GatewayProtocol.AttachedReply(id)).ConfigureAwait(false);
                        }
                        catch (Exception ex)
                        {
                            unbind(id);
                            await send(GatewayProtocol.ErrorMessage(id, ex.Message)).ConfigureAwait(false);
                        }
                        break;
                    }
                case GatewayProtocol.Write:
                case GatewayProtocol.DataIn:
                    if (Id() is { } writeId &&
                        root.TryGetProperty("data", out var data) && data.ValueKind == JsonValueKind.String)
                    {
                        var target = writeId;
                        SafeBackend(() => _backend.Write(target, data.GetString() ?? ""),
                            errId => send(GatewayProtocol.ErrorMessage(errId, "Запись не удалась.")));
                    }
                    break;
                case GatewayProtocol.Resize:
                    {
                        var target = Id() ?? defaultId();
                        if (target is not null)
                        {
                            var cols = Num("cols", 80);
                            var rows = Num("rows", 24);
                            SafeBackend(() => _backend.Resize(target, cols, rows),
                                errId => send(GatewayProtocol.ErrorMessage(errId, "Resize не удался.")));
                        }
                        break;
                    }
                case GatewayProtocol.Kill:
                    if (Id() is { } killId)
                        SafeBackend(() => _backend.Kill(killId),
                            errId => send(GatewayProtocol.ErrorMessage(errId, "Завершение не удалось.")));
                    break;
            }
        }
        catch (JsonException) { }
    }

    private string[] SafeLiveIds()
    {
        try { return _backend.LiveIds(); }
        catch { return []; }
    }

    private bool IsLive(string id)
    {
        try { return ((IList<string>)_backend.LiveIds()).Contains(id); }
        catch { return false; }
    }

    private static void SafeBackend(Action action, Func<string?, Task> onError)
    {
        try { action(); }
        catch { _ = onError(null); }
    }

    private static async Task<(bool binary, byte[] bytes)?> ReceiveAsync(WebSocket ws, CancellationToken token)
    {
        var buffer = new byte[65536];
        using var ms = new MemoryStream();
        bool binary = false;
        while (true)
        {
            WebSocketReceiveResult result;
            try
            {
                result = await ws.ReceiveAsync(buffer, token).ConfigureAwait(false);
            }
            catch (WebSocketException) { return null; }
            if (result.MessageType == WebSocketMessageType.Close)
                return null;
            if (result.MessageType == WebSocketMessageType.Binary)
                binary = true;
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
                return (binary, ms.ToArray());
        }
    }

    private static async Task CloseAsync(WebSocket ws)
    {
        try
        {
            if (ws.State == WebSocketState.Open)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
        try { ws.Dispose(); } catch { }
    }
}
