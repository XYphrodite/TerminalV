using System.IO;

namespace TerminalV.Pty;

internal readonly record struct ShellInfo(string CommandLine, string DisplayName);

internal static class ShellResolver
{
    public static ShellInfo Resolve()
    {
        var overridePath = Environment.GetEnvironmentVariable("TERMINALV_SHELL");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return new(Quote(overridePath) + " -NoLogo", PrettyName(overridePath));
        }

        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate))
            {
                return new(Quote(candidate) + " -NoLogo", PrettyName(candidate));
            }
        }

        var powershell = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.System),
            "WindowsPowerShell",
            "v1.0",
            "powershell.exe");
        return new(Quote(powershell) + " -NoLogo", "Windows PowerShell");
    }

    private static IEnumerable<string> Candidates()
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        yield return Path.Combine(programFiles, "PowerShell", "7", "pwsh.exe");
        yield return Path.Combine(programFiles, "PowerShell", "7-preview", "pwsh.exe");
        yield return Path.Combine(localAppData, "Microsoft", "WinGet", "Links", "pwsh.exe");
        yield return Path.Combine(userProfile, "scoop", "apps", "pwsh", "current", "pwsh.exe");

        var pathPwsh = FindOnPath("pwsh.exe");
        if (pathPwsh is not null)
        {
            yield return pathPwsh;
        }
    }

    private static string? FindOnPath(string fileName)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(path))
        {
            return null;
        }

        foreach (var raw in path.Split(Path.PathSeparator))
        {
            var dir = raw.Trim().Trim('"');
            if (dir.Length == 0)
            {
                continue;
            }

            try
            {
                var candidate = Path.Combine(dir, fileName);
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
            catch (ArgumentException)
            {
            }
        }

        return null;
    }

    private static string Quote(string path) => $"\"{path}\"";

    private static string PrettyName(string path)
    {
        var name = Path.GetFileNameWithoutExtension(path);
        return name.Equals("pwsh", StringComparison.OrdinalIgnoreCase)
            ? "PowerShell 7"
            : name.Equals("powershell", StringComparison.OrdinalIgnoreCase)
                ? "Windows PowerShell"
                : name;
    }
}
