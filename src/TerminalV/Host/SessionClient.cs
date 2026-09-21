using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Threading;
using TerminalV.Diagnostics;

namespace TerminalV.Host;

internal sealed class SessionClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private readonly string _pipeName;
    private readonly Action _startHost;
    private readonly OutputReplayGuard _replay = new();
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readCts;

    public event Action<string, string, bool>? Data;
    public event Action<string, uint>? Exited;
    public event Action<string, string, string?>? DirectoryChanged;
    public event Action<string, string>? Error;
    public bool? CwdTrackingSupported { get; private set; }
    public bool? LaunchProfilesSupported { get; private set; }
    public bool? WslLaunchSupported { get; private set; }
    public bool? EnvironmentRefreshSupported { get; private set; }

    // Tests use a private pipe and a no-op starter, never the user's live host.
    public SessionClient(string pipeName = SessionHost.PipeName, Action? startHost = null)
    {
        _pipeName = pipeName;
        _startHost = startHost ?? StartHost;
    }

    public bool Ensure()
    {
        if (_pipe is { IsConnected: true })
        {
            return true;
        }

        _startHost();
        var pipe = new NamedPipeClientStream(".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        var connected = false;
        for (var attempt = 0; attempt < 40; attempt++)
        {
            try
            {
                pipe.Connect(100);
                connected = true;
                break;
            }
            catch (TimeoutException)
            {
            }
            catch (IOException)
            {
                Thread.Sleep(50);
            }
        }

        if (!connected)
        {
            pipe.Dispose();
            return false;
        }

        _pipe = pipe;
        _replay.Reset();
        LaunchProfilesSupported = null;
        WslLaunchSupported = null;
        EnvironmentRefreshSupported = null;
        _writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
        _readCts = new CancellationTokenSource();
        _ = Task.Factory.StartNew(() => ReadLoop(_readCts.Token), TaskCreationOptions.LongRunning);
        return true;
    }

    public string[] List()
    {
        if (!Ensure())
        {
            return [];
        }

        using var ready = new ManualResetEventSlim(false);
        string[] ids = [];
        void Handler(string type, JsonElement root)
        {
            if (type != "list")
            {
                return;
            }

            if (root.TryGetProperty("ids", out var array) && array.ValueKind == JsonValueKind.Array)
            {
                ids = array.EnumerateArray().Select(item => item.GetString() ?? "").Where(id => id.Length > 0).ToArray();
            }

            CwdTrackingSupported = root.TryGetProperty("cwdTrackingSupported", out var supported) &&
                supported.ValueKind == JsonValueKind.True;
            LaunchProfilesSupported = root.TryGetProperty("launchProfilesSupported", out var profiles) &&
                profiles.ValueKind == JsonValueKind.True;
            WslLaunchSupported = root.TryGetProperty("wslLaunchSupported", out var wsl) && wsl.ValueKind == JsonValueKind.True;
            EnvironmentRefreshSupported = root.TryGetProperty("environmentRefreshSupported", out var environment) &&
                environment.ValueKind == JsonValueKind.True;

            ready.Set();
        }

        _onPacket += Handler;
        try
        {
            lock (_gate)
            {
                _replay.BeginList();
                Send(new { type = "list" });
            }
            ready.Wait(TimeSpan.FromSeconds(2));
        }
        finally
        {
            _onPacket -= Handler;
        }

        return ids;
    }

    public void Create(string id, int cols, int rows, string? cwd, string? shell = null, string? startupCommand = null, string? wslDistribution = null)
    {
        if (shell is not null)
        {
            // Never let an older host silently ignore the selected shell or command.
            LaunchProfilesSupported = null;
            WslLaunchSupported = null;
            List();
            if (shell == "wsl" && WslLaunchSupported != true)
                throw new InvalidOperationException("Фоновый процесс не поддерживает запуск WSL. Сохраните работу и перезагрузите Windows для его обновления. Работающие сессии не прерывались.");
            if (LaunchProfilesSupported != true)
                throw new InvalidOperationException("Фоновый процесс не поддерживает профили запуска. Сохраните работу и перезагрузите Windows для его обновления. Работающие сессии не прерывались.");
        }
        Send(new { type = shell is null ? "create" : "create-profile", id, cols, rows, cwd, shell, startupCommand, wslDistribution });
    }

    public void Attach(string id)
    {
        lock (_gate)
        {
            _replay.BeginList(id);
            Send(new { type = "attach", id });
            Send(new { type = "list" });
        }
    }

    public void Write(string id, string data)
    {
        var swTotal = Stopwatch.StartNew();
        const int Chunk = 4000;
        int chunks = 0;
        if (data.Length <= Chunk)
        {
            chunks = 1;
            Send(new { type = "write", id, data });
        }
        else
        {
            for (var i = 0; i < data.Length;)
            {
                var len = Math.Min(Chunk, data.Length - i);
                if (len < data.Length - i && char.IsHighSurrogate(data[i + len - 1]) && i + len < data.Length && char.IsLowSurrogate(data[i + len]))
                    len--;
                Send(new { type = "write", id, data = data.Substring(i, len) });
                i += len;
                chunks++;
            }
        }
        swTotal.Stop();
        if (chunks > 1 || swTotal.ElapsedMilliseconds > 20)
            Diag.Log("client", $"Write id={id} len={data.Length} chunks={chunks} ms={swTotal.ElapsedMilliseconds}", null);
    }

    public void Resize(string id, int cols, int rows) => Send(new { type = "resize", id, cols, rows });

    public void Kill(string id) => Send(new { type = "kill", id });

    public void Dispose()
    {
        _readCts?.Cancel();
        _writer?.Dispose();
        _pipe?.Dispose();
    }

    private event Action<string, JsonElement>? _onPacket;

    private void ReadLoop(CancellationToken cancellationToken)
    {
        try
        {
            using var reader = new StreamReader(_pipe!, Encoding.UTF8, false, 4096, leaveOpen: true);
            while (!cancellationToken.IsCancellationRequested && reader.ReadLine() is { } line)
            {
                JsonDocument document;
                try
                {
                    document = JsonDocument.Parse(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                using (document)
                {
                    var root = document.RootElement;
                    var type = root.TryGetProperty("type", out var typeEl) ? typeEl.GetString() : null;
                    if (type is null)
                    {
                        continue;
                    }

                    if (type != "list" || _replay.CompleteList())
                        _onPacket?.Invoke(type, root.Clone());
                    var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (type == "data" && id is not null && root.TryGetProperty("data", out var dataEl))
                    {
                        Data?.Invoke(id, dataEl.GetString() ?? "", _replay.IsReplaying(id));
                    }
                    else if (type == "exit" && id is not null)
                    {
                        var code = root.TryGetProperty("code", out var codeEl) ? codeEl.GetUInt32() : 0;
                        Exited?.Invoke(id, code);
                    }
                    else if (type == "cwd" && id is not null && root.TryGetProperty("cwd", out var cwdEl))
                    {
                        var notice = root.TryGetProperty("notice", out var noticeEl) ? noticeEl.GetString() : null;
                        DirectoryChanged?.Invoke(id, cwdEl.GetString() ?? "", notice);
                    }
                    else if (type == "error" && id is not null && root.TryGetProperty("message", out var errorEl))
                    {
                        Error?.Invoke(id, errorEl.GetString() ?? "");
                    }
                    else if (type == "missing" && id is not null)
                    {
                        Error?.Invoke(id, "Сессия завершилась до подключения. Для нового запуска нажмите «Перезапустить».");
                    }
                }
            }
        }
        catch (IOException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
    }

    private void Send(object payload)
    {
        var json = JsonSerializer.Serialize(payload, Json);
        var sw = Stopwatch.StartNew();
        bool waited = false;
        // Try to detect lock contention
        if (!Monitor.TryEnter(_gate, 50))
        {
            waited = true;
            Diag.Log("client", $"Send gate contention type={(payload.GetType().GetProperty("type")?.GetValue(payload) ?? "?")}", null);
            Monitor.Enter(_gate);
        }
        try
        {
            try
            {
                _writer?.WriteLine(json);
            }
            catch (IOException ex)
            {
                Diag.Log("client", $"Send write failed {ex.Message}", null);
            }
        }
        finally
        {
            Monitor.Exit(_gate);
        }
        sw.Stop();
        if (sw.ElapsedMilliseconds > 30 || waited)
            Diag.Log("client", $"Send done len={json.Length} ms={sw.ElapsedMilliseconds} waited={waited}", null);
    }

    private static void StartHost()
    {
        if (Mutex.TryOpenExisting(SessionHost.MutexName, out var existing))
        {
            existing.Dispose();
            return;
        }

        var exe = Environment.ProcessPath;
        if (string.IsNullOrWhiteSpace(exe))
        {
            return;
        }

        Process.Start(new ProcessStartInfo(exe, "--host")
        {
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }
}
