using System.Diagnostics;
using System.IO;
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
        Process.Start(new ProcessStartInfo(exePath)
        {
            UseShellExecute = true,
            WorkingDirectory = Path.GetDirectoryName(exePath) ?? ""
        });

        var current = Environment.ProcessId;
        foreach (var pid in VisibleTerminalVPids())
        {
            if (pid == current)
            {
                continue;
            }

            try
            {
                using var process = Process.GetProcessById(pid);
                CloseWindows(pid);
                if (!process.WaitForExit(1500))
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (ArgumentException)
            {
            }
            catch (InvalidOperationException)
            {
            }
            catch (System.ComponentModel.Win32Exception)
            {
            }
        }
    }

    private static List<int> VisibleTerminalVPids()
    {
        var pids = new HashSet<int>();
        EnumWindows((hwnd, _) =>
        {
            if (!IsWindowVisible(hwnd))
            {
                return true;
            }

            GetWindowThreadProcessId(hwnd, out var pid);
            try
            {
                using var process = Process.GetProcessById((int)pid);
                if (process.ProcessName.Equals("TerminalV", StringComparison.OrdinalIgnoreCase))
                {
                    pids.Add((int)pid);
                }
            }
            catch (ArgumentException)
            {
            }

            return true;
        }, IntPtr.Zero);
        return pids.ToList();
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

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

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
