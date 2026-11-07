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
    private readonly ITailnetAccess? _tailnet;
    private readonly Func<GatewaySessionInfo[]>? _sessionCatalog;
    private readonly string? _uploadDirectory;
    private readonly long _maxUploadBytes;
    private readonly bool _loopbackOnly;

    public GatewayServer(IGatewaySessionBackend backend, int port, string? token = null, ITailnetAccess? tailnet = null,
        Func<GatewaySessionInfo[]>? sessionCatalog = null, string? uploadDirectory = null,
        long maxUploadBytes = GatewayUploadStore.MaxUploadBytes, bool loopbackOnly = false)
    {
        _backend = backend ?? throw new ArgumentNullException(nameof(backend));
        if (port is < 1 or > 65535)
            throw new ArgumentOutOfRangeException(nameof(port));
        if (maxUploadBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxUploadBytes));
        _port = port;
        _token = string.IsNullOrWhiteSpace(token) ? null : token;
        _tailnet = tailnet;
        _sessionCatalog = sessionCatalog;
        _uploadDirectory = uploadDirectory;
        _maxUploadBytes = maxUploadBytes;
        _loopbackOnly = loopbackOnly;
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
        listener.Prefixes.Add($"http://{(_loopbackOnly ? "localhost" : "*")}:{_port}/");
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
        TailnetIdentity? identity = null;
        if (context.Request.Headers["Authorization"] == "Tailscale" && _tailnet is not null)
            identity = await _tailnet.IdentifyAsync(context.Request.RemoteEndPoint, context.Request.LocalEndPoint, token);
        var path = context.Request.Url?.AbsolutePath;
        if (path is "/terminalv/info" or "/terminalv/pair")
        {
            if (identity is null) { context.Response.StatusCode = 401; context.Response.Close(); return; }
            var approved = _tailnet!.IsApproved(identity);
            if (path == "/terminalv/pair")
            {
                if (context.Request.HttpMethod != "POST") { context.Response.StatusCode = 405; context.Response.Close(); return; }
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                timeout.CancelAfter(TimeSpan.FromSeconds(60));
                try { approved = await _tailnet.ApproveAsync(identity, timeout.Token); }
                catch { approved = false; }
                if (!approved) { context.Response.StatusCode = 403; context.Response.Close(); return; }
            }
            context.Response.ContentType = "application/json";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(new { protocol = "terminalv-tailnet-v1", name = Environment.MachineName, approved }, Json);
            context.Response.ContentLength64 = bytes.Length;
            try { await context.Response.OutputStream.WriteAsync(bytes, token); } finally { context.Response.Close(); }
            return;
        }
        if (path == "/terminalv/upload")
        {
            await HandleUploadAsync(context, identity, token).ConfigureAwait(false);
            return;
        }
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

        if (!(identity is not null && _tailnet!.IsApproved(identity)) && !Authorized(context.Request))
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

        // Revocation also ends an already-open identity-authenticated channel.
        using var connection = CancellationTokenSource.CreateLinkedTokenSource(token);
        using var watcher = identity is null ? null : new Timer(_ =>
        {
            try { if (!_tailnet!.IsApproved(identity)) { connection.Cancel(); wsContext.WebSocket.Abort(); } }
            catch (ObjectDisposedException) { }
        }, null, 1000, 1000);
        await ServeAsync(wsContext.WebSocket, connection.Token).ConfigureAwait(false);
    }

    private bool Authorized(HttpListenerRequest request)
    {
        if (request.Headers["Authorization"] == "Tailscale") return false;
        if (_token is null)
            return true;
        var header = request.Headers["Authorization"];
        if (header == "Bearer " + _token)
            return true;
        return request.QueryString["token"] == _token;
    }

    private async Task HandleUploadAsync(HttpListenerContext context, TailnetIdentity? identity, CancellationToken token)
    {
        if (!(identity is not null && _tailnet!.IsApproved(identity)) && !Authorized(context.Request))
        {
            context.Response.StatusCode = 401;
            try { context.Response.Close(); } catch { }
            return;
        }
        if (!string.Equals(context.Request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            context.Response.StatusCode = 405;
            try { context.Response.Close(); } catch { }
            return;
        }
        // Require a custom header so a cross-origin browser <form> cannot POST
        // files to a tokenless LAN gateway without a preflight.
        if (context.Request.Headers["X-TerminalV-Upload"] != "1")
        {
            context.Response.StatusCode = 400;
            await WriteJsonResponseAsync(context.Response, new { error = "Missing X-TerminalV-Upload header." }, token).ConfigureAwait(false);
            return;
        }
        if (context.Request.ContentLength64 > _maxUploadBytes)
        {
            context.Response.StatusCode = 413;
            await WriteJsonResponseAsync(context.Response, new { error = "Файл слишком большой." }, token).ConfigureAwait(false);
            return;
        }

        var fileName = ReadQueryParameter(context.Request.Url, "name");
        try
        {
            var savedPath = await GatewayUploadStore.SaveStreamAsync(
                context.Request.InputStream, fileName, _uploadDirectory, _maxUploadBytes, token).ConfigureAwait(false);
            context.Response.StatusCode = 200;
            await WriteJsonResponseAsync(context.Response, new { path = savedPath }, token).ConfigureAwait(false);
        }
        catch (GatewayUploadTooLargeException ex)
        {
            context.Response.StatusCode = 413;
            await WriteJsonResponseAsync(context.Response, new { error = ex.Message }, token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { context.Response.Close(); } catch { }
        }
        catch (Exception ex)
        {
            context.Response.StatusCode = 500;
            await WriteJsonResponseAsync(context.Response, new { error = ex.Message }, token).ConfigureAwait(false);
        }
    }

    private static string? ReadQueryParameter(Uri? url, string key)
    {
        var query = url?.Query;
        if (string.IsNullOrEmpty(query))
            return null;
        var trimmed = query.TrimStart('?');
        foreach (var pair in trimmed.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var eq = pair.IndexOf('=');
            var rawKey = eq >= 0 ? pair[..eq] : pair;
            if (!string.Equals(Uri.UnescapeDataString(rawKey.Replace('+', ' ')), key, StringComparison.Ordinal))
                continue;
            var rawValue = eq >= 0 ? pair[(eq + 1)..] : "";
            return Uri.UnescapeDataString(rawValue.Replace('+', ' '));
        }
        return null;
    }

    private static async Task WriteJsonResponseAsync(HttpListenerResponse response, object payload, CancellationToken token)
    {
        try
        {
            response.ContentType = "application/json";
            var bytes = JsonSerializer.SerializeToUtf8Bytes(payload, Json);
            response.ContentLength64 = bytes.Length;
            await response.OutputStream.WriteAsync(bytes, token).ConfigureAwait(false);
        }
        catch { }
        finally
        {
            try { response.Close(); } catch { }
        }
    }

    private async Task ServeAsync(WebSocket ws, CancellationToken serverToken)
    {
        var sendGate = new SemaphoreSlim(1, 1);
        var bound = new HashSet<string>();
        var boundGate = new object();
        var clientOwner = new object();
        string? defaultId = null;
        var geometryBackend = _backend as IGatewayGeometryBackend;
        var terminalGeometry = false;
        var binaryData = false;

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

        async Task SendBinaryAsync(string data)
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            await sendGate.WaitAsync(serverToken).ConfigureAwait(false);
            try
            {
                if (ws.State == WebSocketState.Open)
                    await ws.SendAsync(bytes, WebSocketMessageType.Binary, true, serverToken).ConfigureAwait(false);
            }
            catch { }
            finally { sendGate.Release(); }
        }

        void OnData(string id, string data)
        {
            if (!IsBound(id)) return;
            if (binaryData) _ = SendBinaryAsync(data);
            else _ = SendAsync(GatewayProtocol.DataMessage(id, data));
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

        void OnGeometry(string id, int cols, int rows)
        {
            if (terminalGeometry && IsBound(id)) _ = SendAsync(GatewayProtocol.GeometryMessage(id, cols, rows));
        }

        _backend.Data += OnData;
        _backend.Exited += OnExit;
        _backend.DirectoryChanged += OnCwd;
        _backend.Error += OnError;
        if (geometryBackend is not null) geometryBackend.GeometryChanged += OnGeometry;

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
            // Older APKs render unknown control frames as terminal text.
            terminalGeometry = handshake.TerminalGeometry;
            binaryData = handshake.BinaryData;

            if (!handshake.Control)
            {
                if (!string.IsNullOrWhiteSpace(handshake.SessionId))
                {
                    var id = handshake.SessionId;
                    if (IsLive(id))
                    {
                        try
                        {
                            // History can be large. Sending it must not block reading
                            // keystrokes; the send gate still keeps history ahead of "attached".
                            var history = await AttachBackendAsync(id, Bind, SendAsync, SendBinaryAsync, serverToken, terminalGeometry, binaryData).ConfigureAwait(false);
                            _ = Task.WhenAll(history, SendAsync(GatewayProtocol.AttachedReply(id)));
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
                    () => defaultId, SendAsync, serverToken, terminalGeometry, clientOwner, binaryData, SendBinaryAsync).ConfigureAwait(false);
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
            if (geometryBackend is not null)
            {
                geometryBackend.GeometryChanged -= OnGeometry;
                string[] boundIds;
                lock (boundGate) boundIds = [.. bound];
                foreach (var id in boundIds)
                {
                    try { geometryBackend.ReleaseClientGeometry(id, clientOwner); } catch { }
                }
            }
            sendGate.Dispose();
            await CloseAsync(ws).ConfigureAwait(false);
        }
    }

    private async Task DispatchAsync(string text, Action<string> bind,
        Action<string> unbind, Func<string?> defaultId, Func<string, Task> send, CancellationToken ct, bool terminalGeometry, object clientOwner, bool binaryData = false, Func<string, Task>? sendBinary = null)
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
                    try { await send(SessionListReply()).ConfigureAwait(false); }
                    catch (Exception ex) { await send(GatewayProtocol.ErrorMessage(null, ex.Message)).ConfigureAwait(false); }
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
                                var history = binaryData && sendBinary != null
                                    ? await AttachBackendAsync(attachId, bind, send, sendBinary, ct, terminalGeometry, true).ConfigureAwait(false)
                                    : await AttachBackendAsync(attachId, bind, send, null, ct, terminalGeometry, false).ConfigureAwait(false);
                                _ = Task.WhenAll(history, send(GatewayProtocol.AttachedReply(attachId)));
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
                    if (Id() is { } detachId)
                    {
                        unbind(detachId);
                        if (_backend is IGatewayGeometryBackend detachGeo)
                            SafeBackend(() => detachGeo.ReleaseClientGeometry(detachId, clientOwner), _ => Task.CompletedTask);
                    }
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
                            SafeBackend(() =>
                            {
                                if (_backend is IGatewayGeometryBackend resizeGeo)
                                    resizeGeo.ResizeClient(target, clientOwner, cols, rows);
                                else
                                    _backend.Resize(target, cols, rows);
                            }, errId => send(GatewayProtocol.ErrorMessage(errId, "Resize не удался.")));
                        }
                        break;
                    }
                case GatewayProtocol.ReleaseGeometry:
                    {
                        if (_backend is IGatewayGeometryBackend releaseGeo)
                        {
                            var target = Id() ?? defaultId();
                            if (target is not null)
                                SafeBackend(() => releaseGeo.ReleaseClientGeometry(target, clientOwner), _ => Task.CompletedTask);
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

    private async Task<Task> AttachBackendAsync(string id, Action<string> bind, Func<string, Task> send, Func<string, Task>? sendBinary, CancellationToken ct, bool terminalGeometry, bool binaryData)
    {
        var geometrySent = Task.CompletedTask;
        void BeforeReplay()
        {
            if (terminalGeometry && _backend is IGatewayGeometryBackend geometryBackend)
            {
                var size = geometryBackend.EnsureGeometry(id);
                geometrySent = send(GatewayProtocol.GeometryMessage(id, size.Columns, size.Rows));
            }
        }
        if (_backend is IGatewayReplayBackend replayBackend)
        {
            var replaySent = Task.CompletedTask;
            await replayBackend.ReplayAndBindAsync(id,
                data => replaySent = binaryData && sendBinary != null ? sendBinary(data) : send(GatewayProtocol.DataMessage(id, data)),
                () => bind(id), ct, BeforeReplay).ConfigureAwait(false);
            return Task.WhenAll(geometrySent, replaySent);
        }
        // Independent backends may synchronously emit history from Attach.
        BeforeReplay();
        bind(id);
        _backend.Attach(id);
        return geometrySent;
    }

    // Legacy overload for callers not using binary data.
    private Task<Task> AttachBackendAsync(string id, Action<string> bind, Func<string, Task> send, CancellationToken ct, bool terminalGeometry)
        => AttachBackendAsync(id, bind, send, null, ct, terminalGeometry, false);

    private string SessionListReply()
    {
        var live = _backend.LiveIds();
        if (_sessionCatalog is null) return GatewayProtocol.SessionsReply(live);
        var liveSet = new HashSet<string>(live);
        var records = _sessionCatalog().OrderBy(s => s.SortOrder).Select(s => new GatewaySessionInfo
        {
            Id = s.Id, Title = s.Title, CustomTitle = s.CustomTitle,
            Hidden = s.Hidden, SortOrder = s.SortOrder, Active = s.Active,
            Live = liveSet.Contains(s.Id)
        }).ToArray();
        return GatewayProtocol.SessionsReply(records.Where(s => s.Live && !s.Hidden).Select(s => s.Id), records);
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
            if (ws.State is WebSocketState.Open or WebSocketState.CloseReceived)
                await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
            else if (ws.State == WebSocketState.CloseSent)
                await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "bye", CancellationToken.None).ConfigureAwait(false);
        }
        catch { }
        try { ws.Dispose(); } catch { }
    }
}
