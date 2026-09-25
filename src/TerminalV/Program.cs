using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using TerminalV.Cli;
using TerminalV.Data;

namespace TerminalV;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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
            MessageBox.Show(
                "Не удалось открыть данные TerminalV. Проверьте доступ к папке " +
                "%LOCALAPPDATA%\\TerminalV и свободное место на диске.\n\n" + ex.Message,
                "TerminalV", MessageBoxButton.OK, MessageBoxImage.Error);
            return 1;
        }

        using var desktopInstance = lease;
        if (desktopInstance is null)
        {
            MessageBox.Show(
                "TerminalV уже открыт. Переключитесь в существующее окно.",
                "TerminalV", MessageBoxButton.OK, MessageBoxImage.Information);
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
