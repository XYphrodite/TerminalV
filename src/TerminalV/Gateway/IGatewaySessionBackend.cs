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
