using System.Runtime.InteropServices;
using System.Text;
using TerminalV.Cli;

namespace TerminalV;

internal static class Program
{
    [STAThread]
    public static int Main(string[] args)
    {
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
