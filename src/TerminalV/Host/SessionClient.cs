using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;

namespace TerminalV.Host;

internal sealed class SessionClient : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly object _gate = new();
    private NamedPipeClientStream? _pipe;
    private StreamWriter? _writer;
    private CancellationTokenSource? _readCts;

    public event Action<string, string>? Data;
    public event Action<string, uint>? Exited;
    public event Action<string, string, string?>? DirectoryChanged;
    public event Action<string, string>? Error;
    public bool? CwdTrackingSupported { get; private set; }

    public bool Ensure()
    {
        if (_pipe is { IsConnected: true })
        {
            return true;
        }

        StartHost();
        var pipe = new NamedPipeClientStream(".", SessionHost.PipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
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

            ready.Set();
        }

        _onPacket += Handler;
        try
        {
            Send(new { type = "list" });
            ready.Wait(TimeSpan.FromSeconds(2));
        }
        finally
        {
            _onPacket -= Handler;
        }

        return ids;
    }

    public void Create(string id, int cols, int rows, string? cwd) =>
        Send(new { type = "create", id, cols, rows, cwd });

    public void Attach(string id) => Send(new { type = "attach", id });

    public void Write(string id, string data) => Send(new { type = "write", id, data });

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

                    _onPacket?.Invoke(type, root.Clone());
                    var id = root.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                    if (type == "data" && id is not null && root.TryGetProperty("data", out var dataEl))
                    {
                        Data?.Invoke(id, dataEl.GetString() ?? "");
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
        lock (_gate)
        {
            try
            {
                _writer?.WriteLine(json);
            }
            catch (IOException)
            {
            }
        }
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
