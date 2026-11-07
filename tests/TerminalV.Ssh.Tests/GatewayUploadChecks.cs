using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using TerminalV.Gateway;
using TerminalV.Ssh;

internal static class GatewayUploadChecks
{
    public static async Task Run()
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

        Check(SshFileUploader.SanitizeExtension("IMG_2026.PNG") == ".png", "uppercase extension normalized");
        Check(SshFileUploader.SanitizeExtension("Снимок экрана 2026.jpeg") == ".jpeg", "UTF-8 filename keeps extension");
        Check(SshFileUploader.SanitizeExtension("../../etc/passwd") == ".bin", "dotless traversal falls back to .bin");
        Check(SshFileUploader.SanitizeExtension("..\\..\\evil.webp") == ".webp", "path components stripped safely");
        Check(SshFileUploader.SanitizeExtension("photo.png; rm -rf /") == ".bin", "shell metacharacters rejected");
        Check(SshFileUploader.SanitizeExtension("archive.verylongextensionname") == ".bin", "overlong extension rejected");
        Check(SshFileUploader.SanitizeExtension("") == ".bin", "empty filename falls back to .bin");
        Check(SshFileUploader.SanitizeExtension(null) == ".bin", "null filename falls back to .bin");
        Check(SshFileUploader.FormatShellPath("/C:/Users/Gamer/.terminalv-pastes/paste-1.png") == @"C:\Users\Gamer\.terminalv-pastes\paste-1.png",
            "Windows OpenSSH SFTP path converted");
        Check(SshFileUploader.FormatShellPath("/tmp/TerminalV-pastes/paste-1.png") == "/tmp/TerminalV-pastes/paste-1.png",
            "POSIX SFTP path preserved");

        var uploadDir = Path.Combine(Path.GetTempPath(), "terminalv-upload-test-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(uploadDir);
        try
        {
            using var reserve = new TcpListener(IPAddress.Loopback, 0);
            reserve.Start();
            var port = ((IPEndPoint)reserve.LocalEndpoint).Port;
            reserve.Stop();

            var identity = new TailnetIdentity("node-mobile", "phone", "u1", "user@example.test");
            var fakeTailnet = new FakeTailnet(identity, approved: true);
            using var server = new GatewayServer(
                new FakeGatewayBackend(new[] { "s1" }),
                port,
                token: "upload-secret",
                tailnet: fakeTailnet,
                uploadDirectory: uploadDir,
                maxUploadBytes: 4096,
                loopbackOnly: true);
            server.Start();

            var payload = new byte[1536];
            for (var i = 0; i < payload.Length; i++)
                payload[i] = (byte)((i * 31 + 7) & 0xFF);

            // 1. Happy path with Bearer token + UTF-8 filename
            using (var client = new GatewayControlClient($"ws://localhost:{port}", gatewayToken: "upload-secret"))
            await using (var ms = new MemoryStream(payload, writable: false))
            {
                var remotePath = await client.UploadAsync(ms, "Снимок экрана.PNG");
                Check(remotePath.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase), "saved inside uploadDir");
                Check(Path.GetExtension(remotePath) == ".png", "preserved .png extension");
                Check(Path.GetFileName(remotePath).StartsWith("paste-", StringComparison.Ordinal), "uses paste-<guid> prefix");
                var onDisk = await File.ReadAllBytesAsync(remotePath);
                Check(onDisk.SequenceEqual(payload), "binary payload matches byte-for-byte");
                File.Delete(remotePath);
            }

            // 2. Unauthorized token rejected without writing any file
            using (var badClient = new GatewayControlClient($"ws://localhost:{port}", gatewayToken: "wrong-token"))
            await using (var ms = new MemoryStream(payload, writable: false))
            {
                var rejected = false;
                try { await badClient.UploadAsync(ms, "photo.jpg"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("отклонил", StringComparison.OrdinalIgnoreCase))
                {
                    rejected = true;
                }
                Check(rejected, "wrong token rejected with clear message");
                Check(Directory.GetFiles(uploadDir).Length == 0, "no file written on 401");
            }

            // 3. Missing X-TerminalV-Upload CSRF guard header rejected
            using (var http = new HttpClient())
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/terminalv/upload?name=test.png");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer upload-secret");
                req.Content = new ByteArrayContent(payload);
                using var resp = await http.SendAsync(req);
                Check(resp.StatusCode == HttpStatusCode.BadRequest, "missing X-TerminalV-Upload rejected with 400");
                Check(Directory.GetFiles(uploadDir).Length == 0, "no file written on missing CSRF header");
            }

            // 4. Oversized upload rejected with 413 and partial file cleaned up
            using (var client = new GatewayControlClient($"ws://localhost:{port}", gatewayToken: "upload-secret"))
            await using (var huge = new MemoryStream(new byte[8192], writable: false))
            {
                var tooLarge = false;
                try { await client.UploadAsync(huge, "huge.jpg"); }
                catch (InvalidOperationException ex) when (ex.Message.Contains("большой", StringComparison.OrdinalIgnoreCase))
                {
                    tooLarge = true;
                }
                Check(tooLarge, "oversized payload rejected with 413 message");
                Check(Directory.GetFiles(uploadDir).Length == 0, "partial oversized file deleted");
            }

            // 5. Tailnet identity + custom dialer (Android tsnet.aar path)
            var dialed = 0;
            using (var tailClient = new GatewayControlClient(
                $"ws://localhost:{port}",
                tailnetIdentity: true,
                dial: async (_, targetPort, ct) =>
                {
                    Interlocked.Increment(ref dialed);
                    var tcp = new TcpClient();
                    await tcp.ConnectAsync(IPAddress.Loopback, targetPort, ct);
                    return new NetworkStream(tcp.Client, ownsSocket: true);
                }))
            await using (var ms = new MemoryStream(payload, writable: false))
            {
                var remotePath = await tailClient.UploadAsync(ms, "../camera/IMG_001.JPG");
                Check(dialed >= 1, "custom dialer used for upload");
                Check(Path.GetExtension(remotePath) == ".jpg", "sanitized .jpg extension");
                Check((await File.ReadAllBytesAsync(remotePath)).SequenceEqual(payload), "tailnet upload bytes match");
                File.Delete(remotePath);
            }
        }
        finally
        {
            try { if (Directory.Exists(uploadDir)) Directory.Delete(uploadDir, recursive: true); } catch { }
        }
    }

    private sealed class FakeTailnet(TailnetIdentity identity, bool approved) : ITailnetAccess
    {
        public Task<TailnetIdentity?> IdentifyAsync(IPEndPoint remote, IPEndPoint local, CancellationToken ct) =>
            Task.FromResult<TailnetIdentity?>(identity);
        public bool IsApproved(TailnetIdentity candidate) => approved && candidate.NodeId == identity.NodeId;
        public Task<bool> ApproveAsync(TailnetIdentity candidate, CancellationToken ct) => Task.FromResult(approved);
        public void RevokeAll() { }
    }
}
