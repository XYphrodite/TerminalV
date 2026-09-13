namespace TerminalV.Host;

// The host processes requests in order. A list reply after attach is a barrier:
// everything preceding it may be cached output, not a fresh notification.
// This uses the existing protocol and also works with older running hosts.
internal sealed class OutputReplayGuard
{
    private readonly object _gate = new();
    private readonly Queue<string?> _barriers = new();
    private readonly Dictionary<string, int> _attaching = new();

    public void BeginList(string? attachingId = null)
    {
        lock (_gate)
        {
            _barriers.Enqueue(attachingId);
            if (attachingId is not null)
                _attaching[attachingId] = _attaching.GetValueOrDefault(attachingId) + 1;
        }
    }

    public bool IsReplaying(string id)
    {
        lock (_gate) return _attaching.ContainsKey(id);
    }

    // True only for a regular List() call, not an internal attach barrier.
    public bool CompleteList()
    {
        lock (_gate)
        {
            if (!_barriers.TryDequeue(out var id) || id is null) return true;
            if (_attaching[id] == 1) _attaching.Remove(id);
            else _attaching[id]--;
            return false;
        }
    }

    public void Reset()
    {
        lock (_gate)
        {
            _barriers.Clear();
            _attaching.Clear();
        }
    }
}
