using System.Reflection;
using System.Text;
using TerminalV.Ssh;

internal static class SshStreamChecks
{
    private const BindingFlags Instance = BindingFlags.Instance | BindingFlags.NonPublic;

    public static async Task Run()
    {
        var api = typeof(Renci.SshNet.ShellStream).GetMethod("ChangeWindowSize",
            new[] { typeof(uint), typeof(uint), typeof(uint), typeof(uint) });
        if (api is null || !api.IsPublic)
            throw new Exception("Installed SSH.NET must expose public ShellStream.ChangeWindowSize.");

        foreach (var tsnet in new[] { false, true })
        {
            await CheckOutput(tsnet, "\x1b[32mПривет ┌─┐ 界 🙂\x1b[0m\r\n", oneByteReads: true);
            // The first read ends after the first byte of a three-byte glyph.
            await CheckOutput(tsnet, new string('a', 4095) + "界🙂конец", oneByteReads: false);
            await CheckResize(tsnet);
        }
    }

    private static SshSessionBase Create(bool tsnet, Stream stream)
    {
        var options = new SshConnectionOptions
        {
            Host = "localhost", Username = "test", Password = "test", AutoReconnect = false
        };
        options.Tailscale.Enabled = tsnet;
        SshSessionBase session = tsnet
            ? new TsnetSshSession("stream", options, new TailscaleStubConnector())
            : new SshNetSession("stream", options);
        session.GetType().GetField("_shellStream", Instance)!.SetValue(session, stream);
        typeof(SshSessionBase).GetProperty("State")!.SetValue(session, SshSessionState.Connected);
        return session;
    }

    private static async Task CheckOutput(bool tsnet, string expected, bool oneByteReads)
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var stream = new FragmentedStream(Encoding.UTF8.GetBytes(expected), oneByteReads, stop);
        using var session = Create(tsnet, stream);
        var output = new StringBuilder();
        session.DataReceived += chunk => output.Append(chunk);
        var read = (Task)session.GetType().GetMethod("ReadLoop", Instance)!
            .Invoke(session, new object[] { stop.Token })!;
        await read.WaitAsync(TimeSpan.FromSeconds(5));
        if (output.ToString() != expected)
            throw new Exception($"{session.GetType().Name} corrupted fragmented UTF-8 (oneByteReads={oneByteReads}).");
    }

    private static async Task CheckResize(bool tsnet)
    {
        using var stream = new ResizingStream();
        using var session = Create(tsnet, stream);
        await session.ResizeAsync(48, 32);
        await session.ResizeAsync(96, 18);
        await session.ResizeAsync(0, 2000);
        var expected = new[] { (48u, 32u, 0u, 0u), (96u, 18u, 0u, 0u), (1u, 1000u, 0u, 0u) };
        if (!stream.Sizes.SequenceEqual(expected))
            throw new Exception($"{session.GetType().Name} did not forward PTY dimensions through the public API.");
        stream.Fail = true;
        try
        {
            await session.ResizeAsync(50, 20);
            throw new Exception("A failed window change was silently ignored.");
        }
        catch (InvalidOperationException ex) when (ex.Message == "window change failed") { }
    }

    private sealed class FragmentedStream(byte[] data, bool oneByteReads, CancellationTokenSource stop) : MemoryStream(data)
    {
        public override int Read(byte[] buffer, int offset, int count)
        {
            var read = base.Read(buffer, offset, oneByteReads ? Math.Min(count, 1) : count);
            if (read == 0) stop.Cancel();
            return read;
        }
    }

    private sealed class ResizingStream : MemoryStream
    {
        public List<(uint, uint, uint, uint)> Sizes { get; } = [];
        public bool Fail { get; set; }
        public void ChangeWindowSize(uint columns, uint rows, uint width, uint height)
        {
            if (Fail) throw new InvalidOperationException("window change failed");
            Sizes.Add((columns, rows, width, height));
        }
    }
}
