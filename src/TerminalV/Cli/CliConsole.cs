using System.Text;

namespace TerminalV.Cli;

internal static class CliConsole
{
    public static void Attach()
    {
        Console.OutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
    }

    public static void WriteLine(string text) => Console.WriteLine(text);

    public static void WriteError(string text) => Console.Error.WriteLine(text);

    public static void Flush()
    {
        Console.Out.Flush();
        Console.Error.Flush();
    }

    public static void Detach() => Flush();

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
