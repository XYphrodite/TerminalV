using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using TerminalV.Host;
using TerminalV.Pty;

internal static class EnvironmentTests
{
    private const string MarkerName = "TERMINALV_TEST_ENV_MARKER";
    private const string StalePath = @"C:\TerminalV-Test-Stale-Path-Not-An-Installation";

    public static void Run(Action<string, Action> check)
    {
        check("fresh environment replaces stale PATH, preserves process-only Unicode and leaves the host unchanged", () =>
        {
            using var path = new ProcessVariable("PATH", StalePath);
            using var marker = new ProcessVariable(MarkerName, "Первое=значение 🖥");
            using var first = SessionEnvironment.Create();
            var freshPath = Read(first, "PATH");
            Require(!string.IsNullOrEmpty(freshPath) && !freshPath.Contains(StalePath), "PATH was inherited instead of refreshed");
            Require(!string.IsNullOrEmpty(Read(first, "SystemRoot")), "SystemRoot missing");
            Require(!string.IsNullOrEmpty(Read(first, "USERPROFILE")), "User environment missing");
            Require(Read(first, MarkerName) == "Первое=значение 🖥", "Process-only Unicode lost");
            Environment.SetEnvironmentVariable(MarkerName, "Второе");
            using var second = SessionEnvironment.Create();
            Require(Read(second, MarkerName) == "Второе", "Environment cached between sessions");
            Require(Read(first, MarkerName) == "Первое=значение 🖥", "Existing environment was mutated");
            Require(Read(second, "PATH") == freshPath, "PATH changed with the process-only marker");
            Require(Environment.GetEnvironmentVariable("PATH") == StalePath, "Host PATH was mutated");
        });

        check("environment capability distinguishes old and new hosts without restarting sessions", () =>
        {
            foreach (var supported in new[] { false, true })
            {
                using var server = new IsolatedHost(supportsProfiles: true, supportsEnvironmentRefresh: supported);
                using var client = new SessionClient(server.PipeName, () => { });
                Require(client.EnvironmentRefreshSupported is null, "Capability guessed before connection");
                client.List();
                Require(client.EnvironmentRefreshSupported == supported, "Wrong environment capability");
                client.Attach("existing");
                client.List();
                Require(server.Requests.All(r => r.GetProperty("type").GetString() is "list" or "attach"),
                    "Capability check modified a session");
            }
        });

        var shells = new List<string> {
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe")
        };
        var pwsh = Environment.GetEnvironmentVariable("TERMINALV_TEST_PWSH") ??
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "PowerShell", "7", "pwsh.exe");
        if (File.Exists(pwsh)) shells.Add(pwsh);
        var codex = Environment.GetEnvironmentVariable("TERMINALV_TEST_CODEX_PATH");

        foreach (var shell in shells)
        {
            check($"{Path.GetFileName(shell)}: new ConPTY refreshes PATH and Unicode without changing an already running shell", () =>
            {
                using var standardHandles = new GuiStandardHandles();
                using var path = new ProcessVariable("PATH", StalePath);
                using var marker = new ProcessVariable(MarkerName, "Старое 🖥");
                using var existing = new Probe(shell, "[Console]::WriteLine('EXISTING_READY'); $null = [Console]::ReadLine(); " +
                    $"if ($env:{MarkerName} -cne 'Старое 🖥') {{ throw 'Existing environment changed' }}; " +
                    "[Console]::WriteLine('EXISTING_UNCHANGED')");
                existing.WaitFor("EXISTING_READY");

                Environment.SetEnvironmentVariable(MarkerName, "Новое 🖥");
                using (var next = new Probe(shell,
                    $"if ($env:Path.Contains('{StalePath}')) {{ throw 'Stale PATH' }}; " +
                    $"if ($env:{MarkerName} -cne 'Новое 🖥') {{ throw 'Unicode lost' }}; " +
                    "$command = Get-Command where.exe -CommandType Application -ErrorAction Stop; " +
                    "[Console]::WriteLine('FRESH_PATH_AND_UNICODE_OK')"))
                {
                    next.WaitFor("FRESH_PATH_AND_UNICODE_OK");
                    next.ExpectSuccess();
                }
                Require(Environment.GetEnvironmentVariable("PATH") == StalePath, "Host environment modified");
                Require(!existing.Child.HasExited, "Existing session was stopped");
                existing.Pty.Write("continue\r");
                existing.WaitFor("EXISTING_UNCHANGED");
                existing.ExpectSuccess();
            });

            if (!string.IsNullOrWhiteSpace(codex))
            {
                check($"{Path.GetFileName(shell)}: installed Codex resolves by name despite stale host PATH (no CLI execution)", () =>
                {
                    Require(File.Exists(codex), "Opt-in Codex executable missing");
                    using var standardHandles = new GuiStandardHandles();
                    using var path = new ProcessVariable("PATH", StalePath);
                    var literal = "'" + codex.Replace("'", "''") + "'";
                    using var probe = new Probe(shell,
                        "$command = Get-Command codex -CommandType Application -ErrorAction Stop; " +
                        $"if ($command.Source -ine {literal}) {{ throw 'Unexpected Codex path' }}; " +
                        "[Console]::WriteLine('CODEX_FOUND_IN_REFRESHED_PATH')");
                    probe.WaitFor("CODEX_FOUND_IN_REFRESHED_PATH");
                    probe.ExpectSuccess();
                });
            }
        }
        if (string.IsNullOrWhiteSpace(codex)) Console.WriteLine("SKIP installed Codex resolution (set TERMINALV_TEST_CODEX_PATH to opt in)");

        check("cmd ConPTY receives the refreshed environment too", () =>
        {
            using var standardHandles = new GuiStandardHandles();
            using var path = new ProcessVariable("PATH", StalePath);
            var exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe");
            using var probe = new Probe($"\"{exe}\" /d /c where.exe where.exe >nul && echo CMD_FRESH_PATH_OK");
            probe.WaitFor("CMD_FRESH_PATH_OK");
            probe.ExpectSuccess();
        });
    }

    private static string? Read(SessionEnvironment environment, string name)
    {
        var pointer = environment.DangerousGetHandle();
        while (Marshal.PtrToStringUni(pointer) is { Length: > 0 } entry)
        {
            if (entry.StartsWith(name + "=", StringComparison.OrdinalIgnoreCase)) return entry[(name.Length + 1)..];
            pointer = IntPtr.Add(pointer, checked((entry.Length + 1) * sizeof(char)));
        }
        GC.KeepAlive(environment);
        return null;
    }

    private static void Require(bool condition, string message)
    {
        if (!condition) throw new Exception(message);
    }

    // Changes only this test runner, never User/Machine registry variables.
    private sealed class ProcessVariable(string name, string? value) : IDisposable
    {
        private readonly string? _previous = Set(name, value);
        private static string? Set(string name, string? value)
        {
            var previous = Environment.GetEnvironmentVariable(name);
            Environment.SetEnvironmentVariable(name, value);
            return previous;
        }
        public void Dispose() => Environment.SetEnvironmentVariable(name, _previous);
    }

    private sealed class Probe : IDisposable
    {
        private readonly StringBuilder _output = new();
        private string _cursorQueryTail = "";
        private long _exitCode = -1;
        public ConPtySession Pty { get; }
        public Process Child { get; }
        public Probe(string shell, string script) : this($"\"{shell}\" -NoLogo -NoProfile -EncodedCommand " +
            Convert.ToBase64String(Encoding.Unicode.GetBytes("$ErrorActionPreference = 'Stop'; " + script))) { }

        public Probe(string command)
        {
            Pty = ConPtySession.Start("env-test-" + Guid.NewGuid().ToString("N"), command,
                Path.GetTempPath(), 160, 30, session =>
                {
                    session.Exited += code => Interlocked.Exchange(ref _exitCode, code);
                    session.Output += chunk =>
                    {
                        lock (_output) _output.Append(chunk);
                        const string query = "\u001b[6n";
                        var queries = _cursorQueryTail + chunk;
                        for (var index = queries.IndexOf(query, StringComparison.Ordinal); index >= 0;
                            index = queries.IndexOf(query, index + query.Length, StringComparison.Ordinal))
                            session.Write("\u001b[1;1R");
                        _cursorQueryTail = queries[^Math.Min(query.Length - 1, queries.Length)..];
                    };
                });
            Child = Process.GetProcessById(Pty.ProcessId);
        }
        public void WaitFor(string marker)
        {
            if (SpinWait.SpinUntil(() =>
            {
                lock (_output) return _output.ToString().Contains(marker, StringComparison.Ordinal);
            }, 15000)) return;
            throw new Exception($"ConPTY did not emit test marker: {marker}; {Diagnostic()}");
        }
        public void ExpectSuccess()
        {
            if (!Child.WaitForExit(5000)) throw new Exception("Test shell did not exit; " + Diagnostic());
            if (!SpinWait.SpinUntil(() => Volatile.Read(ref _exitCode) != -1, 5000))
                throw new Exception("ConPTY exit event missing; " + Diagnostic());
            if (Volatile.Read(ref _exitCode) != 0) throw new Exception("Test shell failed; " + Diagnostic());
        }
        private string Diagnostic()
        {
            string transcript;
            lock (_output) transcript = _output.ToString();
            var exited = Child.HasExited;
            return $"child exited: {exited}; process exit code: {(exited ? Child.ExitCode.ToString() : "running")}; " +
                $"ConPTY exit code: {Volatile.Read(ref _exitCode)}; Windows: {Environment.OSVersion.Version}; " +
                $"output: {System.Text.Json.JsonSerializer.Serialize(transcript)}";
        }
        public void Dispose()
        {
            Pty.Dispose();
            Child.WaitForExit(5000);
            Child.Dispose();
        }
    }
}
