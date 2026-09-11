using System.Diagnostics;
using System.IO;
using System.Text;
using System.Windows;
using TerminalV.Pty;
using TerminalV.Update;

namespace TerminalV;

public partial class App : Application
{
    private void OnStartup(object sender, StartupEventArgs e)
    {
        var args = Environment.GetCommandLineArgs();
        if (HasFlag(args, "--help") || HasFlag(args, "-h") || HasFlag(args, "/?"))
        {
            WriteStdout(
                $"""
                TerminalV {AppVersion.Informational}
                  --help       Print this text
                  --version    Print the version
                  --smoke      ConPTY self-test
                """);
            Shutdown(0);
            return;
        }

        if (HasFlag(args, "--version"))
        {
            WriteStdout(AppVersion.Informational);
            Shutdown(0);
            return;
        }

        if (HasFlag(args, "--smoke"))
        {
            Shutdown(RunSmoke() ? 0 : 1);
            return;
        }

        PendingUpdateApplier.Apply(Environment.ProcessPath);
        new MainWindow().Show();
    }

    private static bool HasFlag(string[] args, string flag) =>
        args.Any(arg => string.Equals(arg, flag, StringComparison.OrdinalIgnoreCase));

    private static void WriteStdout(string text)
    {
        using var stream = Console.OpenStandardOutput();
        using var writer = new StreamWriter(stream, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false))
        {
            AutoFlush = true
        };
        writer.WriteLine(text);
    }

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
