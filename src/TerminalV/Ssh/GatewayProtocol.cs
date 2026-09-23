using System.Text.Json;

namespace TerminalV.Ssh;

/// <summary>
/// Mirror-gateway wire protocol between the TerminalV desktop server
/// (ws://pc:5454) and remote clients (TerminalV.Mobile).
///
/// JSON text frames, camelCase. Binary client-&gt;server frames are raw
/// terminal input for the connection's default session.
/// A legacy SSH-proxy handshake ({type:"connect",host,port,username,...})
/// is accepted and treated as "create a new local shell".
/// </summary>
public static class GatewayProtocol
{
    public const int DefaultPort = 5454;
    public const string DefaultTerm = "xterm-256color";

    // Client -> server
    public const string Connect = "connect";
    public const string List = "list";
    public const string Attach = "attach";
    public const string Detach = "detach";
    public const string Create = "create";
    public const string Write = "write";
    public const string DataIn = "data";
    public const string Resize = "resize";
    public const string Kill = "kill";

    // Server -> client
    public const string Attached = "attached";
    public const string Created = "created";
    public const string Sessions = "sessions";
    public const string Data = "data";
    public const string Exit = "exit";
    public const string Error = "error";
    public const string Cwd = "cwd";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    /// <summary>
    /// Replies the client must never print into the terminal
    /// (session bookkeeping instead of terminal output).
    /// </summary>
    public static bool IsControlReply(string? type) =>
        type is Attached or Created or Sessions or Cwd;

    public static string ConnectHandshake(string? sessionId, bool control, int cols, int rows, string? term) =>
        JsonSerializer.Serialize(new
        {
            type = Connect,
            sessionId,
            control = control ? true : (bool?)null,
            cols = Math.Clamp(cols, 1, 1000),
            rows = Math.Clamp(rows, 1, 1000),
            term = string.IsNullOrWhiteSpace(term) ? DefaultTerm : term
        }, Json);

    public static string ListRequest() => """{"type":"list"}""";
    public static string AttachRequest(string id) =>
        JsonSerializer.Serialize(new { type = Attach, id }, Json);
    public static string DetachRequest(string id) =>
        JsonSerializer.Serialize(new { type = Detach, id }, Json);

    public static string CreateRequest(string? id, int cols, int rows, string? cwd = null,
        string? shell = null, string? startupCommand = null, string? wslDistribution = null) =>
        JsonSerializer.Serialize(new
        {
            type = Create,
            id,
            cols = Math.Clamp(cols, 1, 1000),
            rows = Math.Clamp(rows, 1, 1000),
            cwd,
            shell,
            startupCommand,
            wslDistribution
        }, Json);

    public static string WriteRequest(string id, string data) =>
        JsonSerializer.Serialize(new { type = Write, id, data }, Json);

    public static string ResizeRequest(string? id, int cols, int rows) =>
        JsonSerializer.Serialize(new
        {
            type = Resize,
            id,
            cols = Math.Clamp(cols, 1, 1000),
            rows = Math.Clamp(rows, 1, 1000)
        }, Json);

    public static string KillRequest(string id) =>
        JsonSerializer.Serialize(new { type = Kill, id }, Json);

    public static string AttachedReply(string id) =>
        JsonSerializer.Serialize(new { type = Attached, id }, Json);

    public static string SessionsReply(IEnumerable<string> ids) =>
        JsonSerializer.Serialize(new { type = Sessions, ids = ids.ToArray() }, Json);

    public static string DataMessage(string id, string data) =>
        JsonSerializer.Serialize(new { type = Data, id, data }, Json);

    public static string ErrorMessage(string? id, string message) =>
        JsonSerializer.Serialize(new { type = Error, id, message }, Json);

    public static string ExitMessage(string id, uint code) =>
        JsonSerializer.Serialize(new { type = Exit, id, code }, Json);

    public static bool TryGetType(string json, out string? type)
    {
        type = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                return false;
            if (!doc.RootElement.TryGetProperty("type", out var t))
                return false;
            type = t.GetString();
            return type is not null;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    public sealed class Handshake
    {
        public string? SessionId { get; set; }
        public bool Control { get; set; }
        public int Cols { get; set; } = 80;
        public int Rows { get; set; } = 24;
        public string Term { get; set; } = DefaultTerm;
    }

    /// <summary>
    /// Parses the first client frame. Unknown extra fields (legacy
    /// host/port/username/password/...) are ignored.
    /// </summary>
    public static bool TryParseHandshake(string json, out Handshake? handshake)
    {
        handshake = null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
                return false;
            if (!root.TryGetProperty("type", out var t) || t.GetString() != Connect)
                return false;
            var h = new Handshake();
            if (root.TryGetProperty("sessionId", out var s) && s.ValueKind == JsonValueKind.String)
                h.SessionId = s.GetString();
            if (root.TryGetProperty("control", out var c) && c.ValueKind == JsonValueKind.True)
                h.Control = true;
            if (root.TryGetProperty("cols", out var cols) && cols.TryGetInt32(out var ci))
                h.Cols = Math.Clamp(ci, 1, 1000);
            if (root.TryGetProperty("rows", out var rows) && rows.TryGetInt32(out var ri))
                h.Rows = Math.Clamp(ri, 1, 1000);
            if (root.TryGetProperty("term", out var term) && term.ValueKind == JsonValueKind.String
                && !string.IsNullOrWhiteSpace(term.GetString()))
                h.Term = term.GetString()!;
            handshake = h;
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
