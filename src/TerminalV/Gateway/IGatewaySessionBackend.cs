namespace TerminalV.Gateway;

/// <summary>
/// Live desktop sessions behind the mirror gateway.
/// Production implementation adapts <see cref="Host.SessionClient"/>
/// (named-pipe SessionHost); tests use a fake.
/// </summary>
internal interface IGatewaySessionBackend
{
    event Action<string, string>? Data;
    event Action<string, uint>? Exited;
    event Action<string, string>? DirectoryChanged;
    event Action<string, string>? Error;

    string[] LiveIds();
    void Create(string id, int cols, int rows, string? cwd, string? shell, string? startupCommand, string? wslDistribution);
    void Attach(string id);
    void Write(string id, string data);
    void Resize(string id, int cols, int rows);
    void Kill(string id);
}

// A backend sharing the desktop's transport must replay only to the new
// subscriber, then bind it atomically with respect to incoming live output.
internal interface IGatewayReplayBackend
{
    Task ReplayAndBindAsync(string id, Action<string> replay, Action bind, CancellationToken ct, Action? beforeReplay = null);
}

internal readonly record struct GatewayTerminalGeometry(int Columns, int Rows);

// One PTY has one cursor grid. Mirrors must follow its owning desktop rather
// than resizing that process to each phone's viewport.
internal interface IGatewayGeometryBackend
{
    event Action<string, int, int>? GeometryChanged;
    GatewayTerminalGeometry? GetGeometry(string id);
    GatewayTerminalGeometry EnsureGeometry(string id);
}
