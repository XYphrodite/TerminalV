using System.IO;

namespace TerminalV.Pty;

internal readonly record struct WorkingDirectory(string Path, string? Notice)
{
    internal static bool IsValidPath(string path) =>
        !string.IsNullOrWhiteSpace(path) && !path.Any(char.IsControl) &&
        path.IndexOfAny(['"', '<', '>', '|', '*']) < 0 &&
        path.IndexOfAny(System.IO.Path.GetInvalidPathChars()) < 0 &&
        System.IO.Path.IsPathFullyQualified(path);

    public static WorkingDirectory Resolve(string? requested)
    {
        if (requested is not null && IsValidPath(requested) && Directory.Exists(requested))
        {
            return new(System.IO.Path.GetFullPath(requested), null);
        }

        var home = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return new(home, string.IsNullOrEmpty(requested) ? null :
            $"Папка «{requested}» недоступна. Сессия открыта в домашней папке «{home}».");
    }
}
