using System.Collections.Concurrent;
using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TerminalV.Gateway;
using TerminalV.Host;
using TerminalV.Ssh;

internal static class SharedGatewayChecks
{
    public static async Task Run()
    {
        static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        using var host = new PrivateHost();
        var starts = 0;
        using var client = new SessionClient(host.Name, () => Interlocked.Increment(ref starts));
        using var backend = new SessionClientBackend(client);
        var desktop = new ConcurrentQueue<string>();
        var desktopEvents = new ConcurrentQueue<(string id, string data)>();
        var backendEvents = new ConcurrentQueue<(string id, string data)>();
        backend.Data += (id, data) => backendEvents.Enqueue((id, data));
        backend.DesktopData += (id, data, _) =>
        {
            desktopEvents.Enqueue((id, data));
            if (id == "desktop") desktop.Enqueue(data);
        };
        var connections = await Task.WhenAll(Enumerable.Range(0, 4).Select(_ => Task.Run(client.Ensure)));
        Require(connections.All(c => c) && starts == 1, "concurrent Ensure must establish only one borrowed pipe");
        Require(backend.LiveIds().SequenceEqual(["desktop"]), "gateway list must use the already-connected desktop client");

        var attaching = backend.AttachDesktopAsync("desktop", 160, 45);
        Require(ReferenceEquals(attaching, backend.AttachDesktopAsync("desktop")),
            "duplicate pending desktop attachments must share one replay");
        var initial = new List<string>();
        var bound = false;
        var pending = Task.CompletedTask;
        try
        {
            await host.AttachSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            pending = backend.ReplayAndBindAsync("desktop", initial.Add, () => bound = true, default);
            Require(!pending.IsCompleted && !bound, "startup must wait for complete history instead of binding an empty cache");
        }
        finally { host.ReleaseAttach.TrySetResult(true); }
        await Task.WhenAll(attaching, pending).WaitAsync(TimeSpan.FromSeconds(5));
        const string history = "history\alive-during-replay";
        Require(initial.Single() == history && bound, "the snapshot includes live data received inside the replay barrier");
        Require(string.Concat(desktop) == history, "the desktop receives original history and live data once");
        while (desktop.TryDequeue(out _)) { } // A newly restored desktop terminal is empty.
        await backend.AttachDesktopAsync("desktop");
        Require(string.Concat(desktop) == history, "repeat desktop attachment uses the complete cache");
        Require(host.Requests.Count(p => p.GetProperty("type").GetString() == "attach") == 1,
            "local replay must not request another host snapshot");

        using var reserve = new TcpListener(IPAddress.Loopback, 0);
        reserve.Start(); var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        using var gateway = new GatewayServer(backend, port, "test-token");
        gateway.Start();
        var url = $"ws://127.0.0.1:{port}";
        using (var control = new GatewayControlClient(url, "test-token"))
            Require((await control.ListAsync()).SequenceEqual(["desktop"]), "WebSocket list must find the live desktop session");
        GatewaySshSession Mirror(string id) => new(id, new SshConnectionOptions
        {
            Host = "unused", Username = "unused", Password = "unused", GatewayUrl = url,
            GatewayToken = "test-token", MirrorSessionId = "desktop", AutoReconnect = false, Columns = 40, Rows = 20
        });
        using var first = Mirror("phone-one");
        var firstOutput = new ConcurrentQueue<string>();
        var firstTimeline = new ConcurrentQueue<string>();
        var transportErrors = new ConcurrentQueue<string>();
        var closedTransports = new ConcurrentQueue<string>();
        first.DataReceived += firstOutput.Enqueue;
        first.DataReceived += data => firstTimeline.Enqueue("data:" + data);
        first.TerminalSizeChanged += (cols, rows) => firstTimeline.Enqueue($"size:{cols}x{rows}");
        first.ErrorReceived += message => transportErrors.Enqueue("phone-one: " + message);
        first.Closed += _ => closedTransports.Enqueue("phone-one");
        await first.ConnectAsync();
        await Until(() => string.Concat(firstOutput) == history);
        Require(firstTimeline.First() == "size:160x45" && first.TerminalColumns == 160 && first.TerminalRows == 45,
            "a mobile parser must receive the desktop grid before the first cursor-addressed history bytes");
        using var second = Mirror("phone-two");
        var secondOutput = new ConcurrentQueue<string>();
        var secondTimeline = new ConcurrentQueue<string>();
        second.DataReceived += secondOutput.Enqueue;
        second.DataReceived += data => secondTimeline.Enqueue("data:" + data);
        second.TerminalSizeChanged += (cols, rows) => secondTimeline.Enqueue($"size:{cols}x{rows}");
        second.ErrorReceived += message => transportErrors.Enqueue("phone-two: " + message);
        second.Closed += _ => closedTransports.Enqueue("phone-two");
        await second.ConnectAsync();
        await Until(() => string.Concat(secondOutput) == history);
        Require(secondTimeline.First() == "size:160x45", "each new mirror receives geometry before its replay");
        Require(string.Concat(firstOutput) == history && string.Concat(desktop) == history,
            "a second phone's snapshot must not replay into the first phone or desktop");
        host.Emit("later");
        await Until(() => string.Concat(firstOutput) == history + "later" && string.Concat(secondOutput) == history + "later");
        var cached = "";
        await backend.ReplayAndBindAsync("desktop", data => cached = data, () => { }, default);
        Require(cached == history + "later", "desktop cache stays available while two phones are connected");
        Require(host.Requests.Count(p => p.GetProperty("type").GetString() == "attach") == 1,
            "mobile and repeated desktop attachment never send host Attach");

        var atomicOutput = new ConcurrentQueue<string>();
        var atomicBound = false;
        void Live(string id, string data) { if (id == "desktop" && atomicBound) atomicOutput.Enqueue(data); }
        backend.Data += Live;
        await backend.ReplayAndBindAsync("desktop", snapshot =>
        {
            atomicOutput.Enqueue(snapshot);
            host.Emit("racing"); // Arrives while the snapshot callback owns the cache lock.
        }, () => atomicBound = true, default);
        await Until(() => string.Concat(atomicOutput) == history + "laterracing");
        backend.Data -= Live;

        // Exercise the exact failing route: real mobile WebSocket -> gateway ->
        // borrowed GUI pipe. The fixture echoes bytes without executing a shell.
        var expectedOutput = history + "laterracing";
        foreach (var (phone, cols, rows, input) in new[]
        {
            (first, 40, 20, "телефон-1\r"),
            (second, 121, 39, "телефон-2\r")
        })
        {
            await phone.ResizeAsync(cols, rows).WaitAsync(TimeSpan.FromSeconds(2));
            await phone.WriteAsync(input).WaitAsync(TimeSpan.FromSeconds(2));
            expectedOutput += input;
            await Until(() => string.Concat(desktop) == expectedOutput
                && string.Concat(firstOutput) == expectedOutput && string.Concat(secondOutput) == expectedOutput);
            Require(!host.Requests.Any(packet => packet.GetProperty("type").GetString() == "resize"
                && packet.GetProperty("id").GetString() == "desktop"
                && packet.GetProperty("cols").GetInt32() == cols && packet.GetProperty("rows").GetInt32() == rows),
                "phone viewport changes must not resize the desktop PTY");
            Require(phone.TerminalColumns == 160 && phone.TerminalRows == 45,
                "a rejected mobile resize returns the actual terminal dimensions");
            Require(host.Requests.Count(packet => packet.GetProperty("type").GetString() == "write"
                && packet.GetProperty("id").GetString() == "desktop" && packet.GetProperty("data").GetString() == input) == 1,
                "each phone write must reach the shared desktop session exactly once");
        }
        Require(transportErrors.IsEmpty && closedTransports.IsEmpty
            && first.State == SshSessionState.Connected && second.State == SshSessionState.Connected && starts == 1,
            "write and resize must preserve the GUI and both phone connections without transport errors");
        Console.WriteLine("PASS both phones write through the occupied desktop pipe without changing its 160x45 grid");

        host.EchoResizes = true;
        backend.ResizeDesktop("desktop", 102, 33);
        const string repaint = "resize:102x33";
        expectedOutput += repaint;
        await Until(() => string.Concat(desktop) == expectedOutput
            && string.Concat(firstOutput) == expectedOutput && string.Concat(secondOutput) == expectedOutput);
        foreach (var timeline in new[] { firstTimeline, secondTimeline })
        {
            var events = timeline.ToArray();
            Require(Array.IndexOf(events, "size:102x33") >= 0
                && Array.IndexOf(events, "size:102x33") < Array.IndexOf(events, "data:" + repaint),
                "the actual PTY resize repaint must follow its geometry notification on each WebSocket");
        }
        Require(first.TerminalColumns == 102 && second.TerminalRows == 33,
            "a desktop window resize changes both mirror parser grids");
        host.EchoResizes = false;
        Console.WriteLine("PASS desktop geometry precedes replay and real host resize output on both mirrors");

        backend.SessionCreated("desktop-born", desktopAttached: true);
        host.Emit("new desktop output", "desktop-born");
        await Until(() => desktopEvents.Any(e => e.id == "desktop-born" && e.data == "new desktop output"));
        backend.Create("phone-born", 80, 24, null, null, null, null);
        backend.Resize("phone-born", 44, 28);
        Require(backend.GetGeometry("phone-born") == new GatewayTerminalGeometry(44, 28),
            "a new phone-only session can establish its own grid before the desktop attaches");
        host.Emit("new phone output", "phone-born");
        await Until(() => backendEvents.Any(e => e.id == "phone-born" && e.data == "new phone output"));
        Require(!desktopEvents.Any(e => e.id == "phone-born"), "phone-created sessions do not send output into unbound desktop terminals");
        await backend.AttachDesktopAsync("phone-born", 150, 40);
        backend.Resize("phone-born", 40, 20);
        Require(backend.GetGeometry("phone-born") == new GatewayTerminalGeometry(150, 40),
            "desktop attachment becomes authoritative for a formerly phone-only session");
        Require(desktopEvents.Count(e => e.id == "phone-born" && e.data == "new phone output") == 1,
            "a later desktop attachment to a phone-created session gets its cache once");
        Require(!host.Requests.Any(p => p.GetProperty("type").GetString() == "attach"
            && (p.GetProperty("id").GetString() is "desktop-born" or "phone-born")),
            "sessions observed from birth never need another host snapshot");

        var staleBound = false;
        var stale = backend.ReplayAndBindAsync("replaced", _ => { }, () => staleBound = true, default);
        backend.SessionCreated("replaced");
        try { await stale; throw new Exception("retired snapshot wait unexpectedly succeeded"); }
        catch (OperationCanceledException) { }
        Require(!staleBound, "replacing a pending session must not bind a stale snapshot");

        await LegacyGeometryCompatibility(url, backend, host);

        gateway.Dispose();
        backend.Dispose();
        var ownerOutput = new ConcurrentQueue<string>();
        client.Data += (id, data, _) => { if (id == "desktop") ownerOutput.Enqueue(data); };
        client.Write("desktop", "still-alive");
        await Until(() => ownerOutput.LastOrDefault() == "still-alive");
        Require(client.List().SequenceEqual(["desktop"]) && ownerOutput.Last() == "still-alive" && starts == 1,
            "disposing the gateway and backend must preserve the desktop client and host");
        await OrphanGatewayFirst();
        await BackpressureDoesNotBlockReplay();
    }

    private static async Task BackpressureDoesNotBlockReplay()
    {
        static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        using var host = new PrivateHost(holdReads: true);
        var client = new SessionClient(host.Name, () => { });
        using var backend = new SessionClientBackend(client);
        Require(client.Ensure(), "backpressure fixture connects only to its private pipe");
        await host.Ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        backend.SessionCreated("busy", desktopAttached: true);
        long received = 0;
        backend.DesktopData += (id, data, _) => { if (id == "busy") Interlocked.Add(ref received, data.Length); };

        var inputStarted = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var input = Task.Run(() =>
        {
            inputStarted.SetResult(true);
            // A real paste fills the command pipe while the host is restoring
            // history and not reading commands. SessionClient writes 4K chunks.
            client.Write("busy", new string('p', 512_000));
        });
        Task attaching = Task.CompletedTask;
        Task replay = Task.CompletedTask;
        try
        {
            await inputStarted.Task;
            await Task.Delay(100);
            Require(!input.IsCompleted && host.Requests.IsEmpty, "command writer must be backpressured before resize");
            attaching = Task.Run(() => backend.AttachDesktopAsync("busy", 160, 45));
            // This is the restored-attachment resize path that used to hold
            // the cache lock while waiting for the occupied command writer.
            await attaching.WaitAsync(TimeSpan.FromSeconds(5));
            replay = Task.Run(() =>
            {
                host.Emit(new string('A', 1_200_000), "busy");
                host.Emit(new string('B', 1_200_000), "busy");
            });
            await replay.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => Interlocked.Read(ref received) == 2_400_000);
            Require(!input.IsCompleted && host.Requests.IsEmpty,
                "2.4MB of replay must drain while the command writer is still blocked");
            host.ReleaseReads.TrySetResult(true);
            await input.WaitAsync(TimeSpan.FromSeconds(5));
            await Until(() => host.Requests.Any(packet => packet.GetProperty("type").GetString() == "resize"
                && packet.GetProperty("id").GetString() == "busy"
                && packet.GetProperty("cols").GetInt32() == 160 && packet.GetProperty("rows").GetInt32() == 45));
            Console.WriteLine("PASS a backpressured command pipe never blocks 2.4MB replay while desktop geometry is queued");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"FAIL backpressure regression: {ex.GetType().Name}: {ex.Message}");
            throw;
        }
        finally
        {
            // Closing the private pipe also releases an old implementation's
            // deadlock, so a failing regression cannot hang the test process.
            host.ReleaseReads.TrySetResult(true);
            host.Dispose();
            try { await Task.WhenAll(input, attaching, replay).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
            // The deliberately aborted private pipe can make StreamWriter's
            // final flush fail. Preserve the test's original failure instead.
            try { client.Dispose(); } catch (IOException) { }
        }
    }

    private static async Task LegacyGeometryCompatibility(string url, SessionClientBackend backend, PrivateHost host)
    {
        using var socket = new ClientWebSocket();
        socket.Options.SetRequestHeader("Authorization", "Bearer test-token");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await socket.ConnectAsync(new Uri(url), timeout.Token);
        // The old APK does not advertise terminalGeometry and treats unknown
        // JSON control frames as terminal text. Exercise that exact handshake.
        var hello = Encoding.UTF8.GetBytes(GatewayProtocol.ConnectHandshake("desktop", false, 40, 20, null));
        await socket.SendAsync(hello, WebSocketMessageType.Text, true, timeout.Token);
        async Task<string?> NextType()
        {
            using var body = new MemoryStream();
            var bytes = new byte[8192];
            WebSocketReceiveResult received;
            do
            {
                received = await socket.ReceiveAsync(bytes, timeout.Token);
                body.Write(bytes, 0, received.Count);
            } while (!received.EndOfMessage);
            using var packet = JsonDocument.Parse(body.ToArray());
            return packet.RootElement.GetProperty("type").GetString();
        }
        var sawData = false;
        while (true)
        {
            var type = await NextType();
            if (type == GatewayProtocol.Geometry) throw new Exception("old APK received unsupported geometry JSON");
            if (type == GatewayProtocol.Data) sawData = true;
            if (type == GatewayProtocol.Attached) break;
        }
        if (!sawData) throw new Exception("old APK must still receive history");
        host.EchoResizes = true;
        backend.ResizeDesktop("desktop", 146, 36);
        if (await NextType() != GatewayProtocol.Data)
            throw new Exception("old APK received geometry instead of terminal repaint bytes");
        host.EchoResizes = false;
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", timeout.Token);
        Console.WriteLine("PASS old mobile clients receive output without unsupported geometry frames");
    }

    private static async Task OrphanGatewayFirst()
    {
        static void Require(bool ok, string message) { if (!ok) throw new Exception(message); }
        using var host = new PrivateHost();
        using var client = new SessionClient(host.Name, () => { });
        using var backend = new SessionClientBackend(client);
        Require(client.Ensure(), "the orphan session uses an existing shared pipe");
        var desktop = new ConcurrentQueue<string>();
        backend.DesktopData += (id, data, _) => { if (id == "desktop") desktop.Enqueue(data); };
        using var reserve = new TcpListener(IPAddress.Loopback, 0);
        reserve.Start(); var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        using var gateway = new GatewayServer(backend, port, "orphan-token");
        gateway.Start();
        var url = $"ws://127.0.0.1:{port}";
        using (var control = new GatewayControlClient(url, "orphan-token"))
            Require((await control.ListAsync()).SequenceEqual(["desktop"]), "the host lists a session absent from desktop restoration");
        GatewaySshSession Mirror(string id) => new(id, new SshConnectionOptions
        {
            Host = "unused", Username = "unused", Password = "unused", GatewayUrl = url,
            GatewayToken = "orphan-token", MirrorSessionId = "desktop", AutoReconnect = false
        });
        using var first = Mirror("orphan-phone-one");
        var firstOutput = new ConcurrentQueue<string>();
        var errors = new ConcurrentQueue<string>();
        first.DataReceived += firstOutput.Enqueue;
        first.ErrorReceived += errors.Enqueue;
        var desktopJoin = Task.CompletedTask;
        try
        {
            await first.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
            await host.AttachSeen.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Require(desktop.IsEmpty && firstOutput.IsEmpty, "a pending orphan snapshot stays private until it is complete");
            desktopJoin = backend.AttachDesktopAsync("desktop");
            Require(!desktopJoin.IsCompleted && ReferenceEquals(desktopJoin, backend.AttachDesktopAsync("desktop")),
                "desktop joins an in-progress mobile seed without starting another host attachment");
        }
        finally { host.ReleaseAttach.TrySetResult(true); }
        await desktopJoin.WaitAsync(TimeSpan.FromSeconds(5));
        const string history = "history\alive-during-replay";
        await Until(() => string.Concat(firstOutput) == history && string.Concat(desktop) == history);
        using var second = Mirror("orphan-phone-two");
        var secondOutput = new ConcurrentQueue<string>();
        second.DataReceived += secondOutput.Enqueue;
        second.ErrorReceived += errors.Enqueue;
        await second.ConnectAsync().WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => string.Concat(secondOutput) == history);
        Require(string.Concat(firstOutput) == history && string.Concat(desktop) == history,
            "later orphan subscribers receive history without replaying it into existing views");
        host.Emit("after-orphan");
        await Until(() => string.Concat(firstOutput) == history + "after-orphan"
            && string.Concat(secondOutput) == history + "after-orphan" && string.Concat(desktop) == history + "after-orphan");
        while (desktop.TryDequeue(out _)) { }
        await backend.AttachDesktopAsync("desktop");
        Require(string.Concat(desktop) == history + "after-orphan" && string.Concat(firstOutput) == history + "after-orphan"
            && string.Concat(secondOutput) == history + "after-orphan", "restored desktop replay is addressed only to the desktop");
        Require(host.Requests.Count(p => p.GetProperty("type").GetString() == "attach") == 1 && errors.IsEmpty,
            "gateway-first orphan restoration uses exactly one host attachment without transport errors");
        Console.WriteLine("PASS orphan gateway-first snapshot and desktop joining during replay preserve one copy of all output");
    }

    private static async Task Until(Func<bool> predicate)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!predicate()) await Task.Delay(10, timeout.Token);
    }

    private sealed class PrivateHost : IDisposable
    {
        public string Name { get; } = "TerminalV.SharedGateway.Test." + Guid.NewGuid().ToString("N");
        public ConcurrentQueue<JsonElement> Requests { get; } = new();
        public TaskCompletionSource<bool> AttachSeen { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseAttach { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> Ready { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource<bool> ReleaseReads { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool EchoResizes { get; set; }
        private readonly NamedPipeServerStream _pipe;
        private readonly Task _serve;
        private readonly object _writeGate = new();
        private StreamWriter? _writer;

        public PrivateHost(bool holdReads = false)
        {
            if (!holdReads) ReleaseReads.SetResult(true);
            _pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous,
                inBufferSize: 4096, outBufferSize: 4096);
            _serve = Task.Run(async () =>
            {
                try
                {
                    await _pipe.WaitForConnectionAsync();
                    using var reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                    using var writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                    lock (_writeGate) _writer = writer;
                    Ready.TrySetResult(true);
                    await ReleaseReads.Task;
                    while (await reader.ReadLineAsync() is { } line)
                    {
                        using var doc = JsonDocument.Parse(line);
                        var packet = doc.RootElement.Clone();
                        Requests.Enqueue(packet);
                        switch (packet.GetProperty("type").GetString())
                        {
                            case "list": Send(new { type = "list", ids = new[] { "desktop" } }); break;
                            case "attach":
                                AttachSeen.TrySetResult(true);
                                await ReleaseAttach.Task;
                                var id = packet.GetProperty("id").GetString()!;
                                Emit("history\a", id); Emit("live-during-replay", id);
                                break;
                            case "write": Emit(packet.GetProperty("data").GetString()!); break;
                            case "resize":
                                if (EchoResizes) Emit($"resize:{packet.GetProperty("cols").GetInt32()}x{packet.GetProperty("rows").GetInt32()}",
                                    packet.GetProperty("id").GetString()!);
                                break;
                        }
                    }
                }
                catch (IOException) { }
                catch (ObjectDisposedException) { }
            });
        }
        public void Emit(string data, string id = "desktop") => Send(new { type = "data", id, data });
        private void Send(object packet)
        {
            lock (_writeGate) _writer!.WriteLine(JsonSerializer.Serialize(packet));
        }
        public void Dispose()
        {
            ReleaseAttach.TrySetResult(true);
            ReleaseReads.TrySetResult(true);
            _pipe.Dispose();
            try { _serve.Wait(TimeSpan.FromSeconds(5)); } catch { }
        }
    }
}
