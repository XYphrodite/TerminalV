using System.IO;

namespace TerminalV.Data;

internal static class AppPaths
{
    public static string Root
    {
        get
        {
            var path = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "TerminalV");
            Directory.CreateDirectory(path);
            Directory.CreateDirectory(Path.Combine(path, "backgrounds"));
            return path;
        }
    }

    public static string Database => Path.Combine(Root, "terminalv.db");

    public static string Backgrounds => Path.Combine(Root, "backgrounds");
}
