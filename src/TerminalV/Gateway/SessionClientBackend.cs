using TerminalV.Host;

namespace TerminalV.Gateway;

/// <summary>
/// <see cref="IGatewaySessionBackend"/> over the running desktop session host.
/// </summary>
internal sealed class SessionClientBackend : IGatewaySessionBackend, IDisposable
{
    private readonly SessionClient _client = new();
    private bool _disposed;

    public SessionClientBackend()
    {
        _client.Data += (id, data, _) => Data?.Invoke(id, data);
        _client.Exited += (id, code) => Exited?.Invoke(id, code);
        _client.DirectoryChanged += (id, cwd, _) => DirectoryChanged?.Invoke(id, cwd);
        _client.Error += (id, message) => Error?.Invoke(id, message);
    }

    public event Action<string, string>? Data;
    public event Action<string, uint>? Exited;
    public event Action<string, string>? DirectoryChanged;
    public event Action<string, string>? Error;

    public string[] LiveIds()
    {
        try
        {
            return EnsureCore() ? _client.List() : [];
        }
        catch
        {
            return [];
        }
    }

    public void Create(string id, int cols, int rows, string? cwd, string? shell, string? startupCommand, string? wslDistribution)
    {
        Ensure();
        _client.Create(id, cols, rows, cwd, shell, startupCommand, wslDistribution);
    }

    public void Attach(string id)
    {
        Ensure();
        _client.Attach(id);
    }

    public void Write(string id, string data)
    {
        Ensure();
        _client.Write(id, data);
    }

    public void Resize(string id, int cols, int rows)
    {
        Ensure();
        _client.Resize(id, cols, rows);
    }

    public void Kill(string id)
    {
        Ensure();
        _client.Kill(id);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _client.Dispose();
    }

    private void Ensure()
    {
        if (!EnsureCore())
        {
            throw new InvalidOperationException("Фоновый процесс TerminalV недоступен.");
        }
    }

    private bool EnsureCore()
    {
        try
        {
            return _client.Ensure();
        }
        catch
        {
            return false;
        }
    }
}
