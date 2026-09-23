using System.IO;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace TerminalV.Ssh;

/// <summary>
/// Control channel to a TerminalV mirror gateway (ws://pc:5454).
/// Lists live desktop sessions and creates/kills remote shells without
/// attaching a terminal. Session traffic itself goes through
/// <see cref="GatewaySshSession"/> (one WebSocket per mirrored tab).
/// </summary>
public sealed class GatewayControlClient : IDisposable
{
    private static readonly JsonSerializerOptions JsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    private readonly string _url;
    private readonly string? _token;
    private readonly TimeSpan _timeout;
    private ClientWebSocket? _ws;
    private int _disposed;

    public GatewayControlClient(string gatewayUrl, string? gatewayToken = null, TimeSpan? timeout = null)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            throw new ArgumentException("GatewayUrl required.", nameof(gatewayUrl));
        _url = Normalize(gatewayUrl);
        _token = gatewayToken;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        var ws = await EnsureAsync(cts.Token).ConfigureAwait(false);
        await SendAsync(ws, GatewayProtocol.ListRequest(), cts.Token).ConfigureAwait(false);
        while (true)
        {
            var text = await ReceiveTextAsync(ws, cts.Token).ConfigureAwait(false);
            if (!GatewayProtocol.TryGetType(text, out var type))
                continue;
            if (type == GatewayProtocol.Sessions)
            {
                try
                {
                    using var doc = JsonDocument.Parse(text);
                    if (doc.RootElement.TryGetProperty("ids", out var ids) &&
                        ids.ValueKind == JsonValueKind.Array)
                    {
                        return ids.EnumerateArray()
                            .Select(e => e.GetString() ?? "")
                            .Where(s => s.Length > 0)
                            .ToList()
                            .AsReadOnly();
                    }
                    return Array.Empty<string>();
                }
                catch (JsonException)
                {
                    return Array.Empty<string>();
                }
            }
            if (type == GatewayProtocol.Error)
                throw new InvalidOperationException(ReadMessage(text) ?? "Gateway error.");
        }
    }

    public async Task<string> CreateAsync(int cols = 80, int rows = 24, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        var ws = await EnsureAsync(cts.Token).ConfigureAwait(false);
        await SendAsync(ws, GatewayProtocol.CreateRequest(null, cols, rows), cts.Token).ConfigureAwait(false);
        while (true)
        {
            var text = await ReceiveTextAsync(ws, cts.Token).ConfigureAwait(false);
            if (!GatewayProtocol.TryGetType(text, out var type))
                continue;
            if (type == GatewayProtocol.Attached || type == GatewayProtocol.Created)
            {
                using var doc = JsonDocument.Parse(text);
                if (doc.RootElement.TryGetProperty("id", out var idEl) &&
                    idEl.GetString() is { Length: > 0 } id)
                    return id;
                throw new InvalidOperationException("Gateway did not report a session id.");
            }
            if (type == GatewayProtocol.Error)
                throw new InvalidOperationException(ReadMessage(text) ?? "Gateway error.");
        }
    }

    public async Task KillAsync(string id, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id required.", nameof(id));
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(_timeout);
        var ws = await EnsureAsync(cts.Token).ConfigureAwait(false);
        await SendAsync(ws, GatewayProtocol.KillRequest(id), cts.Token).ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
        {
            try { _ws?.Dispose(); } catch { }
            _ws = null;
        }
    }

    private async Task<ClientWebSocket> EnsureAsync(CancellationToken ct)
    {
        if (_ws is { State: WebSocketState.Open })
            return _ws;
        _ws?.Dispose();
        var ws = new ClientWebSocket();
        if (!string.IsNullOrWhiteSpace(_token))
            ws.Options.SetRequestHeader("Authorization", "Bearer " + _token);
        await ws.ConnectAsync(new Uri(_url), ct).ConfigureAwait(false);
        var handshake = GatewayProtocol.ConnectHandshake(null, control: true, cols: 80, rows: 24, term: null);
        await SendAsync(ws, handshake, ct).ConfigureAwait(false);
        _ws = ws;
        return ws;
    }

    private static async Task SendAsync(ClientWebSocket ws, string text, CancellationToken ct)
    {
        var bytes = Encoding.UTF8.GetBytes(text);
        await ws.SendAsync(bytes, WebSocketMessageType.Text, true, ct).ConfigureAwait(false);
    }

    private static async Task<string> ReceiveTextAsync(ClientWebSocket ws, CancellationToken ct)
    {
        var buffer = new byte[65536];
        using var ms = new MemoryStream();
        while (true)
        {
            var result = await ws.ReceiveAsync(buffer, ct).ConfigureAwait(false);
            if (result.MessageType == WebSocketMessageType.Close)
                throw new InvalidOperationException("Gateway closed the connection.");
            ms.Write(buffer, 0, result.Count);
            if (result.EndOfMessage)
            {
                if (result.MessageType != WebSocketMessageType.Text)
                    continue;
                return Encoding.UTF8.GetString(ms.ToArray());
            }
        }
    }

    private static string? ReadMessage(string text)
    {
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (doc.RootElement.TryGetProperty("message", out var m))
                return m.GetString();
            return null;
        }
        catch (JsonException) { return null; }
    }

    internal static string Normalize(string gatewayUrl)
    {
        var baseUrl = gatewayUrl.Trim();
        if (baseUrl.StartsWith("http://", StringComparison.OrdinalIgnoreCase))
            baseUrl = "ws://" + baseUrl[7..];
        else if (baseUrl.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            baseUrl = "wss://" + baseUrl[8..];
        return baseUrl.TrimEnd('/');
    }
}
