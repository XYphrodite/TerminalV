using System.IO;
using System.Reflection;
using SelfUpdateKit;

namespace TerminalV.Update;

internal static class AppVersion
{
    public const string ExecutableName = "TerminalV.exe";

    public static string Informational
    {
        get
        {
            var informational = typeof(AppVersion).Assembly
                .GetCustomAttribute<AssemblyInformationalVersionAttribute>()
                ?.InformationalVersion;
            if (!string.IsNullOrWhiteSpace(informational))
            {
                var plus = informational.IndexOf('+');
                return plus >= 0 ? informational[..plus] : informational;
            }

            return typeof(AppVersion).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";
        }
    }

    public static ReleaseVersion Current =>
        ReleaseVersion.TryParse(Informational, out var version) ? version : new ReleaseVersion(0, 0, 0);

    public static bool CanSelfUpdate(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath) || !Path.IsPathFullyQualified(processPath))
        {
            return false;
        }

        if (!string.Equals(Path.GetFileName(processPath), ExecutableName, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var full = Path.GetFullPath(processPath);
        if (full.Contains(@"\bin\Debug\", StringComparison.OrdinalIgnoreCase) ||
            full.Contains(@"\bin\Release\", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var directory = Path.GetDirectoryName(full);
        return directory is not null && File.Exists(Path.Combine(directory, "wwwroot", "index.html"));
    }
}
