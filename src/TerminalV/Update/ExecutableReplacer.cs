using System.IO;

namespace TerminalV.Update;

internal interface IExecutableReplacer
{
    string Replace(string currentPath, string stagedPath);

    void Restore(string retiredPath, string currentPath);

    int RemoveRetiredCopies(string currentPath);
}

internal sealed class ExecutableReplacer : IExecutableReplacer
{
    private const string RetiredSuffix = ".old-";

    public string Replace(string currentPath, string stagedPath)
    {
        var current = RequireFullPath(currentPath, nameof(currentPath));
        var staged = RequireFullPath(stagedPath, nameof(stagedPath));
        if (!File.Exists(staged))
        {
            throw new FileNotFoundException("The staged binary is missing.", staged);
        }

        if (!File.Exists(current))
        {
            throw new FileNotFoundException("The installed binary is missing.", current);
        }

        var retired = current + RetiredSuffix + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",
            System.Globalization.CultureInfo.InvariantCulture);
        File.Move(current, retired);
        try
        {
            File.Move(staged, current);
        }
        catch
        {
            File.Move(retired, current);
            throw;
        }

        return retired;
    }

    public void Restore(string retiredPath, string currentPath)
    {
        var retired = RequireFullPath(retiredPath, nameof(retiredPath));
        var current = RequireFullPath(currentPath, nameof(currentPath));
        if (!File.Exists(retired))
        {
            throw new FileNotFoundException("The retired binary is missing.", retired);
        }

        if (File.Exists(current))
        {
            File.Delete(current);
        }

        File.Move(retired, current);
    }

    public int RemoveRetiredCopies(string currentPath)
    {
        var current = RequireFullPath(currentPath, nameof(currentPath));
        var directory = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(directory) || !Directory.Exists(directory))
        {
            return 0;
        }

        var removed = 0;
        var prefix = Path.GetFileName(current) + RetiredSuffix;
        foreach (var candidate in Directory.EnumerateFiles(directory, prefix + "*"))
        {
            try
            {
                File.Delete(candidate);
                removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        foreach (var candidate in Directory.EnumerateDirectories(directory, "wwwroot.old-*"))
        {
            try
            {
                Directory.Delete(candidate, recursive: true);
                removed++;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return removed;
    }

    private static string RequireFullPath(string value, string parameterName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value, parameterName);
        if (!Path.IsPathFullyQualified(value))
        {
            throw new ArgumentException("The path must be fully qualified.", parameterName);
        }

        return Path.GetFullPath(value);
    }
}
