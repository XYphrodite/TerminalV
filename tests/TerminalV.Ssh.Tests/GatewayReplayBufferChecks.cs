using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using TerminalV.Gateway;
using TerminalV.Host;

internal static class GatewayReplayBufferChecks
{
    private const int Capacity = 1_500_000;
    private const string Modes = "\x1b[?1049h\x1b[?1003h\x1b[?1006h";

    public static async Task Run()
    {
        // Exercise the actual host -> shared backend -> desktop/subscriber path.
        // The host's mode prefix is additional to its bounded raw history, so a
        // second plain StringBuilder trim used to remove it immediately.
        var hostReplay = new TerminalReplayBuffer(Capacity);
        hostReplay.Add("\x1b[?1049;1003;1006h");
        hostReplay.Add(new string('A', Capacity));
        var initial = hostReplay.Snapshot();
        Require(initial.Length > Capacity, "fixture must exceed the gateway's raw history limit");

        using var host = new PrivateHost(initial);
        using var client = new SessionClient(host.Name, () => { });
        using var backend = new SessionClientBackend(client);
        var desktop = new ConcurrentQueue<(string Data, bool Replay)>();
        backend.DesktopData += (id, data, replay) =>
        {
            if (id == "grok") desktop.Enqueue((data, replay));
        };
        Require(client.Ensure(), "private fixture pipe must connect");

        await backend.AttachDesktopAsync("grok").WaitAsync(TimeSpan.FromSeconds(5));
        Require(desktop.TryDequeue(out var first) && first.Replay && first.Data == initial,
            "desktop must receive the host's full alternate-screen and SGR mouse prefix");
        await CheckSubscriber(backend, initial);
        Require(desktop.IsEmpty, "new subscriber replay must not leak into the existing desktop");

        var live = new string('B', Capacity + 37);
        host.Emit(live);
        // Ordered pipe barrier: the entire preceding live packet was processed.
        Require(client.List().SequenceEqual(["grok"]), "live output barrier must complete");
        Require(desktop.TryDequeue(out var update) && !update.Replay && update.Data == live,
            "existing desktop must receive live output unchanged, exactly once");
        var expected = Modes + new string('B', Capacity);
        await backend.AttachDesktopAsync("grok").WaitAsync(TimeSpan.FromSeconds(5));
        Require(desktop.TryDequeue(out var restored) && restored.Replay && restored.Data == expected,
            "desktop reconnect must retain modes after another full history rollover");
        await CheckSubscriber(backend, expected);
        Require(desktop.IsEmpty, "subscribers must not resend cached output to the desktop");

        // A later shell has normal history again; neither layer may permanently
        // force the TUI modes which were active at an earlier trim boundary.
        var shell = new string('C', Capacity);
        var exit = "\x1b[?1003;1006;1049l" + shell;
        host.Emit(exit);
        Require(client.List().SequenceEqual(["grok"]), "TUI exit barrier must complete");
        Require(desktop.TryDequeue(out var exited) && !exited.Replay && exited.Data == exit,
            "TUI exit and following shell output must be delivered unmodified");
        await backend.AttachDesktopAsync("grok").WaitAsync(TimeSpan.FromSeconds(5));
        Require(desktop.TryDequeue(out var normal) && normal.Replay && normal.Data == shell,
            "desktop replay after TUI exit must return to normal scrollback and mouse modes");
        await CheckSubscriber(backend, shell);
        Require(desktop.IsEmpty && host.AttachCount == 1,
            "all reconnects and new subscribers must share one original host attachment");
    }

    private static async Task CheckSubscriber(SessionClientBackend backend, string expected)
    {
        string? replay = null;
        var bound = false;
        await backend.ReplayAndBindAsync("grok", data => replay = data, () => bound = true, default);
        Require(bound && replay == expected, "new subscriber must get the complete mode-aware snapshot before binding");
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    private sealed class PrivateHost : IDisposable
    {
        public string Name { get; } = "TerminalV.GatewayReplay.Test." + Guid.NewGuid().ToString("N");
        private readonly NamedPipeServerStream _pipe;
        private readonly Task _serve;
        private readonly object _writeGate = new();
        private StreamWriter? _writer;
        private int _attachCount;
        public int AttachCount => Volatile.Read(ref _attachCount);

        public PrivateHost(string snapshot)
        {
            _pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _serve = Task.Run(async () =>
            {
                try
                {
                    await _pipe.WaitForConnectionAsync();
                    using var reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                    using var writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                    lock (_writeGate) _writer = writer;
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        using var packet = JsonDocument.Parse(line);
                        switch (packet.RootElement.GetProperty("type").GetString())
                        {
                            case "list": Send(new { type = "list", ids = new[] { "grok" } }); break;
                            case "attach":
                                Interlocked.Increment(ref _attachCount);
                                Emit(snapshot);
                                break;
                        }
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            });
        }

        public void Emit(string data) => Send(new { type = "data", id = "grok", data });

        private void Send(object packet)
        {
            lock (_writeGate) _writer!.WriteLine(JsonSerializer.Serialize(packet));
        }

        public void Dispose()
        {
            _pipe.Dispose();
            try { _serve.Wait(TimeSpan.FromSeconds(5)); } catch { }
        }
    }
}
