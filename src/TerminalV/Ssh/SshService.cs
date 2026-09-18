namespace TerminalV.Ssh;

/// <summary>
/// Factory + registry for SSH sessions. Supports direct OpenSSH via SSH.NET ShellStream and TerminalV gateway WebSocket.
/// Handles host/port/user/auth, shell type xterm-256color, cols/rows, reconnect.
/// Intended for MAUI and WPF: no UI dependency.
/// </summary>
internal sealed class SshService : IDisposable
{
    private readonly Dictionary<string, ISshSession> _sessions = new();
    private readonly object _lock = new();
    private bool _disposed;

    public ISshSession Create(string id, SshConnectionOptions options)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SshService));
        if (string.IsNullOrWhiteSpace(id)) throw new ArgumentException("Id required.", nameof(id));
        options.Validate();
        // Normalize term
        if (string.IsNullOrWhiteSpace(options.TerminalType)) options.TerminalType = "xterm-256color";
        // Clamp
        options.Columns = Math.Clamp(options.Columns, 1, 1000);
        options.Rows = Math.Clamp(options.Rows, 1, 1000);

        ISshSession session = options.UseGateway
            ? new GatewaySshSession(id, options)
            : new SshNetSession(id, options);

        lock (_lock)
        {
            if (_sessions.TryGetValue(id, out var existing))
            {
                try { existing.Dispose(); } catch { }
                _sessions.Remove(id);
            }
            _sessions[id] = session;
        }
        // Auto-remove on close
        session.Closed += _ =>
        {
            lock (_lock) { _sessions.Remove(id); }
        };
        return session;
    }

    public ISshSession? TryGet(string id)
    {
        lock (_lock) return _sessions.TryGetValue(id, out var s) ? s : null;
    }

    public IReadOnlyCollection<ISshSession> List()
    {
        lock (_lock) return _sessions.Values.ToList().AsReadOnly();
    }

    public async Task<ISshSession> ConnectAsync(string id, SshConnectionOptions options, CancellationToken ct = default)
    {
        var s = Create(id, options);
        await s.ConnectAsync(ct).ConfigureAwait(false);
        return s;
    }

    public async Task DisconnectAsync(string id)
    {
        ISshSession? s;
        lock (_lock) _sessions.TryGetValue(id, out s);
        if (s is not null) await s.DisconnectAsync().ConfigureAwait(false);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        List<ISshSession> copy;
        lock (_lock) { copy = _sessions.Values.ToList(); _sessions.Clear(); }
        foreach (var s in copy) try { s.Dispose(); } catch { }
    }
}
