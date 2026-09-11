using System.Diagnostics;
using System.Runtime.InteropServices;
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
            var lastPaint = Stopwatch.StartNew();
            var started = Stopwatch.StartNew();
            var painted = false;
            var applied = service.ApplyAsync(CancellationToken.None, (received, total) =>
            {
                if (lastPaint.ElapsedMilliseconds < 150 && total is long size && received < size)
                {
                    return;
                }

                lastPaint.Restart();
                painted = true;
                var percent = total is > 0 ? Math.Min(100, (int)(100.0 * received / total.Value)) : 0;
                var filled = percent / 5;
                var bar = new string('#', filled) + new string('-', 20 - filled);
                var receivedMb = received / (1024.0 * 1024.0);
                var totalMb = (total ?? received) / (1024.0 * 1024.0);
                var speed = started.Elapsed.TotalSeconds > 0 ? receivedMb / started.Elapsed.TotalSeconds : 0;
                Console.Write($"\r==> [{bar}] {percent,3}%  {receivedMb:0.0}/{totalMb:0.0} MB  {speed:0.0} MB/s   ");
            }).GetAwaiter().GetResult();
            if (painted)
            {
                Console.WriteLine();
            }
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
        var quoted = exePath.Replace("\"", "\"\"");
        // `start` detaches from this process tree so the relaunch survives if
        // this updater is a child of the GUI we are about to close.
        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = "/c start \"\" cmd /c \"ping 127.0.0.1 -n 3 >nul & start \"\" \"" + quoted + "\"\"",
            UseShellExecute = false,
            CreateNoWindow = true
        });

        var current = Environment.ProcessId;
        foreach (var process in Process.GetProcessesByName("TerminalV"))
        {
            try
            {
                if (process.Id == current)
                {
                    continue;
                }

                CloseWindows(process.Id);
                if (!process.WaitForExit(1500))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
            finally
            {
                process.Dispose();
            }
        }
    }

    private static void CloseWindows(int processId)
    {
        EnumWindows((hwnd, _) =>
        {
            GetWindowThreadProcessId(hwnd, out var pid);
            if (pid == (uint)processId)
            {
                PostMessage(hwnd, WmClose, IntPtr.Zero, IntPtr.Zero);
            }

            return true;
        }, IntPtr.Zero);
    }

    private const uint WmClose = 0x0010;

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

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
