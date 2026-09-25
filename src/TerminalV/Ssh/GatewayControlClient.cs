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
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };
    private readonly string _url;
    private readonly string? _token;
    private readonly TimeSpan _timeout;
    private ClientWebSocket? _ws;
    private int _disposed;
    private readonly bool _tailnetIdentity;
    private readonly System.Net.Http.HttpMessageInvoker? _transport;

    public GatewayControlClient(string gatewayUrl, string? gatewayToken = null, TimeSpan? timeout = null,
        bool tailnetIdentity = false, Func<string, int, CancellationToken, Task<Stream>>? dial = null)
    {
        if (string.IsNullOrWhiteSpace(gatewayUrl))
            throw new ArgumentException("GatewayUrl required.", nameof(gatewayUrl));
        _url = Normalize(gatewayUrl);
        _token = gatewayToken;
        _timeout = timeout ?? TimeSpan.FromSeconds(15);
        _tailnetIdentity = tailnetIdentity;
        _transport = GatewayTransport.CreateHandler(dial);
    }

    public async Task<IReadOnlyList<string>> ListAsync(CancellationToken cancellationToken = default) =>
        (await ListCatalogAsync(cancellationToken).ConfigureAwait(false)).Ids;

    public async Task<GatewaySessionCatalog> ListCatalogAsync(CancellationToken cancellationToken = default)
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
                    var root = doc.RootElement;
                    var ids = root.TryGetProperty("ids", out var idsElement) && idsElement.ValueKind == JsonValueKind.Array
                        ? idsElement.EnumerateArray()
                            .Where(e => e.ValueKind == JsonValueKind.String)
                            .Select(e => e.GetString()!)
                            .Where(s => !string.IsNullOrWhiteSpace(s))
                            .ToArray()
                        : Array.Empty<string>();
                    GatewaySessionInfo[]? sessions = null;
                    if (root.TryGetProperty("sessions", out var catalog) && catalog.ValueKind != JsonValueKind.Null)
                    {
                        sessions = catalog.Deserialize<GatewaySessionInfo[]>(JsonOpts)
                            ?? throw new JsonException("Missing desktop session catalog.");
                        if (sessions.Any(s => s is null || string.IsNullOrWhiteSpace(s.Id)) ||
                            sessions.Select(s => s.Id).Distinct(StringComparer.Ordinal).Count() != sessions.Length)
                            throw new JsonException("Invalid desktop session catalog.");
                    }
                    return new GatewaySessionCatalog(ids, sessions);
                }
                catch (JsonException ex)
                {
                    // Do not turn an invalid reply into an authoritative empty list.
                    throw new InvalidOperationException("Invalid gateway session list.", ex);
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
            _transport?.Dispose();
        }
    }

    private async Task<ClientWebSocket> EnsureAsync(CancellationToken ct)
    {
        if (_ws is { State: WebSocketState.Open })
            return _ws;
        _ws?.Dispose();
        var ws = new ClientWebSocket();
        if (_tailnetIdentity) ws.Options.SetRequestHeader("Authorization", "Tailscale");
        else if (!string.IsNullOrWhiteSpace(_token))
            ws.Options.SetRequestHeader("Authorization", "Bearer " + _token);
        await GatewayTransport.ConnectAsync(ws, new Uri(_url), _transport, ct).ConfigureAwait(false);
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
