using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TerminalV.Ssh;

/// <summary>
/// TerminalV gateway WebSocket client. Connects to gateway which proxies SSH.
/// Handles host/port/user/auth, shell type xterm-256color, cols/rows, reconnect via WebSocket.
/// Protocol: gateway ws endpoint accepts query params or JSON handshake + binary/text frames.
/// </summary>
public sealed class GatewaySshSession : SshSessionBase
{
    private ClientWebSocket? _ws;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    public GatewaySshSession(string id, SshConnectionOptions options) : base(id, options)
    {
        if (!options.UseGateway) throw new ArgumentException("GatewayUrl required for GatewaySshSession.", nameof(options));
    }

    /// <summary>
    /// Desktop session id reported by the mirror gateway
    /// ({type:"attached"/"created"}). Null until the server replies.
    /// </summary>
    public string? AttachedSessionId { get; private set; }

    public event Action<string>? Attached;

    public override async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(GatewaySshSession));
        if (State == SshSessionState.Connected) return;
        State = SshSessionState.Connecting;
        _options.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
            var ws = new ClientWebSocket();
            if (!string.IsNullOrWhiteSpace(_options.GatewayToken))
                ws.Options.SetRequestHeader("Authorization", "Bearer " + _options.GatewayToken);
            // Subprotocol? Use terminalv.ssh
            // Build URL: gatewayUrl + ?host=&port=&user=&cols=&rows=&term=
            var uri = BuildUri(_options);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.ConnectTimeout);
            await ws.ConnectAsync(uri, timeoutCts.Token).ConfigureAwait(false);
            // Handshake: send auth + terminal info as JSON text frame.
            // Mirror mode adds sessionId so the gateway attaches a live
            // desktop session instead of proxying SSH (see GatewayProtocol).
            var handshake = new
            {
                type = "connect",
                host = _options.Host,
                port = _options.Port,
                username = _options.Username,
                password = _options.Password,
                privateKey = _options.PrivateKeyContent,
                keyPassphrase = _options.PrivateKeyPassphrase,
                sessionId = _options.MirrorSessionId,
                term = TerminalType,
                cols = Columns,
                rows = Rows
            };
            var json = JsonSerializer.Serialize(handshake, JsonOpts);
            var bytes = Encoding.UTF8.GetBytes(json);
            await ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
            _ws = ws;
            State = SshSessionState.Connected;
            _readCts = new CancellationTokenSource();
            _readTask = Task.Factory.StartNew(() => ReadLoop(_readCts.Token), TaskCreationOptions.LongRunning).Unwrap();
        }
        catch (Exception ex)
        {
            State = SshSessionState.Faulted;
            RaiseError(ex.Message);
            await DisconnectCoreAsync().ConfigureAwait(false);
            throw;
        }
        finally { _gate.Release(); }
    }

    public override async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); State = SshSessionState.Disconnected; }
        finally { _gate.Release(); }
    }

    public override async Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        if (IsDisposed) return;
        if (State != SshSessionState.Connected || _ws is null) throw new InvalidOperationException("Gateway not connected.");
        if (string.IsNullOrEmpty(data)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Send as binary (vt data) or JSON {type:"data", data:"..."}
            // Use binary for efficiency: raw UTF8 bytes
            var bytes = Encoding.UTF8.GetBytes(data);
            // Frame as binary
            await _ws.SendAsync(bytes, WebSocketMessageType.Binary, true, cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    protected override async Task OnResizeAsync(int cols, int rows, CancellationToken cancellationToken)
    {
        if (State != SshSessionState.Connected || _ws is null) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var msg = JsonSerializer.Serialize(new { type = "resize", cols, rows, term = TerminalType }, JsonOpts);
            var bytes = Encoding.UTF8.GetBytes(msg);
            await _ws.SendAsync(bytes, WebSocketMessageType.Text, true, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) { RaiseError(ex.Message); }
        finally { _gate.Release(); }
    }

    protected override void DisposeCore()
    {
        try { _readCts?.Cancel(); } catch { }
        try { _readTask?.Wait(500); } catch { }
        try { DisconnectCoreAsync().GetAwaiter().GetResult(); } catch { }
        _gate.Dispose();
        _readCts?.Dispose();
        base.DisposeCore();
    }

    private async Task DisconnectCoreAsync()
    {
        try { _readCts?.Cancel(); } catch { }
        var ws = _ws; _ws = null;
        if (ws is not null)
        {
            try
            {
                if (ws.State == WebSocketState.Open || ws.State == WebSocketState.CloseReceived)
                    await ws.CloseAsync(WebSocketCloseStatus.NormalClosure, "disconnect", CancellationToken.None).ConfigureAwait(false);
            } catch { }
            ws.Dispose();
        }
    }

    private async Task ReadLoop(CancellationToken token)
    {
        var buffer = new byte[8192];
        var sb = new MemoryStream();
        while (!token.IsCancellationRequested && State == SshSessionState.Connected)
        {
            try
            {
                var ws = _ws;
                if (ws is null) break;
                var result = await ws.ReceiveAsync(buffer, token).ConfigureAwait(false);
                if (result.MessageType == WebSocketMessageType.Close)
                {
                    RaiseClosed(null);
                    if (_options.AutoReconnect) _ = Task.Run(async () => { try { await ReconnectAsync().ConfigureAwait(false); } catch { } });
                    break;
                }
                sb.Write(buffer, 0, result.Count);
                if (!result.EndOfMessage) continue;
                var payload = sb.ToArray();
                sb.SetLength(0);
                if (result.MessageType == WebSocketMessageType.Text)
                {
                    var text = Encoding.UTF8.GetString(payload);
                    // Mirror-gateway bookkeeping (attached/sessions/cwd) must
                    // never leak into the terminal as raw text.
                    if (HandleControlMessage(text))
                        continue;
                    // Gateway may send JSON {type:"data", data:"..."} or {type:"error"}
                    if (TryParseGatewayMessage(text, out var data, out var err, out var code))
                    {
                        if (data is not null) RaiseData(data);
                        if (err is not null) RaiseError(err);
                        if (code.HasValue) { State = SshSessionState.Disconnected; RaiseClosed(code); break; }
                    }
                    else
                    {
                        // raw text
                        RaiseData(text);
                    }
                }
                else if (result.MessageType == WebSocketMessageType.Binary)
                {
                    var text = Encoding.UTF8.GetString(payload);
                    RaiseData(text);
                }
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    RaiseError(ex.Message);
                    State = SshSessionState.Faulted;
                    RaiseClosed(null);
                    if (_options.AutoReconnect) _ = Task.Run(async () => { try { await ReconnectAsync().ConfigureAwait(false); } catch { } });
                }
                break;
            }
        }
        if (State == SshSessionState.Connected) { State = SshSessionState.Disconnected; RaiseClosed(null); }
    }

    /// <summary>
    /// Consumes mirror-gateway control replies. Returns true when the frame
    /// was bookkeeping and must not reach the terminal.
    /// </summary>
    private bool HandleControlMessage(string text)
    {
        if (!GatewayProtocol.TryGetType(text, out var type) || !GatewayProtocol.IsControlReply(type))
            return false;
        if ((type == GatewayProtocol.Attached || type == GatewayProtocol.Created))
        {
            try
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("id", out var idEl) &&
                    idEl.GetString() is { Length: > 0 } id)
                {
                    AttachedSessionId = id;
                    Attached?.Invoke(id);
                }
            }
            catch (JsonException) { }
        }
        return true;
    }

    private static bool TryParseGatewayMessage(string json, out string? data, out string? error, out int? exitCode)
    {
        data = null; error = null; exitCode = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (!root.TryGetProperty("type", out var t)) return false;
            var type = t.GetString();
            if (type == "data" && root.TryGetProperty("data", out var d)) data = d.GetString();
            else if (type == "error" && root.TryGetProperty("message", out var m)) error = m.GetString();
            else if (type == "exit" && root.TryGetProperty("code", out var c)) exitCode = c.GetInt32();
            else if (type == "binary") return false;
            return data is not null || error is not null || exitCode.HasValue;
        } catch { return false; }
    }

    private static Uri BuildUri(SshConnectionOptions o)
    {
        var baseUrl = o.GatewayUrl!;
        // Support http(s) -> ws(s) conversion
        if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            baseUrl = "ws://" + baseUrl[7..];
        else if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            baseUrl = "wss://" + baseUrl[8..];
        var sep = baseUrl.Contains('?') ? "&" : "?";
        var ub = $"{baseUrl}{sep}host={Uri.EscapeDataString(o.Host)}&port={o.Port}&user={Uri.EscapeDataString(o.Username)}&cols={Math.Clamp(o.Columns,1,1000)}&rows={Math.Clamp(o.Rows,1,1000)}&term={Uri.EscapeDataString(string.IsNullOrWhiteSpace(o.TerminalType) ? "xterm-256color" : o.TerminalType)}";
        if (!string.IsNullOrWhiteSpace(o.MirrorSessionId))
            ub += $"&sessionId={Uri.EscapeDataString(o.MirrorSessionId)}";
        return new Uri(ub);
    }
}
