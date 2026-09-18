namespace TerminalV.Ssh;

internal enum SshSessionState { Disconnected, Connecting, Connected, Faulted }

internal interface ISshSession : IAsyncDisposable, IDisposable
{
    string Id { get; }
    SshConnectionOptions Options { get; }
    SshSessionState State { get; }
    event Action<string>? DataReceived;
    event Action<string>? ErrorReceived;
    event Action<int?>? Closed;

    Task ConnectAsync(CancellationToken cancellationToken = default);
    Task DisconnectAsync();
    Task WriteAsync(string data, CancellationToken cancellationToken = default);
    Task ResizeAsync(int cols, int rows, CancellationToken cancellationToken = default);
    Task ReconnectAsync(CancellationToken cancellationToken = default);
}
