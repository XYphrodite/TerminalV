using TerminalV.Host;

namespace TerminalV.Gateway;

/// <summary>Shares the desktop's only connection to an existing session host.</summary>
internal sealed class SessionClientBackend : IGatewaySessionBackend, IGatewayReplayBackend, IDisposable
{
    private const int SnapshotCapacity = 1_500_000;
    private readonly SessionClient _client;
    private readonly object _gate = new();
    private readonly Dictionary<string, Snapshot> _snapshots = new();
    private bool _disposed;

    private sealed class Snapshot
    {
        public TerminalReplayBuffer Text { get; set; } = new(SnapshotCapacity);
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool Prepared { get; set; }
        public bool DesktopAttached { get; set; }
        public Task? DesktopReplay { get; set; }
    }

    public SessionClientBackend(SessionClient client)
    {
        _client = client;
        _client.Data += OnData;
        _client.Exited += OnExited;
        _client.DirectoryChanged += OnDirectoryChanged;
        _client.Error += OnError;
        _client.ReplayCompleted += OnReplayCompleted;
    }

    public event Action<string, string>? Data;
    public event Action<string, uint>? Exited;
    public event Action<string, string>? DirectoryChanged;
    public event Action<string, string>? Error;
    public event Action<string, string, bool>? DesktopData;
    public event Action<string, string?, string?, string?, string?>? Created;

    public string[] LiveIds()
    {
        Ensure();
        return _client.List();
    }

    public void Create(string id, int cols, int rows, string? cwd, string? shell, string? startupCommand, string? wslDistribution)
    {
        Ensure();
        SessionCreated(id);
        _client.Create(id, cols, rows, cwd, shell, startupCommand, wslDistribution);
        Created?.Invoke(id, cwd, shell, startupCommand, wslDistribution);
    }

    public void SessionCreated(string id, bool desktopAttached = false)
    {
        lock (_gate)
        {
            ThrowIfDisposed();
            if (_snapshots.Remove(id, out var previous)) previous.Ready.TrySetCanceled();
            var snapshot = new Snapshot { DesktopAttached = desktopAttached };
            snapshot.Ready.SetResult(true);
            _snapshots[id] = snapshot;
        }
    }

    public Task AttachDesktopAsync(string id)
    {
        lock (_gate)
        {
            var snapshot = GetSnapshot(id);
            // Duplicate requests before initial history arrives share one replay.
            if (snapshot.DesktopReplay is { IsCompleted: false }) return snapshot.DesktopReplay;
            var completed = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            snapshot.DesktopReplay = completed.Task;
            _ = AttachDesktopCoreAsync(id, snapshot, completed);
            return completed.Task;
        }
    }

    private async Task AttachDesktopCoreAsync(string id, Snapshot snapshot, TaskCompletionSource<bool> completed)
    {
        try
        {
            await EnsureSnapshotAsync(id, snapshot, default).ConfigureAwait(false);
            lock (_gate)
            {
                AssertCurrent(id, snapshot);
                var replay = snapshot.Text.Snapshot();
                if (replay.Length > 0) DesktopData?.Invoke(id, replay, true);
                snapshot.DesktopAttached = true;
                completed.TrySetResult(true);
            }
        }
        catch (Exception ex) { completed.TrySetException(ex); }
    }

    private Snapshot GetSnapshot(string id)
    {
        ThrowIfDisposed();
        if (!_snapshots.TryGetValue(id, out var snapshot) || (snapshot.Ready.Task.IsCompleted && !snapshot.Ready.Task.IsCompletedSuccessfully))
            _snapshots[id] = snapshot = new Snapshot();
        return snapshot;
    }

    private void AssertCurrent(string id, Snapshot snapshot)
    {
        ThrowIfDisposed();
        if (!_snapshots.TryGetValue(id, out var current) || !ReferenceEquals(current, snapshot))
            throw new InvalidOperationException("Сессия изменилась во время подключения. Повторите подключение.");
    }

    private async Task EnsureSnapshotAsync(string id, Snapshot snapshot, CancellationToken ct)
    {
        bool start;
        lock (_gate)
        {
            AssertCurrent(id, snapshot);
            start = !snapshot.Prepared && !snapshot.Ready.Task.IsCompleted;
            if (start)
            {
                snapshot.Text = new TerminalReplayBuffer(SnapshotCapacity);
                snapshot.Prepared = true;
            }
        }
        if (start)
        {
            // The old host accepts one pipe. Attach includes a synchronous list
            // barrier write, so it must never run while holding the cache lock.
            _ = Task.Run(() =>
            {
                try
                {
                    Ensure();
                    lock (_gate) AssertCurrent(id, snapshot);
                    _client.Attach(id);
                }
                catch (Exception ex) { snapshot.Ready.TrySetException(ex); }
            });
        }
        await snapshot.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5), ct).ConfigureAwait(false);
    }

    public async Task ReplayAndBindAsync(string id, Action<string> replay, Action bind, CancellationToken ct)
    {
        Snapshot snapshot;
        lock (_gate)
        {
            snapshot = GetSnapshot(id);
        }
        await EnsureSnapshotAsync(id, snapshot, ct).ConfigureAwait(false);
        lock (_gate)
        {
            AssertCurrent(id, snapshot);
            ct.ThrowIfCancellationRequested();
            // OnData holds this same lock: snapshot first, then every subsequent
            // live chunk exactly once. Only the new subscriber gets the replay.
            var output = snapshot.Text.Snapshot();
            if (output.Length > 0) replay(output);
            bind();
        }
    }

    // GatewayServer uses the optional replay API instead of broadcasting Attach.
    public void Attach(string id) => throw new InvalidOperationException("Для общего подключения требуется адресное восстановление вывода.");

    public void Write(string id, string data) { Ensure(); _client.Write(id, data); }
    public void Resize(string id, int cols, int rows) { Ensure(); _client.Resize(id, cols, rows); }
    public void Kill(string id) { Ensure(); _client.Kill(id); }

    private void OnData(string id, string data, bool replay)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!_snapshots.TryGetValue(id, out var snapshot)) _snapshots[id] = snapshot = new Snapshot();
            // The host's bounded replay includes its discarded mode prefix.
            // A second raw tail trim here would immediately lose that prefix.
            snapshot.Text.Add(data);
            // The replay flag also covers real live output during attach.
            // Dropping flagged data would lose output on an older session host.
            // Old hosts do not mark the snapshot packet: live data racing before
            // that first packet can also be present inside its history. Preserve
            // all bytes here; exact initial deduplication needs a host protocol
            // marker. Completed snapshots are replayed only to new subscribers.
            if (snapshot.DesktopAttached) DesktopData?.Invoke(id, data, replay);
            Data?.Invoke(id, data);
        }
    }

    private void OnReplayCompleted(string id)
    {
        lock (_gate)
            if (!_disposed && _snapshots.TryGetValue(id, out var snapshot) && snapshot.Prepared)
                snapshot.Ready.TrySetResult(true);
    }

    private void OnExited(string id, uint code)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_snapshots.Remove(id, out var snapshot)) snapshot.Ready.TrySetCanceled();
            Exited?.Invoke(id, code);
        }
    }
    private void OnDirectoryChanged(string id, string cwd, string? notice)
    {
        lock (_gate) { if (!_disposed) DirectoryChanged?.Invoke(id, cwd); }
    }
    private void OnError(string id, string message)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (_snapshots.TryGetValue(id, out var snapshot) && snapshot.Prepared)
                snapshot.Ready.TrySetException(new InvalidOperationException(message));
            Error?.Invoke(id, message);
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _client.Data -= OnData;
            _client.Exited -= OnExited;
            _client.DirectoryChanged -= OnDirectoryChanged;
            _client.Error -= OnError;
            _client.ReplayCompleted -= OnReplayCompleted;
            foreach (var snapshot in _snapshots.Values) snapshot.Ready.TrySetCanceled();
            _snapshots.Clear();
        }
        // The desktop owns the borrowed client and the live session host.
    }

    private void ThrowIfDisposed() { if (_disposed) throw new ObjectDisposedException(nameof(SessionClientBackend)); }
    private void Ensure()
    {
        ThrowIfDisposed();
        if (!_client.Ensure()) throw new InvalidOperationException("Фоновый процесс TerminalV недоступен.");
    }
}
