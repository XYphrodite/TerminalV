using System.IO;
using System.Runtime.InteropServices;
using System.Text;

namespace TerminalV.Cli;

internal static class CliConsole
{
    private const uint AttachParentProcess = 0xFFFFFFFF;

    private static TextWriter? _out;
    private static TextWriter? _err;
    private static bool _attached;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(uint dwProcessId);

    public static void Attach()
    {
        if (_attached)
        {
            return;
        }

        AttachConsole(AttachParentProcess);
        var encoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
        _out = new StreamWriter(Console.OpenStandardOutput(), encoding) { AutoFlush = true };
        _err = new StreamWriter(Console.OpenStandardError(), encoding) { AutoFlush = true };
        Console.SetOut(_out);
        Console.SetError(_err);
        _attached = true;
    }

    public static void WriteLine(string text)
    {
        Attach();
        _out!.WriteLine(text);
    }

    public static void WriteError(string text)
    {
        Attach();
        _err!.WriteLine(text);
    }

    public static string HelpText(string version) =>
        $"""
        TerminalV {version}

        Usage:
          TerminalV                 Open the terminal window
          TerminalV update          Install the latest GitHub release
          TerminalV update --check  Show whether a newer release exists
          TerminalV --version       Print the version
          TerminalV --help          Print this text
          TerminalV --smoke         ConPTY self-test
        """;
}
