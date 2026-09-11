using System.IO;

namespace TerminalV.Update;

internal static class PendingUpdateApplier
{
    public static void Apply(string? processPath)
    {
        if (string.IsNullOrWhiteSpace(processPath) || !Path.IsPathFullyQualified(processPath))
        {
            return;
        }

        var exe = Path.GetFullPath(processPath);
        var installDirectory = Path.GetDirectoryName(exe);
        if (string.IsNullOrEmpty(installDirectory))
        {
            return;
        }

        new ExecutableReplacer().RemoveRetiredCopies(exe);

        var marker = Path.Combine(installDirectory, SelfUpdateService.PendingMarker);
        if (!File.Exists(marker))
        {
            CleanupOrphanStaging(installDirectory);
            return;
        }

        try
        {
            var payload = File.ReadAllText(marker).Trim();
            var stagedWwwroot = Path.Combine(payload, "wwwroot");
            var destWwwroot = Path.Combine(installDirectory, "wwwroot");
            if (Directory.Exists(stagedWwwroot))
            {
                ReplaceWwwroot(destWwwroot, stagedWwwroot);
            }

            SelfUpdateService.TryDeleteDirectory(Path.GetDirectoryName(payload) ?? payload);
            File.Delete(marker);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }

        CleanupOrphanStaging(installDirectory);
    }

    private static void ReplaceWwwroot(string destWwwroot, string stagedWwwroot)
    {
        if (Directory.Exists(destWwwroot))
        {
            var retired = destWwwroot + ".old-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff");
            Directory.Move(destWwwroot, retired);
            try
            {
                Directory.Move(stagedWwwroot, destWwwroot);
                SelfUpdateService.TryDeleteDirectory(retired);
            }
            catch
            {
                if (!Directory.Exists(destWwwroot) && Directory.Exists(retired))
                {
                    Directory.Move(retired, destWwwroot);
                }

                throw;
            }
        }
        else
        {
            Directory.Move(stagedWwwroot, destWwwroot);
        }
    }

    private static void CleanupOrphanStaging(string installDirectory)
    {
        foreach (var directory in Directory.EnumerateDirectories(installDirectory, SelfUpdateService.StagingPrefix + "*"))
        {
            SelfUpdateService.TryDeleteDirectory(directory);
        }
    }
}
