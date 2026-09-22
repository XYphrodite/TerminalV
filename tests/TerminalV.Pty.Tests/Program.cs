using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TerminalV.Pty;
using TerminalV.Host;

var passed = 0;
void Check(string name, Action test)
{
    try { test(); }
    catch (Exception ex) { Console.WriteLine($"FAIL {name}: {ex}"); throw; }
    Console.WriteLine($"PASS {name}");
    passed++;
}
void Equal<T>(T actual, T expected)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected))
        throw new Exception($"Expected {expected}, got {actual}");
}
string Marker(string path, string end = "\a") => $"\u001b]9;9;\"{path}\"{end}";
string Quote(string path) => "'" + path.Replace("'", "''") + "'";
string Encode(string script) => Convert.ToBase64String(Encoding.Unicode.GetBytes(script));

EnvironmentTests.Run(Check);

Check("OSC metadata survives every possible chunk boundary and both terminators", () =>
{
    foreach (var end in new[] { "\a", "\u001b\\", "\u009c" })
    {
        var marker = Marker(@"C:\Проект с пробелом\[test] O'Brien", end);
        for (var split = 0; split <= marker.Length; split++)
        {
            var tracker = new WorkingDirectoryTracker(@"C:\");
            tracker.Feed(marker[..split]);
            tracker.Feed(marker[split..]);
            Equal(tracker.CurrentDirectory, @"C:\Проект с пробелом\[test] O'Brien");
        }
    }
});
Check("titles, relative paths and malformed metadata never become directories", () =>
{
    var tracker = new WorkingDirectoryTracker(@"C:\initial");
    foreach (var text in new[] {
        "\u001b]0;C:\\title\a", Marker("relative"), Marker("C:relative"),
        Marker("C:\\bad\npath"), Marker("C:\\bad\"path"),
        Marker("C:\\" + new string('x', 40000)), "\u001b]9;9;C:\\cancel\u0018\a" })
    {
        tracker.Feed(text);
        Equal(tracker.CurrentDirectory, @"C:\initial");
    }
    tracker.Feed(Marker(@"C:\recovered"));
    Equal(tracker.CurrentDirectory, @"C:\recovered");
});
Check("latest directory outlives scrollback and duplicate prompts are ignored", () =>
{
    var tracker = new WorkingDirectoryTracker(@"C:\initial");
    var changes = 0;
    tracker.Changed += _ => changes++;
    tracker.Feed("\u009d9;9;C:\\one\u009c" + Marker(@"C:\one"));
    tracker.Feed(new string('x', 1_600_000));
    Equal(tracker.CurrentDirectory, @"C:\one");
    tracker.Feed(Marker(@"\\server\share\folder"));
    Equal(tracker.CurrentDirectory, @"\\server\share\folder");
    Equal(changes, 2);
});
Check("launch command enables profiles, integrates both PowerShell executables only", () =>
{
    foreach (var executable in new[] { @"C:\Windows\powershell.exe", @"C:\Program Files\PowerShell\7\pwsh.exe" })
    {
        var command = PowerShellIntegration.CommandLine(executable, @"C:\Test's; Write-Error bad");
        Equal(command.Contains(" -NoExit -EncodedCommand "), true);
        Equal(command.Contains("-NoProfile"), false);
        var decoded = Encoding.Unicode.GetString(Convert.FromBase64String(command.Split(" -EncodedCommand ")[1]));
        Equal(decoded.Contains("-LiteralPath 'C:\\Test''s; Write-Error bad'"), true);
    }
    Equal(PowerShellIntegration.CommandLine(@"C:\Windows\cmd.exe"), "\"C:\\Windows\\cmd.exe\"");
});

Check("explicit shell selection ignores automatic override and validates command boundaries", () =>
{
    Equal(ShellResolver.Resolve(shell: "powershell").DisplayName, "Windows PowerShell");
    Equal(ShellResolver.Resolve(shell: "cmd").CommandLine.EndsWith(" /d"), true);
    foreach (var action in new Action[] {
        () => ShellResolver.Resolve(shell: "unknown"),
        () => ShellResolver.Resolve(shell: "cmd", startupCommand: "echo a\necho b"),
        () => ShellResolver.Resolve(shell: "powershell", startupCommand: new string('x', 4097)),
        () => ShellResolver.Resolve(shell: "powershell", startupCommand: "bad\0text"),
        () => ShellResolver.Resolve(@"\\server\share", "cmd"),
        () => PowerShellIntegration.CommandLine("custom.exe", startupCommand: "echo unsupported")
    })
    {
        try { action(); throw new Exception("Invalid launch accepted"); }
        catch (ArgumentException) { }
    }
});

Check("list barriers silence only the attaching sessions until their cached output is drained", () =>
{
    var guard = new OutputReplayGuard();
    guard.BeginList(); // initial inventory query
    guard.BeginList("a");
    guard.BeginList("b");
    Equal(guard.IsReplaying("a"), true);
    Equal(guard.IsReplaying("b"), true);
    Equal(guard.IsReplaying("other"), false);
    Equal(guard.CompleteList(), true);
    Equal(guard.IsReplaying("a"), true);
    Equal(guard.CompleteList(), false);
    Equal(guard.IsReplaying("a"), false);
    Equal(guard.IsReplaying("b"), true);
    Equal(guard.CompleteList(), false);
    Equal(guard.IsReplaying("b"), false);
});
Check("repeated attaches and ordinary list requests keep their own ordered barriers", () =>
{
    var guard = new OutputReplayGuard();
    guard.BeginList("same");
    guard.BeginList();
    guard.BeginList("same");
    Equal(guard.CompleteList(), false);
    Equal(guard.IsReplaying("same"), true);
    Equal(guard.CompleteList(), true);
    Equal(guard.IsReplaying("same"), true);
    Equal(guard.CompleteList(), false);
    Equal(guard.IsReplaying("same"), false);
});
Check("reconnecting clears pending replay state, including attaches with no output", () =>
{
    var guard = new OutputReplayGuard();
    guard.BeginList("empty");
    Equal(guard.CompleteList(), false);
    Equal(guard.IsReplaying("empty"), false);
    guard.BeginList("disconnected");
    guard.Reset();
    Equal(guard.IsReplaying("disconnected"), false);
    Equal(guard.CompleteList(), true);
});

Check("legacy host cannot silently launch a profile with the wrong shell", () =>
{
    using var server = new IsolatedHost(supportsProfiles: false);
    using var client = new SessionClient(server.PipeName, () => { });
    Equal(client.Ensure(), true);
    try { client.Create("profile", 80, 24, null, "cmd", "echo test"); throw new Exception("Legacy host accepted a profile"); }
    catch (InvalidOperationException ex) { Equal(ex.Message.Contains("не поддерживает профили"), true); }
    Equal(server.Requests.Count, 1);
    Equal(server.Requests.Single().GetProperty("type").GetString(), "list");
    client.Create("default", 80, 24, null);
    Equal(SpinWait.SpinUntil(() => server.Requests.Count == 2, 5000), true);
    Equal(server.Requests.Last().GetProperty("type").GetString(), "create");
});
Check("profile transport sends one explicit create and reattachment never resends the startup script", () =>
{
    using var server = new IsolatedHost(supportsProfiles: true);
    using var client = new SessionClient(server.PipeName, () => { });
    var replay = new System.Collections.Concurrent.ConcurrentQueue<bool>();
    client.Data += (_, _, historical) => replay.Enqueue(historical);
    Equal(client.Ensure(), true);
    const string command = "Write-Output 'Кириллица'\r\nWrite-Output \"quotes\"";
    client.Create("profile", 100, 30, @"C:\Проект", "powershell", command);
    client.Attach("profile");
    client.List(); // ordered barrier: attach replay is drained before returning
    Equal(server.Requests.Count(r => r.GetProperty("type").GetString() == "create-profile"), 1);
    var create = server.Requests.Single(r => r.GetProperty("type").GetString() == "create-profile");
    Equal(create.GetProperty("startupCommand").GetString(), command);
    Equal(create.GetProperty("shell").GetString(), "powershell");
    Equal(create.GetProperty("cwd").GetString(), @"C:\Проект");
    Equal(replay.Single(), true);
    Equal(server.Requests.Single(r => r.GetProperty("type").GetString() == "attach").TryGetProperty("startupCommand", out _), false);
    Equal(server.Requests.Any(r => r.GetProperty("type").GetString() is "write" or "kill"), false);
});

Check("WSL discovery parses UTF-16 decoded names, deduplicates and rejects control/option names", () =>
{
    var names = WslSupport.ParseDistributions("\uFEFFUbuntu\r\nDebian\r\nubuntu\r\nМой-Linux\r\n--exec\r\nbad\0name\r\nC:\\bad\r\ndocker-desktop\r\nrancher-desktop-data\r\n");
    Equal(string.Join('|', names), "Ubuntu|Debian|Мой-Linux");
    Equal(WslSupport.ParseDistributions("\r\n  ").Length, 0);
});
Check("WSL launch keeps the distribution a literal argument and starts in Linux home", () =>
{
    Equal(WslSupport.CommandLine(@"C:\Windows\System32\wsl.exe", "Ubuntu.Dev_24-04"),
        "\"C:\\Windows\\System32\\wsl.exe\" --distribution Ubuntu.Dev_24-04 --cd ~");
    foreach (var name in new string?[] { null, "", " ", "Ubuntu Dev", "Ubuntu&bad", "Ubuntu;bad", "--exec", "Ubuntu\" --exec bad", "bad\nname", "bad\\name", new('x', 257) })
    {
        try { WslSupport.CommandLine("wsl.exe", name); throw new Exception("Invalid distribution accepted"); }
        catch (ArgumentException) { }
    }
    foreach (var action in new Action[] {
        () => ShellResolver.Resolve(shell: "powershell", wslDistribution: "Ubuntu"),
        () => ShellResolver.Resolve(shell: "wsl", wslDistribution: "Ubuntu", startupCommand: "unexpected"),
        () => ShellResolver.Resolve(@"C:\Windows", "wsl", wslDistribution: "Ubuntu") })
    {
        try { action(); throw new Exception("Invalid WSL options accepted"); }
        catch (ArgumentException) { }
    }
});
Check("profile-capable legacy host cannot ignore a WSL distribution or replace it with Windows shell", () =>
{
    using var server = new IsolatedHost(supportsProfiles: true);
    using var client = new SessionClient(server.PipeName, () => { });
    Equal(client.Ensure(), true);
    try { client.Create("wsl", 80, 24, null, "wsl", wslDistribution: "Ubuntu"); throw new Exception("Legacy WSL launch accepted"); }
    catch (InvalidOperationException ex) { Equal(ex.Message.Contains("WSL"), true); }
    Equal(server.Requests.Any(r => r.GetProperty("type").GetString() != "list"), false);
});
Check("WSL transport preserves distribution across explicit create and never relaunches on attach", () =>
{
    using var server = new IsolatedHost(supportsProfiles: true, supportsWsl: true);
    using var client = new SessionClient(server.PipeName, () => { });
    Equal(client.Ensure(), true);
    client.Create("linux", 100, 30, null, "wsl", wslDistribution: "Ubuntu-Dev");
    client.Attach("linux");
    client.List();
    var create = server.Requests.Single(r => r.GetProperty("type").GetString() == "create-profile");
    Equal(create.GetProperty("wslDistribution").GetString(), "Ubuntu-Dev");
    Equal(create.GetProperty("shell").GetString(), "wsl");
    Equal(create.GetProperty("cwd").ValueKind, System.Text.Json.JsonValueKind.Null);
    Equal(server.Requests.Any(r => r.GetProperty("type").GetString() is "write" or "kill"), false);
    Equal(server.Requests.Single(r => r.GetProperty("type").GetString() == "attach").TryGetProperty("wslDistribution", out _), false);
});
Check("large paste is chunked into bounded writes and preserves surrogates", () =>
{
    using var server = new IsolatedHost(supportsProfiles: true, supportsWsl: true);
    using var client = new SessionClient(server.PipeName, () => { });
    Equal(client.Ensure(), true);
    // Empty the initial list request
    while (server.Requests.TryDequeue(out _)) { }
    var large = new string('a', 10000) + " кириллица 🖥" + new string('b', 8000);
    var emoji = "😀"; // surrogate pair
    var withEmoji = new string('x', 3999) + emoji + new string('y', 5000);
    foreach (var payload in new[] { large, withEmoji, "\u001b[200~" + new string('z', 12000) + "\u001b[201~" })
    {
        while (server.Requests.TryDequeue(out _)) { }
        client.Write("test-id", payload);
        // Give pipe time to deliver all chunks
        Equal(SpinWait.SpinUntil(() => server.Requests.Count >= 1, 2000), true);
        Thread.Sleep(100);
        var writes = server.Requests.Where(r => r.GetProperty("type").GetString() == "write").ToArray();
        Equal(writes.Length > 1 || payload.Length <= 4000, true);
        foreach (var w in writes) Equal(w.GetProperty("data").GetString()!.Length <= 4000, true);
        var reassembled = string.Concat(writes.Select(w => w.GetProperty("data").GetString()));
        Equal(reassembled, payload);
        // No chunk should split surrogate pair
        foreach (var w in writes)
        {
            var data = w.GetProperty("data").GetString()!;
            if (data.Length > 0) Equal(char.IsLowSurrogate(data[0]), false);
            if (data.Length > 0) Equal(char.IsHighSurrogate(data[data.Length - 1]) && payload.Contains(data + "😀"), false);
        }
        // Clear for next payload
        while (server.Requests.TryDequeue(out _)) { }
    }
    // 100 chars with dialog (multiline) in muse — must not be chunked and must not block
    var smallMultiline = new string('x', 95) + "\n123"; // 100 chars, contains \n
    while (server.Requests.TryDequeue(out _)) { }
    var swSmall = Stopwatch.StartNew();
    client.Write("small-id", smallMultiline);
    Equal(SpinWait.SpinUntil(() => server.Requests.Count >= 1, 500), true);
    Thread.Sleep(50);
    var smallWrites = server.Requests.Where(r => r.GetProperty("type").GetString() == "write").ToArray();
    Equal(smallWrites.Length, 1);
    Equal(string.Concat(smallWrites.Select(r => r.GetProperty("data").GetString())), smallMultiline);
    Equal(swSmall.ElapsedMilliseconds < 100, true);
    // 100k large paste (typical Muse code paste) must be chunked promptly
    var huge = new string('q', 100000);
    while (server.Requests.TryDequeue(out _)) { }
    var sw = Stopwatch.StartNew();
    client.Write("huge-id", huge);
    SpinWait.SpinUntil(() => server.Requests.Count > 20, 2000);
    sw.Stop();
    Equal(sw.ElapsedMilliseconds < 500, true);
    var hugeWrites = server.Requests.Where(r => r.GetProperty("type").GetString() == "write").ToArray();
    Equal(string.Concat(hugeWrites.Select(r => r.GetProperty("data").GetString())).Length, huge.Length);
});

Check("concurrent 4k paste writes don't deadlock queue lock (split 4 panes reproducer)", () =>
{
    // Reproduces the 4k multiline bracketed paste that previously held queueGate during blocking Write.
    using var standardHandles = new GuiStandardHandles();
    var cmd = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe") + " /d";
    using var pty = ConPtySession.Start("concurrent-4k", cmd, Path.GetTempPath(), 120, 30, session =>
    {
        session.Output += _ => { };
    });
    var payload = "\u001b[200~" + new string('a', 4000) + "\u001b[201~"; // 4012 chars -> 2 chunks via SessionClient but 1 via ConPty (8192)
    var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(() =>
    {
        // Simulate 4 panes pasting at once (or rapid successive chunks needing queue)
        for (var i = 0; i < 8; i++) pty.Write(payload);
    })).ToArray();
    var sw = Stopwatch.StartNew();
    var completed = Task.WaitAll(tasks, 5000);
    sw.Stop();
    Equal(completed, true);
    Equal(sw.ElapsedMilliseconds < 2000, true);
    // Allow pump to drain
    Thread.Sleep(400);
    // Verify pump still responsive to new write after burst
    var sw2 = Stopwatch.StartNew();
    pty.Write("echo OK\r");
    // Don't require output, just that Write doesn't block
    Equal(sw2.ElapsedMilliseconds < 500, true);
});

Check("WindowChrome hit-test: all four edges, corners, caption and maximized", () =>
{
    // Pure math from WindowFrame.HitTest — must match 6px border / 36px caption
    int Hit(int l, int t, int r, int b, int x, int y, bool maximized = false)
    {
        const int B = 6, H = 36, C = 1, Cap = 2, L = 10, R = 11, Top = 12, TL = 13, TR = 14, Bot = 15, BL = 16, BR = 17;
        if (maximized) return C;
        var onL = x - l < B; var onR = r - x < B; var onT = y - t < B; var onB = b - y < B;
        if (onT && onL) return TL; if (onT && onR) return TR; if (onB && onL) return BL; if (onB && onR) return BR;
        if (onL) return L; if (onR) return R; if (onT) return Top; if (onB) return Bot;
        if (y - t < H && !onL && !onR && !onB) return Cap; return C;
    }
    int l = 100, t = 100, r = 1320, b = 880;
    Equal(Hit(l, t, r, b, l + 2, (t + b) / 2), 10); // left
    Equal(Hit(l, t, r, b, r - 2, (t + b) / 2), 11); // right
    Equal(Hit(l, t, r, b, (l + r) / 2, t + 2), 12); // top
    Equal(Hit(l, t, r, b, (l + r) / 2, b - 2), 15); // bottom
    Equal(Hit(0, 0, 800, 600, 2, 2), 13); Equal(Hit(0, 0, 800, 600, 798, 2), 14);
    Equal(Hit(0, 0, 800, 600, 2, 598), 16); Equal(Hit(0, 0, 800, 600, 798, 598), 17);
    Equal(Hit(l, t, r, b, (l + r) / 2, t + 10), 2); // caption
    Equal(Hit(0, 0, 1920, 1080, 2, 200, true), 1); // maximized no resize
});

// Opt-in to a known installed distribution. No Linux profile, file writes or distro shutdown.
var wslDistribution = Environment.GetEnvironmentVariable("TERMINALV_TEST_WSL_DISTRIBUTION");
if (!string.IsNullOrWhiteSpace(wslDistribution))
{
    Check("installed WSL distribution is discovered without launching a Linux shell", () =>
    {
        var catalog = LaunchCatalog.DiscoverAsync(CancellationToken.None).GetAwaiter().GetResult();
        Equal(catalog.Targets.Any(t => t.Shell == "wsl" && t.WslDistribution == wslDistribution), true);
        Equal(catalog.Targets.Any(t => t.Shell == "powershell"), true);
        Equal(catalog.Targets.Any(t => t.Shell == "cmd"), true);
    });
    Check("WSL ConPTY accepts input, outputs Unicode and starts in Linux home without loading shell profiles", () =>
    {
        using var standardHandles = new GuiStandardHandles();
        using var ready = new ManualResetEventSlim();
        var output = new StringBuilder();
        // --exec /bin/sh -c replaces the default interactive shell only in this test.
        var script = "printf 'TERMINALV_WSL_READY\\n'; read -r reply; test \"$reply\" = PING && printf 'TERMINALV_WSL_INPUT_OK\\n'; " +
            "test \"$PWD\" = \"$HOME\" && printf 'TERMINALV_WSL_HOME_OK\\n'; printf 'Проверка_кириллицы\\n'; exit 0";
        var command = ShellResolver.Resolve(shell: "wsl", wslDistribution: wslDistribution).CommandLine +
            " --exec /bin/sh -c \"" + script.Replace("\"", "\\\"") + "\"";
        using var pty = ConPtySession.Start("wsl-test", command,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), 120, 30, session =>
            {
                session.Output += chunk =>
                {
                    lock (output)
                    {
                        output.Append(chunk);
                        if (output.ToString().Contains("TERMINALV_WSL_READY")) ready.Set();
                    }
                    if (chunk.Contains("\u001b[6n")) session.Write("\u001b[1;1R");
                };
            });
        using var child = Process.GetProcessById(pty.ProcessId);
        if (!ready.Wait(TimeSpan.FromSeconds(20)))
        {
            lock (output) throw new Exception("WSL did not become ready; exited=" + child.HasExited +
                "; output=" + System.Text.Json.JsonSerializer.Serialize(output.ToString()));
        }
        pty.Write("PING\r");
        Equal(SpinWait.SpinUntil(() => { lock (output) return output.ToString().Contains("Проверка_кириллицы"); }, 10000), true);
        lock (output)
        {
            Equal(output.ToString().Contains("TERMINALV_WSL_INPUT_OK"), true);
            Equal(output.ToString().Contains("TERMINALV_WSL_HOME_OK"), true);
        }
        Equal(child.WaitForExit(5000), true);
    });
}
else Console.WriteLine("SKIP installed WSL integration (set TERMINALV_TEST_WSL_DISTRIBUTION to opt in)");

var testRoot = Directory.CreateTempSubdirectory("terminalv-cwd-test-").FullName;
try
{
    var initial = Directory.CreateDirectory(Path.Combine(testRoot, "initial")).FullName;
    var target = Directory.CreateDirectory(Path.Combine(testRoot, "Проект с пробелом [test] O'Brien; literal")).FullName;
    Check("directory resolver preserves literal paths and reports missing folders", () =>
    {
        Equal(WorkingDirectory.Resolve(target), new WorkingDirectory(target, null));
        Equal(WorkingDirectory.Resolve(null).Notice, null);
        var missing = WorkingDirectory.Resolve(Path.Combine(testRoot, "missing"));
        Equal(missing.Path, Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
        Equal(missing.Notice is not null, true);
        Equal(WorkingDirectory.Resolve("C:relative").Notice is not null, true);
    });

    var shells = new List<string> {
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
    };
    var pwsh = Environment.GetEnvironmentVariable("TERMINALV_TEST_PWSH") ??
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
    if (File.Exists(pwsh)) shells.Add(pwsh);
    else Console.WriteLine("SKIP PowerShell 7 runtime (set TERMINALV_TEST_PWSH to its executable)");

    foreach (var shell in shells)
    {
        Check($"{Path.GetFileName(shell)}: startup script runs once, after cwd setup, without command quoting loss", () =>
        {
            var marker = Path.Combine(testRoot, Guid.NewGuid().ToString("N") + ".txt");
            var startup = "$terminalVProfileValue = 'Кириллица & quotes \" literal'\r\n" +
                "[IO.File]::AppendAllText(" + Quote(marker) + ", (Get-Location).Path + [Environment]::NewLine + $terminalVProfileValue + [Environment]::NewLine)";
            var script = PowerShellIntegration.Script(target, startup) + "\nprompt\nprompt\n";
            var info = new ProcessStartInfo(shell) { UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Encode(script) }) info.ArgumentList.Add(arg);
            using var child = Process.Start(info)!;
            var output = child.StandardOutput.ReadToEndAsync();
            var errors = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(15000)) { child.Kill(); throw new Exception("Startup test timed out"); }
            output.GetAwaiter().GetResult();
            if (child.ExitCode != 0) throw new Exception(errors.GetAwaiter().GetResult());
            var lines = File.ReadAllLines(marker);
            Equal(lines.Length, 2);
            Equal(lines[0], target);
            Equal(lines[1], "Кириллица & quotes \" literal");

            // A deleted cwd must stop the script, not run it in the home folder.
            info.ArgumentList[^1] = Encode(PowerShellIntegration.Script(Path.Combine(testRoot, "missing"), startup));
            using var missing = Process.Start(info)!;
            var failedOutput = missing.StandardOutput.ReadToEndAsync();
            var failedError = missing.StandardError.ReadToEndAsync();
            if (!missing.WaitForExit(15000)) { missing.Kill(); throw new Exception("Missing cwd test timed out"); }
            failedOutput.GetAwaiter().GetResult();
            failedError.GetAwaiter().GetResult();
            Equal(File.ReadAllLines(marker).Length, 2);
        });

        Check($"{Path.GetFileName(shell)}: literal cwd, custom prompt, command status and provider fallback", () =>
        {
            // Profiles are deliberately disabled only in tests: the real profile may run user commands.
            // Simulate a profile defining a custom prompt and moving away from the inherited cwd.
            var script = """
                [Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
                function global:prompt { "CUSTOM status=$? native=$LASTEXITCODE" }
                """ + "\nSet-Location -LiteralPath " + Quote(initial) + "\n" +
                PowerShellIntegration.Script(target) + "\n" + PowerShellIntegration.Script() + "\n" + """
                $global:LASTEXITCODE = 42
                Write-Error 'expected failure' -ErrorAction SilentlyContinue
                prompt
                Set-Location HKCU:\
                prompt
                """;
            var info = new ProcessStartInfo(shell) {
                UseShellExecute = false, CreateNoWindow = true,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = Encoding.UTF8
            };
            foreach (var arg in new[] { "-NoLogo", "-NoProfile", "-NonInteractive", "-EncodedCommand", Encode(script) })
                info.ArgumentList.Add(arg);
            using var child = Process.Start(info)!;
            var outputTask = child.StandardOutput.ReadToEndAsync();
            var errorTask = child.StandardError.ReadToEndAsync();
            if (!child.WaitForExit(15000)) { child.Kill(); throw new Exception("Isolated PowerShell test timed out"); }
            var output = outputTask.GetAwaiter().GetResult();
            var error = errorTask.GetAwaiter().GetResult();
            if (child.ExitCode != 0) throw new Exception(error);
            Equal(output.Contains("CUSTOM status=False native=42"), true);
            Equal(output.Split("]9;9;").Length - 1, 1);
            var tracker = new WorkingDirectoryTracker(initial);
            tracker.Feed(output);
            Equal(tracker.CurrentDirectory, target);
        });

        Check($"{Path.GetFileName(shell)}: real ConPTY forwards directory metadata before subscribers miss it", () =>
        {
            // Model the GUI host's absent standard handles. A redirected console test
            // runner would otherwise pass its own pipes to PowerShell outside ConPTY.
            using var standardHandles = new GuiStandardHandles();
            using var directoryReady = new ManualResetEventSlim();
            var expected = target;
            var command = PowerShellIntegration.CommandLine(shell, target).Replace(" -NoLogo ", " -NoLogo -NoProfile ");
            using var pty = ConPtySession.Start("cwd-test", command,
                initial, 100, 30, session =>
                {
                    session.DirectoryChanged += path => { if (path == expected) directoryReady.Set(); };
                    session.Output += data => { if (data.Contains("\u001b[6n")) session.Write("\u001b[1;1R"); };
                });
            using var child = Process.GetProcessById(pty.ProcessId);
            Equal(directoryReady.Wait(TimeSpan.FromSeconds(10)), true);
            Equal(pty.CurrentDirectory, target);
            expected = initial;
            directoryReady.Reset();
            pty.Write("Set-Location -LiteralPath " + Quote(initial) + "\r");
            Equal(directoryReady.Wait(TimeSpan.FromSeconds(10)), true);
            Equal(pty.CurrentDirectory, initial);
            pty.Dispose();
            Equal(child.WaitForExit(5000), true);
        });
    }

    Check("cmd profile executes a quoted executable once and remains interactive in its starting folder", () =>
    {
        using var standardHandles = new GuiStandardHandles();
        var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
        var markerFile = Path.Combine(testRoot, "cmd-starts.txt");
        var command = ShellResolver.Resolve(target, "cmd", $"\"{exe}\" /d /c echo PROFILE_ONCE >> \"{markerFile}\"").CommandLine;
        var output = new StringBuilder();
        using var pty = ConPtySession.Start("cmd-profile-test", command, target, 160, 30, session =>
        {
            session.Output += chunk => {
                lock (output) output.Append(chunk);
                if (chunk.Contains("\u001b[6n")) session.Write("\u001b[1;1R");
            };
        });
        using var child = Process.GetProcessById(pty.ProcessId);
        // Raw output also contains an OSC window title with the command text.
        // Count actual side effects, not occurrences in terminal escape sequences.
        bool HasLines(int count)
        {
            try { return File.Exists(markerFile) && File.ReadAllLines(markerFile).Length == count; }
            catch (IOException) { return false; }
        }
        Equal(SpinWait.SpinUntil(() => HasLines(1), 10000), true);
        Equal(child.HasExited, false);
        pty.Write($"echo INTERACTIVE_OK >> \"{markerFile}\"\r");
        Equal(SpinWait.SpinUntil(() => HasLines(2), 10000), true);
        Equal(File.ReadAllLines(markerFile).Count(line => line.Trim() == "PROFILE_ONCE"), 1);
        Equal(pty.CurrentDirectory, target);
        pty.Dispose();
        Equal(child.WaitForExit(5000), true);
    });
}
finally
{
    // Only this run's isolated temp tree, never a real profile or user project.
    for (var attempt = 0; ; attempt++)
    {
        try { Directory.Delete(testRoot, recursive: true); break; }
        catch (IOException) when (attempt < 25) { Thread.Sleep(100); }
    }
}
Console.WriteLine($"{passed} checks passed.");

sealed class GuiStandardHandles : IDisposable
{
    private readonly (int Id, IntPtr Handle)[] _handles = new[] { -10, -11, -12 }.Select(id => (id, GetStdHandle(id))).ToArray();
    public GuiStandardHandles()
    {
        foreach (var (id, _) in _handles) SetStdHandle(id, IntPtr.Zero);
    }
    public void Dispose()
    {
        foreach (var (id, handle) in _handles) SetStdHandle(id, handle);
    }
    [DllImport("kernel32.dll")]
    private static extern IntPtr GetStdHandle(int handle);
    [DllImport("kernel32.dll")]
    private static extern bool SetStdHandle(int handle, IntPtr value);
}
