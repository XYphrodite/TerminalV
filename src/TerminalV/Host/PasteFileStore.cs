using System.IO;
using System.Text;
using System.Windows.Media.Imaging;

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

    // Image-only clipboards land here as PNG so the UI can paste the path.
    public static string SaveImage(BitmapSource image)
    {
        System.IO.Directory.CreateDirectory(Directory);
        var path = Path.Combine(Directory, $"paste-{Guid.NewGuid():N}.png");
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        using var stream = File.Create(path);
        encoder.Save(stream);
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
