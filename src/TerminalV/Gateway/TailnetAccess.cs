using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text.Json;

namespace TerminalV.Gateway;

internal sealed record TailnetIdentity(string NodeId, string UserId, string Login, string Device);

internal interface ITailnetAccess
{
    Task<TailnetIdentity?> IdentifyAsync(IPEndPoint remote, IPEndPoint local, CancellationToken ct);
    bool IsApproved(TailnetIdentity identity);
    Task<bool> ApproveAsync(TailnetIdentity identity, CancellationToken ct);
}

// Identity comes from tailscaled's authenticated connection metadata, never HTTP headers.
internal sealed class TailnetAccess : ITailnetAccess
{
    private readonly string _path;
    private readonly Func<TailnetIdentity, CancellationToken, Task<bool>> _confirm;
    private readonly SemaphoreSlim _approval = new(1, 1);
    private readonly SemaphoreSlim _lookup = new(4, 4);
    private readonly object _gate = new();
    private readonly HashSet<string> _grants;
    private DateTime _nextPrompt;
    private int _generation;
    public TailnetAccess(string path, Func<TailnetIdentity, CancellationToken, Task<bool>> confirm)
    {
        _path = path; _confirm = confirm;
        try { _grants = JsonSerializer.Deserialize<HashSet<string>>(File.ReadAllText(path)) ?? []; }
        catch { _grants = []; }
    }
    private static string Key(TailnetIdentity i) => i.UserId + ":" + i.NodeId;
    public bool IsApproved(TailnetIdentity i) { lock (_gate) return _grants.Contains(Key(i)); }
    public void RevokeAll()
    {
        lock (_gate) { Save([]); _grants.Clear(); _generation++; }
    }
    private void Save(HashSet<string> grants)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(_path)!);
        var temp = _path + ".tmp";
        File.WriteAllText(temp, JsonSerializer.Serialize(grants));
        File.Move(temp, _path, true);
    }
    public async Task<bool> ApproveAsync(TailnetIdentity identity, CancellationToken ct)
    {
        if (IsApproved(identity)) return true;
        if (!await _approval.WaitAsync(0, ct)) return false;
        try
        {
            if (IsApproved(identity)) return true;
            if (DateTime.UtcNow < _nextPrompt) return false;
            _nextPrompt = DateTime.UtcNow.AddSeconds(15);
            int generation;
            lock (_gate) generation = _generation;
            if (!await _confirm(identity, ct)) return false;
            ct.ThrowIfCancellationRequested();
            lock (_gate)
            {
                if (_generation != generation) return false;
                var next = new HashSet<string>(_grants) { Key(identity) };
                Save(next);
                _grants.Add(Key(identity));
            }
            return true;
        }
        finally { _approval.Release(); }
    }
    internal static bool IsTailnet(IPAddress ip)
    {
        if (ip.IsIPv4MappedToIPv6) ip = ip.MapToIPv4();
        var b = ip.GetAddressBytes();
        return b.Length == 4 ? b[0] == 100 && (b[1] & 192) == 64
            : b.Length == 16 && b[0] == 0xfd && b[1] == 0x7a && b[2] == 0x11 && b[3] == 0x5c && b[4] == 0xa1 && b[5] == 0xe0;
    }
    public async Task<TailnetIdentity?> IdentifyAsync(IPEndPoint remote, IPEndPoint local, CancellationToken ct)
    {
        if (!IsTailnet(remote.Address) || !IsTailnet(local.Address)) return null;
        if (!await _lookup.WaitAsync(0, ct)) return null;
        try
        {
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Tailscale", "tailscale.exe");
            var start = new ProcessStartInfo(exe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            start.ArgumentList.Add("whois"); start.ArgumentList.Add("--json"); start.ArgumentList.Add(remote.ToString());
            using var process = Process.Start(start)!;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            var output = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var error = process.StandardError.ReadToEndAsync(timeout.Token);
            try { await process.WaitForExitAsync(timeout.Token); }
            catch { try { process.Kill(); } catch { } throw; }
            await error;
            if (process.ExitCode != 0) return null;
            using var doc = JsonDocument.Parse(await output);
            return ParseIdentity(doc.RootElement);
        }
        catch { return null; } // Missing/offline/unauthorized LocalAPI fails closed.
        finally { _lookup.Release(); }
    }
    internal static TailnetIdentity? ParseIdentity(JsonElement root)
    {
        if (!root.TryGetProperty("Node", out var node) || !root.TryGetProperty("UserProfile", out var user)) return null;
        if (node.TryGetProperty("Tags", out var tags) && tags.ValueKind == JsonValueKind.Array && tags.GetArrayLength() > 0) return null;
        if (!node.TryGetProperty("StableID", out var stable) || string.IsNullOrWhiteSpace(stable.GetString())) return null;
        if (!user.TryGetProperty("ID", out var id) || id.ToString() == "0") return null;
        if (!user.TryGetProperty("LoginName", out var login) || string.IsNullOrWhiteSpace(login.GetString())) return null;
        return new(stable.GetString()!, id.ToString(), login.GetString()!, node.GetProperty("Name").GetString() ?? "Устройство");
    }
}
