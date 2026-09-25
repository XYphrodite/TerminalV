using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using TerminalV.Gateway;
using TerminalV.Ssh;

internal static class TailnetChecks
{
    public static async Task Run()
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }
        Check(!TailnetAccess.IsTailnet(IPAddress.Loopback), "loopback is not an identity");
        Check(!TailnetAccess.IsTailnet(IPAddress.Parse("192.168.1.1")), "LAN is not an identity");
        Check(!TailnetAccess.IsTailnet(IPAddress.Parse("100.128.0.1")), "outside CGN range");
        Check(TailnetAccess.IsTailnet(IPAddress.Parse("100.89.154.125")), "tailnet v4");
        Check(TailnetAccess.IsTailnet(IPAddress.Parse("fd7a:115c:a1e0::123")), "tailnet v6");
        var raw = """{"Node":{"StableID":"node-one","Name":"phone","Tags":[]},"UserProfile":{"ID":123,"LoginName":"test@example.test"}}""";
        var identity = TailnetAccess.ParseIdentity(JsonSerializer.Deserialize<JsonElement>(raw))!;
        Check(identity.NodeId == "node-one", "stable device identity");
        Check(TailnetAccess.ParseIdentity(JsonSerializer.Deserialize<JsonElement>(raw.Replace("\"Tags\":[]", "\"Tags\":[\"tag:server\"]"))) is null, "tagged devices have no human identity");
        var folder = Path.Combine(Path.GetTempPath(), "terminalv-tailnet-" + Guid.NewGuid());
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, "grants.json");
        try
        {
            var prompts = 0;
            var access = new TailnetAccess(path, (_, _) => { prompts++; return Task.FromResult(true); });
            Check(!access.IsApproved(identity), "new device denied");
            Check(await access.ApproveAsync(identity, default), "approval granted");
            Check(await access.ApproveAsync(identity, default) && prompts == 1, "approval only once");
            var restored = new TailnetAccess(path, (_, _) => Task.FromResult(false));
            Check(restored.IsApproved(identity), "grant persists");
            Check(!restored.IsApproved(identity with { NodeId = "node-two" }), "another device denied");
            Check(!restored.IsApproved(identity with { UserId = "456" }), "changed account denied");
            restored.RevokeAll();
            Check(!new TailnetAccess(path, (_, _) => Task.FromResult(false)).IsApproved(identity), "revocation persists");
        }
        finally { Directory.Delete(folder, true); }

        using var reserve = new TcpListener(IPAddress.Loopback, 0);
        reserve.Start(); var port = ((IPEndPoint)reserve.LocalEndpoint).Port; reserve.Stop();
        var fake = new Access(identity);
        using var server = new GatewayServer(new FakeGatewayBackend(new[] { "existing" }), port, "secret", fake);
        server.Start();
        var url = $"http://127.0.0.1:{port}";
        using var http = new HttpClient();
        using (var request = new HttpRequestMessage(HttpMethod.Get, url + "/terminalv/info"))
        {
            request.Headers.Add("Tailscale-User-Login", identity.Login);
            using var response = await http.SendAsync(request);
            Check(response.StatusCode == HttpStatusCode.Unauthorized, "spoofed identity header denied");
        }
        http.DefaultRequestHeaders.Add("Authorization", "Tailscale");
        using (var info = await http.GetAsync(url + "/terminalv/info"))
        {
            Check(info.IsSuccessStatusCode, "discovery allowed before approval");
            var body = await info.Content.ReadAsStringAsync();
            Check(!body.Contains("existing") && !body.Contains("secret"), "discovery never exposes sessions or token");
        }
        using (var client = new GatewayControlClient(url, tailnetIdentity: true))
        {
            try { await client.ListAsync(); throw new Exception("unpaired device admitted"); }
            catch (System.Net.WebSockets.WebSocketException) { }
        }
        using (var response = await http.PostAsync(url + "/terminalv/pair", null)) Check(response.IsSuccessStatusCode, "pair request approved");
        var dials = 0;
        async Task<Stream> Dial(string host, int targetPort, CancellationToken ct)
        {
            Interlocked.Increment(ref dials);
            var tcp = new TcpClient();
            await tcp.ConnectAsync(host, targetPort, ct);
            return new NetworkStream(tcp.Client, ownsSocket: true);
        }
        using var control = new GatewayControlClient(url, tailnetIdentity: true, dial: Dial);
        Check((await control.ListAsync()).Contains("existing") && dials == 1, "identity WebSocket over custom transport");
        fake.Approved = false;
        await Task.Delay(1300);
        try { await control.ListAsync(); throw new Exception("revoked client kept access"); }
        catch (Exception ex) when (ex is System.Net.WebSockets.WebSocketException or InvalidOperationException) { }
        Console.WriteLine("PASS tailnet identity, pairing, persistence, spoof rejection, transport and live revocation");
    }
    private sealed class Access(TailnetIdentity identity) : ITailnetAccess
    {
        public bool Approved;
        public Task<TailnetIdentity?> IdentifyAsync(IPEndPoint remote, IPEndPoint local, CancellationToken ct) => Task.FromResult<TailnetIdentity?>(identity);
        public bool IsApproved(TailnetIdentity value) => Approved;
        public Task<bool> ApproveAsync(TailnetIdentity value, CancellationToken ct) { Approved = true; return Task.FromResult(true); }
    }
}
