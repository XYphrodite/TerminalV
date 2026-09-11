using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using TerminalV.Cli;
using TerminalV.Pty;
using TerminalV.Update;

namespace TerminalV;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        PendingUpdateApplier.Apply(Environment.ProcessPath);
        base.OnStartup(e);
        new MainWindow().Show();
    }

    internal static int RunCli(string[] argv)
    {
        var command = argv[0];
        if (IsHelp(command) || argv.Any(IsHelp) && !command.Equals("update", StringComparison.OrdinalIgnoreCase))
        {
            CliConsole.WriteLine(CliConsole.HelpText(AppVersion.Informational));
            return 0;
        }

        if (command.Equals("--version", StringComparison.OrdinalIgnoreCase))
        {
            CliConsole.WriteLine(AppVersion.Informational);
            return 0;
        }

        if (command.Equals("--smoke", StringComparison.OrdinalIgnoreCase))
        {
            return RunSmoke() ? 0 : 1;
        }

        if (command.Equals("update", StringComparison.OrdinalIgnoreCase))
        {
            return UpdateCommand.Run(argv.Skip(1).ToArray());
        }

        CliConsole.WriteError($"Unknown command: {command}");
        CliConsole.WriteLine(CliConsole.HelpText(AppVersion.Informational));
        return 1;
    }

    private static bool IsHelp(string value) =>
        value is "-h" or "-?" or "/?" || value.Equals("--help", StringComparison.OrdinalIgnoreCase);

    private static bool RunSmoke()
    {
        var log = new StringBuilder();
        try
        {
            var shell = ShellResolver.Resolve();
            log.AppendLine($"shell={shell.CommandLine}");
            using var ready = new ManualResetEventSlim(false);
            using var session = ConPtySession.Start(
                "smoke",
                shell.CommandLine,
                Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                80,
                24);
            log.AppendLine($"pid={session.ProcessId}");
            session.Output += chunk =>
            {
                log.Append(chunk);
                if (log.ToString().Contains("TERMINALV_SMOKE_OK", StringComparison.Ordinal))
                {
                    ready.Set();
                }
            };

            var promptWait = Stopwatch.StartNew();
            while (promptWait.Elapsed < TimeSpan.FromSeconds(4) && log.Length == 0)
            {
                Thread.Sleep(50);
            }

            session.Write("echo TERMINALV_SMOKE_OK\r");
            if (!ready.Wait(TimeSpan.FromSeconds(6)))
            {
                WriteSmokeLog(log, "timeout");
                return false;
            }

            WriteSmokeLog(log, "ok");
            return true;
        }
        catch (Exception ex)
        {
            log.AppendLine(ex.ToString());
            WriteSmokeLog(log, "exception");
            return false;
        }
    }

    private static void WriteSmokeLog(StringBuilder log, string status)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "smoke-result.txt");
        File.WriteAllText(path, status + Environment.NewLine + log);
    }
}
