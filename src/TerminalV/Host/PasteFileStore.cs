using System.IO;
using System.Text;

namespace TerminalV.Host;

// Large clipboard pastes are staged here so the UI can insert a file path
// instead of pushing the raw text through the PTY. Files live until the app
// exits; MainWindow cleans the directory up on close.
internal static class PasteFileStore
{
    public static string Directory { get; } =
        Path.Combine(Path.GetTempPath(), "TerminalV", "pastes");

    public static string Save(string text)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, $"paste-{Guid.NewGuid():N}.txt");
        File.WriteAllText(path, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        return path;
    }

    public static void Cleanup()
    {
        try
        {
            if (System.IO.Directory.Exists(Directory))
            {
                System.IO.Directory.Delete(Directory, recursive: true);
            }
        }
        catch
        {
        }
    }
}
