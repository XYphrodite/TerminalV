using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using TerminalV.Cli;
using TerminalV.Data;
using TerminalV.Localization;

namespace TerminalV;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
        // DI setup for IStringLocalizerFactory: initialize localization before any UI.
        LocalizationService.Initialize();

        if (args.Length > 0 && args[0].Equals("--host", StringComparison.OrdinalIgnoreCase))
        {
            TerminalV.Host.SessionHost.Run();
            return 0;
        }

        if (args.Length > 0)
        {
            Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            Console.InputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
            try
            {
                return App.RunCli(args);
            }
            finally
            {
                Console.Out.Flush();
                Console.Error.Flush();
            }
        }

        HideConsole();
        DesktopInstanceLease? lease;
        try
        {
            lease = DesktopInstanceLease.TryAcquire(AppPaths.Root);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Security.SecurityException)
        {
            var loc = LocalizationService.Localizer;
            MessageBox.Show(
                loc["Error_DataAccess"] + "\n\n" + ex.Message,
                loc["Window_Title"], MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }

        using var desktopInstance = lease;
        if (desktopInstance is null)
        {
            var loc = LocalizationService.Localizer;
            MessageBox.Show(
                loc["Info_AlreadyRunning"],
                loc["Window_Title"], MessageBoxButton.OK, MessageBoxImage.Information);
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        app.Run();
        return 0;
    }

    private static void HideConsole()
    {
        var hwnd = GetConsoleWindow();
        if (hwnd != IntPtr.Zero)
        {
            ShowWindow(hwnd, SwHide);
        }

        FreeConsole();
    }

    private const int SwHide = 0;

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetConsoleWindow();

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("kernel32.dll")]
    private static extern bool FreeConsole();
}
