using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TerminalV.Pty;
using TerminalV.Host;

var passed = 0;
void Check(string name, Action test)
{
    test();
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
