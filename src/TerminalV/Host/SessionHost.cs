using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TerminalV.Pty;

namespace TerminalV.Host;

internal static class SessionHost
{
    internal const string MutexName = "Local\\TerminalV.SessionHost";
    internal const string PipeName = "TerminalV.Host";

    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private static readonly ConcurrentDictionary<string, HostedSession> Sessions = new();
    private static readonly object WriteGate = new();
    private static StreamWriter? _writer;

    public static void Run()
    {
        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            return;
        }

        while (true)
        {
            using var pipe = new NamedPipeServerStream(
                PipeName,
                PipeDirection.InOut,
                1,
                PipeTransmissionMode.Byte,
                PipeOptions.Asynchronous);
            pipe.WaitForConnection();
            using var reader = new StreamReader(pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
            using var writer = new StreamWriter(pipe, new UTF8Encoding(false), 4096, leaveOpen: true)
            {
                AutoFlush = true
            };
            lock (WriteGate)
            {
                _writer = writer;
            }

            try
            {
                Serve(reader);
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
            finally
            {
                lock (WriteGate)
                {
                    if (ReferenceEquals(_writer, writer))
                    {
                        _writer = null;
                    }
                }
            }
        }
    }

    private static void Serve(StreamReader reader)
    {
        while (reader.ReadLine() is { } line)
        {
            HostRequest? request;
            try
            {
                request = JsonSerializer.Deserialize<HostRequest>(line, Json);
            }
            catch (JsonException)
            {
                continue;
            }

            if (request?.Type is null)
            {
                continue;
            }

            switch (request.Type)
            {
                case "list":
                    Send(new
                    {
                        type = "list",
                        ids = Sessions.Keys.ToArray()
                    });
                    break;
                case "create":
                    Create(request);
                    break;
                case "attach":
                    Attach(request.Id);
                    break;
                case "write":
                    if (request.Id is not null && request.Data is not null &&
                        Sessions.TryGetValue(request.Id, out var writing))
                    {
                        writing.Pty.Write(request.Data);
                    }
                    break;
                case "resize":
                    if (request.Id is not null && Sessions.TryGetValue(request.Id, out var resizing))
                    {
                        resizing.Pty.Resize(Math.Max(request.Cols, 1), Math.Max(request.Rows, 1));
                    }
                    break;
                case "kill":
                    if (request.Id is not null)
                    {
                        Kill(request.Id);
                    }
                    break;
            }
        }
    }

    private static void Create(HostRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Id))
        {
            return;
        }

        Kill(request.Id);
        var cwd = !string.IsNullOrWhiteSpace(request.Cwd) && Directory.Exists(request.Cwd)
            ? request.Cwd
            : Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var shell = ShellResolver.Resolve();
        var pty = ConPtySession.Start(
            request.Id,
            shell.CommandLine,
            cwd,
            Math.Max(request.Cols, 1),
            Math.Max(request.Rows, 1));
        var hosted = new HostedSession(pty);
        pty.Output += chunk =>
        {
            hosted.Add(chunk);
            Send(new { type = "data", id = request.Id, data = chunk });
        };
        pty.Exited += code =>
        {
            Send(new { type = "exit", id = request.Id, code });
            Kill(request.Id);
        };
        Sessions[request.Id] = hosted;
    }

    private static void Attach(string? id)
    {
        if (id is null || !Sessions.TryGetValue(id, out var hosted))
        {
            Send(new { type = "missing", id });
            return;
        }

        var snapshot = hosted.Snapshot();
        if (snapshot.Length > 0)
        {
            Send(new { type = "data", id, data = snapshot });
        }
    }

    private static void Kill(string id)
    {
        if (Sessions.TryRemove(id, out var hosted))
        {
            hosted.Dispose();
        }
    }

    private static void Send(object payload)
    {
        var json = JsonSerializer.Serialize(payload, Json);
        lock (WriteGate)
        {
            try
            {
                _writer?.WriteLine(json);
            }
            catch (IOException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private sealed class HostedSession : IDisposable
    {
        private const int Cap = 1_500_000;
        private readonly List<string> _chunks = [];
        private int _chars;

        public ConPtySession Pty { get; }

        public HostedSession(ConPtySession pty) => Pty = pty;

        public void Add(string chunk)
        {
            if (string.IsNullOrEmpty(chunk))
            {
                return;
            }

            _chunks.Add(chunk);
            _chars += chunk.Length;
            while (_chars > Cap && _chunks.Count > 1)
            {
                _chars -= _chunks[0].Length;
                _chunks.RemoveAt(0);
            }
        }

        public string Snapshot() => string.Concat(_chunks);

        public void Dispose() => Pty.Dispose();
    }

    private sealed class HostRequest
    {
        public string? Type { get; set; }
        public string? Id { get; set; }
        public string? Data { get; set; }
        public string? Cwd { get; set; }
        public int Cols { get; set; }
        public int Rows { get; set; }
    }
}
