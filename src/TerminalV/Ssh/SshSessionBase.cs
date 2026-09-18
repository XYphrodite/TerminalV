namespace TerminalV.Ssh;

internal abstract class SshSessionBase : ISshSession
{
    protected readonly SshConnectionOptions _options;
    private int _disposed;
    private int _cols;
    private int _rows;

    protected SshSessionBase(string id, SshConnectionOptions options)
    {
        Id = id ?? throw new ArgumentNullException(nameof(id));
        _options = options.Clone();
        _options.Validate();
        // clamp
        _cols = Math.Clamp(_options.Columns, 1, 1000);
        _rows = Math.Clamp(_options.Rows, 1, 1000);
        Columns = _cols;
        Rows = _rows;
        State = SshSessionState.Disconnected;
    }

    public string Id { get; }
    public SshConnectionOptions Options => _options;
    public SshSessionState State { get; protected set; }
    public int Columns { get; private set; }
    public int Rows { get; private set; }
    public string TerminalType => string.IsNullOrWhiteSpace(_options.TerminalType) ? "xterm-256color" : _options.TerminalType;

    public event Action<string>? DataReceived;
    public event Action<string>? ErrorReceived;
    public event Action<int?>? Closed;

    protected void RaiseData(string data) => DataReceived?.Invoke(data);
    protected void RaiseError(string msg) => ErrorReceived?.Invoke(msg);
    protected void RaiseClosed(int? code = null) => Closed?.Invoke(code);

    public abstract Task ConnectAsync(CancellationToken cancellationToken = default);
    public abstract Task DisconnectAsync();
    public abstract Task WriteAsync(string data, CancellationToken cancellationToken = default);

    public virtual Task ResizeAsync(int cols, int rows, CancellationToken cancellationToken = default)
    {
        if (_disposed != 0) return Task.CompletedTask;
        Columns = Math.Clamp(cols, 1, 1000);
        Rows = Math.Clamp(rows, 1, 1000);
        return OnResizeAsync(Columns, Rows, cancellationToken);
    }

    protected abstract Task OnResizeAsync(int cols, int rows, CancellationToken cancellationToken);

    public async Task ReconnectAsync(CancellationToken cancellationToken = default)
    {
        if (_disposed != 0) throw new ObjectDisposedException(nameof(SshSessionBase));
        var attempts = 0;
        var max = _options.MaxReconnectAttempts <= 0 && _options.AutoReconnect ? int.MaxValue : Math.Max(1, _options.MaxReconnectAttempts);
        var delay = TimeSpan.FromSeconds(1);
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                await DisconnectAsync().ConfigureAwait(false);
                await ConnectAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                attempts++;
                if (!_options.AutoReconnect || attempts >= max)
                {
                    State = SshSessionState.Faulted;
                    RaiseError(ex.Message);
                    throw;
                }
                RaiseError($"Reconnect attempt {attempts} failed: {ex.Message}. Retrying in {delay.TotalSeconds}s");
                await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                delay = TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, 30));
            }
        }
    }

    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) DisposeCore(); }
    public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    protected virtual void DisposeCore() { }
    protected bool IsDisposed => _disposed != 0;
}
