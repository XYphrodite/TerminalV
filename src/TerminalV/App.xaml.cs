using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Windows;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Localization;
using SelfUpdateKit;
using TerminalV.Cli;
using TerminalV.Data;
using TerminalV.Localization;
using TerminalV.Pty;
using TerminalV.Update;

namespace TerminalV;

public partial class App : Application
{
    public static IServiceProvider? Services { get; private set; }
    public static IStringLocalizerFactory? LocalizerFactory { get; private set; }

    protected override void OnStartup(StartupEventArgs e)
    {
        // DI setup for IStringLocalizerFactory
        ConfigureLocalization();
        ApplyCulture();
        PendingUpdateApplier.Apply(Environment.ProcessPath,
            TerminalVUpdate.Options(TerminalVUpdate.InstalledVariant(Environment.ProcessPath)));
        base.OnStartup(e);
        new MainWindow().Show();
    }

    private static void ConfigureLocalization()
    {
        // Reuse existing LocalizationService if already initialized from Program.Main,
        // otherwise build the DI container here (e.g., when launched via App directly).
        try
        {
            LocalizationService.Initialize();
            Services = LocalizationService.Provider;
            LocalizerFactory = LocalizationService.Factory;
        }
        catch
        {
            var services = new ServiceCollection();
            services.AddLocalization(options => options.ResourcesPath = "Localization/Resources");
            Services = services.BuildServiceProvider();
            LocalizerFactory = Services.GetRequiredService<IStringLocalizerFactory>();
            LocalizationService.Initialize(Services);
        }
    }

    private static void ApplyCulture()
    {
        try
        {
            string language;
            using (var db = new AppDatabase())
            {
                language = db.LoadSettings().Language;
            }
            var cultureName = language == "en" ? "en" : "ru";
            var culture = new CultureInfo(cultureName);
            CultureInfo.DefaultThreadCurrentCulture = culture;
            CultureInfo.DefaultThreadCurrentUICulture = culture;
            CultureInfo.CurrentCulture = culture;
            CultureInfo.CurrentUICulture = culture;
            LocalizationService.ApplyLanguage(cultureName);
        }
        catch
        {
            var fallback = new CultureInfo("ru");
            CultureInfo.DefaultThreadCurrentCulture = fallback;
            CultureInfo.DefaultThreadCurrentUICulture = fallback;
        }
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
