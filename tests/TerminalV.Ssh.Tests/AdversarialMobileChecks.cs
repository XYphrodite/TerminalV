using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using TerminalV.Gateway;
using TerminalV.Ssh;

internal static class AdversarialMobileChecks
{
    public static async Task Run()
    {
        void Check(bool condition, string message) { if (!condition) throw new Exception(message); }

        Console.WriteLine("--- ADVERSARIAL CHALLENGE 1: Extension Sanitization ---");
        
        // 1. Path Traversal Attacks
        Check(SshFileUploader.SanitizeExtension("../../etc/passwd") == ".bin", "path traversal dotless falls back to .bin");
        Check(SshFileUploader.SanitizeExtension("..\\..\\evil.webp") == ".webp", "windows traversal stripped to .webp");
        Check(SshFileUploader.SanitizeExtension("../") == ".bin", "slash only falls back to .bin");
        Check(SshFileUploader.SanitizeExtension("..\\") == ".bin", "backslash only falls back to .bin");
        Check(SshFileUploader.SanitizeExtension(".") == ".bin", "single dot falls back to .bin");
        Check(SshFileUploader.SanitizeExtension("..") == ".bin", "double dot falls back to .bin");
        Check(SshFileUploader.SanitizeExtension("...") == ".bin", "triple dot falls back to .bin");
        Check(SshFileUploader.SanitizeExtension("/etc/shadow") == ".bin", "posix path without dot falls back to .bin");
        Check(SshFileUploader.SanitizeExtension(@"C:\Windows\System32\cmd.exe") == ".exe", "windows absolute path stripped to .exe");
        Check(SshFileUploader.SanitizeExtension("C:/test/file.tar.gz") == ".gz", "multi-extension takes final .gz");
        Check(SshFileUploader.SanitizeExtension("../../../boot.ini") == ".ini", "relative traversal stripped to .ini");
        Check(SshFileUploader.SanitizeExtension("evil.png/") == ".bin", "trailing slash invalidates extension");
        Check(SshFileUploader.SanitizeExtension("evil.png\\") == ".bin", "trailing backslash invalidates extension");
        Check(SshFileUploader.SanitizeExtension("foo.bar/baz") == ".bin", "slash after dot invalidates extension");
        Check(SshFileUploader.SanitizeExtension("foo.bar\\baz") == ".bin", "backslash after dot invalidates extension");

        // 2. Shell Metacharacters and Control Injections
        Check(SshFileUploader.SanitizeExtension("photo.png;rm -rf /") == ".bin", "semicolon command injection rejected");
        Check(SshFileUploader.SanitizeExtension("photo.png|whoami") == ".bin", "pipe command injection rejected");
        Check(SshFileUploader.SanitizeExtension("photo.png&calc") == ".bin", "ampersand command injection rejected");
        Check(SshFileUploader.SanitizeExtension("photo.png$PATH") == ".bin", "env var expansion rejected");
        Check(SshFileUploader.SanitizeExtension("photo.png`id`") == ".bin", "backtick injection rejected");
        Check(SshFileUploader.SanitizeExtension("photo.png'test") == ".bin", "single quote rejected");
        Check(SshFileUploader.SanitizeExtension("photo.png\"test") == ".bin", "double quote rejected");
        Check(SshFileUploader.SanitizeExtension("photo.png<script>") == ".bin", "angle brackets rejected");
        Check(SshFileUploader.SanitizeExtension("photo.png\0") == ".bin", "null byte in extension rejected to .bin");
        Check(SshFileUploader.SanitizeExtension("photo\0.jpg") == ".jpg", "null byte in base filename stripped, safe extension preserved");
        Check(SshFileUploader.SanitizeExtension("photo.png\r\n") == ".png", "trailing CRLF safely trimmed");
        Check(SshFileUploader.SanitizeExtension("photo.p\rng") == ".bin", "embedded CR in extension rejected to .bin");
        Check(SshFileUploader.SanitizeExtension("photo.p\nng") == ".bin", "embedded LF in extension rejected to .bin");
        Check(SshFileUploader.SanitizeExtension("photo.p\tng") == ".bin", "embedded tab in extension rejected to .bin");
        Check(SshFileUploader.SanitizeExtension("photo.p ng") == ".bin", "embedded space in extension rejected to .bin");
        Check(SshFileUploader.SanitizeExtension(" photo.png ") == ".png", "surrounding filename space trimmed");
        Check(SshFileUploader.SanitizeExtension("photo.png!") == ".bin", "exclamation rejected");
        Check(SshFileUploader.SanitizeExtension("photo.png?") == ".bin", "question mark rejected");
        Check(SshFileUploader.SanitizeExtension("photo.png*") == ".bin", "asterisk wildcard rejected");
        Check(SshFileUploader.SanitizeExtension("photo.png[1]") == ".bin", "brackets rejected");
        Check(SshFileUploader.SanitizeExtension("photo.png(1)") == ".bin", "parentheses rejected");

        // 3. Uppercase and Mixed-Case Normalization
        Check(SshFileUploader.SanitizeExtension("PHOTO.PNG") == ".png", "all uppercase normalized to lowercase");
        Check(SshFileUploader.SanitizeExtension("image.JpEg") == ".jpeg", "mixed case normalized to lowercase");
        Check(SshFileUploader.SanitizeExtension("doc.TxT") == ".txt", "mixed case txt normalized");
        Check(SshFileUploader.SanitizeExtension("ANIMATION.WEBP") == ".webp", "all uppercase webp normalized");

        // 4. Non-ASCII, Cyrillic and International Characters
        Check(SshFileUploader.SanitizeExtension("тест-снимок.png") == ".png", "Cyrillic base keeps ASCII .png");
        Check(SshFileUploader.SanitizeExtension("тест.рф") == ".bin", "Cyrillic extension .рф falls back to .bin");
        Check(SshFileUploader.SanitizeExtension("картинка.ЖПГ") == ".bin", "Cyrillic extension .ЖПГ falls back to .bin");
        Check(SshFileUploader.SanitizeExtension("документ.докс") == ".bin", "Cyrillic extension .докс falls back to .bin");
        Check(SshFileUploader.SanitizeExtension("file.📁") == ".bin", "emoji extension falls back to .bin");
        Check(SshFileUploader.SanitizeExtension("file.🎉") == ".bin", "emoji extension falls back to .bin");
        Check(SshFileUploader.SanitizeExtension("file.中文") == ".bin", "CJK extension falls back to .bin");
        Check(SshFileUploader.SanitizeExtension("file.العربية") == ".bin", "Arabic extension falls back to .bin");
        Check(SshFileUploader.SanitizeExtension("file.png\u00A0") == ".png", "trailing non-breaking space safely trimmed");
        Check(SshFileUploader.SanitizeExtension("file.p\u00A0ng") == ".bin", "embedded non-breaking space in extension rejected to .bin");

        // 5. Overlong Extension Boundary (12 character limit)
        Check(SshFileUploader.SanitizeExtension("file.abcdefghijk") == ".abcdefghijk", "11 alphanumeric chars accepted");
        Check(SshFileUploader.SanitizeExtension("file.abcdefghijkl") == ".abcdefghijkl", "12 alphanumeric chars accepted");
        Check(SshFileUploader.SanitizeExtension("file.abcdefghijklm") == ".bin", "13 alphanumeric chars rejected to .bin");
        Check(SshFileUploader.SanitizeExtension("file.thirteenchars") == ".bin", "13 char english rejected to .bin");
        Check(SshFileUploader.SanitizeExtension("file.verylongextensionnamethatshouldnotfit") == ".bin", "42 chars rejected to .bin");
        Check(SshFileUploader.SanitizeExtension("file.") == ".bin", "empty extension rejected to .bin");
        Check(SshFileUploader.SanitizeExtension("file") == ".bin", "no extension rejected to .bin");
        Check(SshFileUploader.SanitizeExtension("") == ".bin", "empty string rejected to .bin");
        Check(SshFileUploader.SanitizeExtension("   ") == ".bin", "whitespace rejected to .bin");
        Check(SshFileUploader.SanitizeExtension(null) == ".bin", "null rejected to .bin");
        Check(SshFileUploader.SanitizeExtension(".gitignore") == ".gitignore", "dotfile treated as .<name>");
        Check(SshFileUploader.SanitizeExtension("..gitignore") == ".gitignore", "double dot file treated as .<name>");

        Console.WriteLine("--- ADVERSARIAL CHALLENGE 2: FormatShellPath ---");
        Check(SshFileUploader.FormatShellPath("/C:/Users/Gamer/test.png") == @"C:\Users\Gamer\test.png", "windows drive root converted");
        Check(SshFileUploader.FormatShellPath("/D:/Data/file.txt") == @"D:\Data\file.txt", "d drive root converted");
        Check(SshFileUploader.FormatShellPath("/tmp/TerminalV-pastes/paste.png") == "/tmp/TerminalV-pastes/paste.png", "posix tmp preserved");
        Check(SshFileUploader.FormatShellPath("") == "", "empty path preserved");

        Console.WriteLine("--- ADVERSARIAL CHALLENGE 3: Gateway Boundaries and Cleanup ---");
        var uploadDir = Path.Combine(Path.GetTempPath(), "terminalv-adversarial-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(uploadDir);
        try
        {
            using var reserve = new TcpListener(IPAddress.Loopback, 0);
            reserve.Start();
            var port = ((IPEndPoint)reserve.LocalEndpoint).Port;
            reserve.Stop();

            const int maxBytes = 32768; // 32 KB threshold for test speed
            using var server = new GatewayServer(
                new FakeGatewayBackend(new[] { "s1" }),
                port,
                token: "adv-token",
                uploadDirectory: uploadDir,
                maxUploadBytes: maxBytes,
                loopbackOnly: true);
            server.Start();

            // 1. Content-Length Over Boundary: Server returns HTTP 413, ZERO files created on disk
            using (var http = new HttpClient())
            {
                using var req = new HttpRequestMessage(HttpMethod.Post, $"http://localhost:{port}/terminalv/upload?name=test.png");
                req.Headers.TryAddWithoutValidation("Authorization", "Bearer adv-token");
                req.Headers.TryAddWithoutValidation("X-TerminalV-Upload", "1");
                req.Content = new ByteArrayContent(new byte[maxBytes + 1024]);
                using var resp = await http.SendAsync(req);
                Check(resp.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Content-Length over limit returns 413");
                Check(Directory.GetFiles(uploadDir).Length == 0, "No file created on 413 Content-Length rejection");
            }

            // 2. Streaming Chunked Upload Over Boundary: Server returns HTTP 413, PARTIAL FILE DELETED
            using (var client = new GatewayControlClient($"ws://localhost:{port}", gatewayToken: "adv-token"))
            await using (var oversizedStream = new MemoryStream(new byte[maxBytes + 4096], writable: false))
            {
                var caughtTooLarge = false;
                try
                {
                    await client.UploadAsync(oversizedStream, "chunked-oversized.png");
                }
                catch (InvalidOperationException ex) when (ex.Message.Contains("большой", StringComparison.OrdinalIgnoreCase))
                {
                    caughtTooLarge = true;
                }
                Check(caughtTooLarge, "Oversized stream rejected with 413 message");
                Check(Directory.GetFiles(uploadDir).Length == 0, "Partial file cleaned up after 413 streaming rejection");
            }

            // 3. Aborted / Canceled Upload Mid-Stream: PARTIAL FILE DELETED
            using (var client = new GatewayControlClient($"ws://localhost:{port}", gatewayToken: "adv-token"))
            {
                using var cts = new CancellationTokenSource();
                // Custom slow stream that cancels after producing partial bytes
                var partialStream = new AbortingStream(totalBytes: 16384, abortAtBytes: 8192, cts);
                var caughtCanceled = false;
                try
                {
                    await client.UploadAsync(partialStream, "aborted.png", cancellationToken: cts.Token);
                }
                catch (OperationCanceledException)
                {
                    caughtCanceled = true;
                }
                catch (InvalidOperationException)
                {
                    caughtCanceled = true;
                }
                Check(caughtCanceled, "Upload was aborted mid-stream as expected");

                // Verify that within 500ms, no orphaned file remains in uploadDir
                var cleaned = false;
                for (var i = 0; i < 10; i++)
                {
                    if (Directory.GetFiles(uploadDir).Length == 0) { cleaned = true; break; }
                    await Task.Delay(50);
                }
                Check(cleaned, "Partial file was cleaned up on aborted/canceled stream");
            }

            // 4. Malicious query parameter traversal: must be safely trapped in uploadDir
            using (var client = new GatewayControlClient($"ws://localhost:{port}", gatewayToken: "adv-token"))
            using (var ms = new MemoryStream(new byte[128], writable: false))
            {
                var maliciousName = "../../../../windows/system32/calc.exe;rm -rf /";
                var remotePath = await client.UploadAsync(ms, maliciousName);
                Check(remotePath.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase), "Path traversal safely trapped in uploadDir");
                Check(Path.GetExtension(remotePath) == ".bin", "Metacharacters in traversal fallback to .bin");
                Check(Path.GetFileName(remotePath).StartsWith("paste-", StringComparison.Ordinal), "Uses paste-<guid> prefix");
                Check(File.Exists(remotePath), "File was written safely");
                File.Delete(remotePath);
            }

            // 5. Cyrillic query parameter upload: must preserve .png
            using (var client = new GatewayControlClient($"ws://localhost:{port}", gatewayToken: "adv-token"))
            using (var ms = new MemoryStream(new byte[128], writable: false))
            {
                var cyrillicName = "тест-снимок.png";
                var remotePath = await client.UploadAsync(ms, cyrillicName);
                Check(remotePath.StartsWith(uploadDir, StringComparison.OrdinalIgnoreCase), "Saved in uploadDir");
                Check(Path.GetExtension(remotePath) == ".png", "Preserved .png extension for Cyrillic filename");
                Check(File.Exists(remotePath), "File was written");
                File.Delete(remotePath);
            }

            Console.WriteLine("--- ADVERSARIAL ALL CHECKS PASSED ---");
        }
        finally
        {
            try { if (Directory.Exists(uploadDir)) Directory.Delete(uploadDir, recursive: true); } catch { }
        }
    }

    private sealed class AbortingStream(int totalBytes, int abortAtBytes, CancellationTokenSource cts) : Stream
    {
        private int _position;
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => totalBytes;
        public override long Position { get => _position; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            if (_position >= abortAtBytes)
            {
                cts.Cancel();
                throw new OperationCanceledException("Stream aborted deliberately by test harness.");
            }
            var toRead = Math.Min(count, Math.Min(4096, totalBytes - _position));
            if (toRead <= 0) return 0;
            Array.Clear(buffer, offset, toRead);
            _position += toRead;
            await Task.Delay(10, cancellationToken);
            return toRead;
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            ReadAsync(buffer, offset, count, CancellationToken.None).GetAwaiter().GetResult();
    }
}
