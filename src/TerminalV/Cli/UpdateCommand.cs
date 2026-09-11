using System.Diagnostics;
using TerminalV.Update;

namespace TerminalV.Cli;

internal static class UpdateCommand
{
    public static int Run(IReadOnlyList<string> args)
    {
        CliConsole.Attach();

        var checkOnly = false;
        foreach (var arg in args)
        {
            if (arg.Equals("--check", StringComparison.OrdinalIgnoreCase))
            {
                checkOnly = true;
                continue;
            }

            if (arg is "-h" or "-?" || arg.Equals("--help", StringComparison.OrdinalIgnoreCase))
            {
                CliConsole.WriteLine(CliConsole.HelpText(AppVersion.Informational));
                return 0;
            }

            CliConsole.WriteError($"Unknown option: {arg}");
            CliConsole.WriteError("Usage: TerminalV update [--check]");
            return 1;
        }

        if (!AppVersion.CanSelfUpdate(Environment.ProcessPath))
        {
            CliConsole.WriteError("Self-update is only available for an installed TerminalV.exe.");
            return 1;
        }

        try
        {
            using var source = new GitHubReleaseSource();
            var service = new SelfUpdateService(Environment.ProcessPath!, AppVersion.Current, source);
            if (checkOnly)
            {
                var report = service.CheckAsync(CancellationToken.None).GetAwaiter().GetResult();
                if (report.Status == SelfUpdateStatus.AlreadyCurrent)
                {
                    CliConsole.WriteLine($"TerminalV {report.Installed} is up to date.");
                }
                else
                {
                    CliConsole.WriteLine(
                        $"Update available: {report.Installed} -> {report.Release} ({report.Tag}).");
                    CliConsole.WriteLine("Run 'TerminalV update' to install it.");
                }

                return 0;
            }

            CliConsole.WriteLine($"TerminalV {AppVersion.Informational}. Checking GitHub Releases...");
            var applied = service.ApplyAsync(CancellationToken.None).GetAwaiter().GetResult();
            if (applied.Status == SelfUpdateStatus.AlreadyCurrent)
            {
                CliConsole.WriteLine($"TerminalV {applied.Installed} is up to date.");
                return 0;
            }

            var exe = Environment.ProcessPath!;
            var guiOpen = OtherTerminalVRunning();
            if (!guiOpen)
            {
                PendingUpdateApplier.Apply(exe);
            }

            CliConsole.WriteLine($"Installed {applied.Tag}.");
            if (guiOpen)
            {
                RestartOpenWindows(exe);
            }

            return 0;
        }
        catch (Exception ex)
        {
            CliConsole.WriteError(ex.Message);
            return 1;
        }
    }

    private static void RestartOpenWindows(string exePath)
    {
        var current = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("TerminalV"))
        {
            try
            {
                if (process.Id != current)
                {
                    process.CloseMainWindow();
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        var quoted = "\"" + exePath.Replace("\"", "\\\"") + "\"";
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c ping 127.0.0.1 -n 3 >nul & start \"\" " + quoted,
            UseShellExecute = false,
            CreateNoWindow = true
        });
    }

    private static bool OtherTerminalVRunning()
    {
        var current = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("TerminalV"))
        {
            try
            {
                if (process.Id != current)
                {
                    return true;
                }
            }
            finally
            {
                process.Dispose();
            }
        }

        return false;
    }
}
