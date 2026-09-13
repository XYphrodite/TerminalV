using System.IO;

namespace TerminalV.Pty;

internal readonly record struct ShellInfo(string CommandLine, string DisplayName);

internal static class ShellResolver
{
    public static ShellInfo Resolve(string? directory = null, string? shell = null, string? startupCommand = null)
    {
        if (startupCommand is { Length: > 4096 } || startupCommand?.Contains('\0') == true)
            throw new ArgumentException("Стартовая команда: максимум 4096 символов, без NUL.");
        var executable = shell switch
        {
            null or "auto" => DefaultExecutable(),
            "powershell" => WindowsPowerShell(),
            "pwsh" => Candidates().FirstOrDefault(File.Exists) ??
                throw new FileNotFoundException("PowerShell 7 не найден. Установите его или выберите другую оболочку."),
            "cmd" => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
            _ => throw new ArgumentException("Неизвестная оболочка профиля.")
        };
        if (!File.Exists(executable)) throw new FileNotFoundException("Оболочка не найдена.", executable);
        if (Path.GetFileName(executable).Equals("cmd.exe", StringComparison.OrdinalIgnoreCase))
        {
            if (startupCommand?.IndexOfAny(['\r', '\n']) >= 0)
                throw new ArgumentException("Для cmd укажите одну строку команды; несколько команд можно соединить через &&.");
            if (directory?.StartsWith(@"\\") == true)
                throw new ArgumentException("cmd не поддерживает сетевую UNC-папку как текущую. Выберите PowerShell.");
            // /s strips only the added outer quotes; /d disables registry AutoRun.
            var command = string.IsNullOrWhiteSpace(startupCommand) ? "" : $" /s /k \"{startupCommand}\"";
            return new($"\"{executable}\" /d{command}", "Командная строка");
        }
        return new(PowerShellIntegration.CommandLine(executable, directory, startupCommand), PrettyName(executable));
    }

    private static string WindowsPowerShell() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.System), "WindowsPowerShell", "v1.0", "powershell.exe");

    private static string DefaultExecutable()
    {
        var overridePath = Environment.GetEnvironmentVariable("TERMINALV_SHELL");
        if (!string.IsNullOrWhiteSpace(overridePath) && File.Exists(overridePath))
        {
            return overridePath;
        }

        foreach (var candidate in Candidates())
        {
            if (File.Exists(candidate))
            {
                return candidate;
            }
        }

        return WindowsPowerShell();
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
