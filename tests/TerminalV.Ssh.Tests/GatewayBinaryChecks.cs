using System.IO.Pipes;
using System.Net;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using TerminalV.Gateway;
using TerminalV.Host;
using TerminalV.Ssh;

internal static class GatewayBinaryChecks
{
    public static async Task Run()
    {
        static void Require(bool ok, string msg) { if (!ok) throw new Exception(msg); }

        // 1. Handshake roundtrip for binaryData flag
        var hs = GatewayProtocol.ConnectHandshake("desk1", control: false, cols: 80, rows: 24, term: null, binaryData: true, terminalGeometry: true);
        if (!GatewayProtocol.TryParseHandshake(hs, out var parsed) || parsed is null || !parsed.BinaryData || !parsed.TerminalGeometry)
            throw new Exception("binaryData+geometry handshake must roundtrip");
        var hs2 = GatewayProtocol.ConnectHandshake("desk1", false, 80, 24, null, binaryData: false);
        if (!GatewayProtocol.TryParseHandshake(hs2, out var parsed2) || parsed2 is null || parsed2.BinaryData)
            throw new Exception("binaryData=false must not set flag");
        // Legacy handshake without binaryData still parses
        var legacy = """{"type":"connect","cols":80,"rows":24,"term":"xterm-256color"}""";
        if (!GatewayProtocol.TryParseHandshake(legacy, out var leg) || leg is null || leg.BinaryData)
            throw new Exception("legacy handshake must parse without binaryData");

        // 2. Binary S->C path: history + live data arrive as WebSocket Binary frames
        using var host = new PrivateHost();
        var starts = 0;
        using var client = new SessionClient(host.Name, () => Interlocked.Increment(ref starts));
        using var backend = new SessionClientBackend(client);
        Require(client.Ensure(), "private pipe must connect");
        await backend.AttachDesktopAsync("desk").WaitAsync(TimeSpan.FromSeconds(5));

        using var reserve = new TcpListener(IPAddress.Loopback, 0);
        reserve.Start(); var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        using var gateway = new GatewayServer(backend, port, "tok");
        gateway.Start();
        var url = $"ws://127.0.0.1:{port}";

        // 2a. Legacy client (no binaryData) receives JSON text data
        try
        {
            using (var legacySock = new ClientWebSocket())
            {
                legacySock.Options.SetRequestHeader("Authorization", "Bearer tok");
                await legacySock.ConnectAsync(new Uri(url), CancellationToken.None);
                var helloLegacy = Encoding.UTF8.GetBytes(GatewayProtocol.ConnectHandshake("desk", false, 80, 24, null, binaryData: false));
                await legacySock.SendAsync(helloLegacy, WebSocketMessageType.Text, true, CancellationToken.None);
                var frame = await ReceiveAsync(legacySock, TimeSpan.FromSeconds(3));
                Require(frame is not null && !frame.Value.binary, "legacy client must receive text frame");
                var text = Encoding.UTF8.GetString(frame.Value.bytes);
                Require(text.Contains("\"type\":\"data\"") || text.Contains("\"data\""), "legacy data must be JSON");
                // Drain attached
                var attachedLegacy = await ReceiveUntilAsync(legacySock, GatewayProtocol.Attached, TimeSpan.FromSeconds(3));
                Require(attachedLegacy is not null, "legacy must get attached");
                try { await legacySock.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
                legacySock.Dispose();
            }
        }
        catch (Exception ex) { throw new Exception($"legacy client failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex); }

        // 2b. Binary client receives Binary frames for history and live
        try
        {
            using var binSock = new ClientWebSocket();
            binSock.Options.SetRequestHeader("Authorization", "Bearer tok");
            await binSock.ConnectAsync(new Uri(url), CancellationToken.None);
            var helloBin = Encoding.UTF8.GetBytes(GatewayProtocol.ConnectHandshake("desk", false, 80, 24, null, binaryData: true, terminalGeometry: true));
            await binSock.SendAsync(helloBin, WebSocketMessageType.Text, true, CancellationToken.None);

            // Geometry (text) precedes history binary when terminalGeometry is negotiated
            var geoFirst = await ReceiveAsync(binSock, TimeSpan.FromSeconds(3));
            Require(geoFirst is not null && !geoFirst.Value.binary, "first frame must be geometry text when terminalGeometry=true");
            var geoFirstText = Encoding.UTF8.GetString(geoFirst.Value.bytes);
            Require(geoFirstText.Contains("geometry"), $"first frame must be geometry, got: {geoFirstText}");

            // History binary
            var first = await ReceiveAsync(binSock, TimeSpan.FromSeconds(3));
            Require(first is not null, "binary client history frame is null (closed)");
            Require(first.Value.binary, $"binary client history must be Binary frame, not text (got binary={first.Value.binary} text={Encoding.UTF8.GetString(first.Value.bytes).Substring(0, Math.Min(200, first.Value.bytes.Length))})");
            var historyText = Encoding.UTF8.GetString(first.Value.bytes);
            Require(historyText == "history-data", $"binary history mismatch: {historyText}");

            var attached = await ReceiveUntilAsync(binSock, GatewayProtocol.Attached, TimeSpan.FromSeconds(3));
            Require(attached is not null, "binary client must receive attached after history");

            // Live data also binary
            host.Emit("live-binary");
            var live = await ReceiveAsync(binSock, TimeSpan.FromSeconds(3));
            Require(live is not null, "live frame null");
            Require(live.Value.binary, $"live data must be Binary when binaryData negotiated (binary={live.Value.binary})");
            Require(Encoding.UTF8.GetString(live.Value.bytes) == "live-binary", "live binary payload mismatch");

            // Control frames (geometry) remain text even in binary mode
            backend.ResizeDesktop("desk", 100, 40);
            var geo = await ReceiveAsync(binSock, TimeSpan.FromSeconds(3));
            Require(geo is not null, "geo frame null");
            Require(!geo.Value.binary, $"geometry must remain text even in binary mode (binary={geo.Value.binary})");
            var geoText = Encoding.UTF8.GetString(geo.Value.bytes);
            Require(geoText.Contains("\"geometry\"") || geoText.Contains("\"type\":\"geometry\""), "geometry frame must be JSON");

            try { await binSock.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "done", CancellationToken.None); } catch { }
            binSock.Dispose();
        }
        catch (Exception ex) { throw new Exception($"binary client failed: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}", ex); }

        // 2c. GatewaySshSession negotiates binaryData
        using var svc = new SshService();
        var opts = new SshConnectionOptions
        {
            Host = "", Username = "", Port = 22,
            GatewayUrl = url, GatewayToken = "tok",
            MirrorSessionId = "desk", ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        var sess = (GatewaySshSession)svc.Create("m-bin", opts);
        var received = new System.Collections.Concurrent.ConcurrentQueue<string>();
        sess.DataReceived += received.Enqueue;
        string? attachedId = null;
        sess.Attached += id => attachedId = id;
        await sess.ConnectAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (attachedId is null && DateTime.UtcNow < deadline) await Task.Delay(20);
        Require(attachedId == "desk", "GatewaySshSession must attach via binary handshake");
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (received.IsEmpty && DateTime.UtcNow < deadline) await Task.Delay(20);
        Require(!received.IsEmpty, "GatewaySshSession must receive binary history as data");
        if (received.Any(d => d.Contains("\"attached\"") || d.Contains("\"sessions\"")))
            throw new Exception("control leaked into terminal via binary path");

        host.Emit("via-session");
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (!received.Any(d => d.Contains("via-session")) && DateTime.UtcNow < deadline) await Task.Delay(20);
        Require(received.Any(d => d.Contains("via-session")), "live binary via GatewaySshSession must arrive");

        gateway.Stop();
        Console.WriteLine("PASS binary S->C path: handshake, history, live, geometry isolation, and GatewaySshSession negotiation");
    }

    private static async Task<(bool binary, byte[] bytes)?> ReceiveAsync(ClientWebSocket ws, TimeSpan timeout)
    {
        using var cts = new CancellationTokenSource(timeout);
        var buffer = new byte[65536];
        using var ms = new MemoryStream();
        bool binary = false;
        while (true)
        {
            var res = await ws.ReceiveAsync(buffer, cts.Token);
            if (res.MessageType == WebSocketMessageType.Close) return null;
            if (res.MessageType == WebSocketMessageType.Binary) binary = true;
            ms.Write(buffer, 0, res.Count);
            if (res.EndOfMessage) return (binary, ms.ToArray());
        }
    }

    private static async Task<(bool binary, byte[] bytes)?> ReceiveUntilAsync(ClientWebSocket ws, string type, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            var frame = await ReceiveAsync(ws, TimeSpan.FromSeconds(1));
            if (frame is null) return null;
            if (!frame.Value.binary)
            {
                var text = Encoding.UTF8.GetString(frame.Value.bytes);
                if (text.Contains($"\"{type}\"")) return frame;
            }
        }
        return null;
    }

    private sealed class PrivateHost : IDisposable
    {
        public string Name { get; } = "TerminalV.GatewayBinary.Test." + Guid.NewGuid().ToString("N");
        private readonly NamedPipeServerStream _pipe;
        private readonly Task _serve;
        private readonly object _gate = new();
        private StreamWriter? _writer;
        public PrivateHost()
        {
            _pipe = new NamedPipeServerStream(Name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
            _serve = Task.Run(async () =>
            {
                await _pipe.WaitForConnectionAsync();
                using var reader = new StreamReader(_pipe, Encoding.UTF8, false, 4096, leaveOpen: true);
                using var writer = new StreamWriter(_pipe, new UTF8Encoding(false), 4096, leaveOpen: true) { AutoFlush = true };
                lock (_gate) _writer = writer;
                // initial list + snapshot on attach
                while (await reader.ReadLineAsync() is { } line)
                {
                    using var doc = JsonDocument.Parse(line);
                    var t = doc.RootElement.GetProperty("type").GetString();
                    if (t == "list") Send(new { type = "list", ids = new[] { "desk" } });
                    else if (t == "attach")
                    {
                        Send(new { type = "data", id = "desk", data = "history-data" });
                    }
                    else if (t == "write") { /* ignore */ }
                    else if (t == "resize") { /* ignore */ }
                }
            });
        }
        public void Emit(string data)
        {
            Send(new { type = "data", id = "desk", data });
        }
        private void Send(object o)
        {
            lock (_gate) _writer!.WriteLine(JsonSerializer.Serialize(o));
        }
        public void Dispose()
        {
            _pipe.Dispose();
            try { _serve.Wait(500); } catch { }
        }
    }
}
