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

        var marker = Path.Combine(installDirectory, SelfUpdateService.PendingMarker);
        try
        {
            if (Directory.Exists(marker)) return;
            if (!File.Exists(marker))
            {
                CleanupRetired(exe, installDirectory);
                return;
            }
            // An invalid marker is discarded without touching the path it contains.
            if (!SelfUpdateService.IsRegularTree(marker) ||
                !TryGetPayload(installDirectory, File.ReadAllText(marker).Trim(), out var payload, out var staging))
            {
                File.Delete(marker);
                return;
            }

            if (Directory.Exists(staging) && !SelfUpdateService.IsRegularTree(staging)) return;
            ReplacePayload(installDirectory, payload);

            SelfUpdateService.TryDeleteDirectory(staging);
            File.Delete(marker);
            CleanupRetired(exe, installDirectory);
        }
        catch (IOException)
        {
            return;
        }
        catch (UnauthorizedAccessException)
        {
            return;
        }
    }

    private static bool TryGetPayload(string installDirectory, string value, out string payload, out string staging)
    {
        payload = staging = "";
        try
        {
            if (!Path.IsPathFullyQualified(value)) return false;
            payload = Path.GetFullPath(value);
            staging = Path.GetDirectoryName(payload) ?? "";
            return Path.GetFileName(payload).Equals("payload", StringComparison.OrdinalIgnoreCase) &&
                   IsOwnedStaging(installDirectory, staging);
        }
        catch (ArgumentException) { return false; }
        catch (NotSupportedException) { return false; }
        catch (IOException) { return false; }
    }

    private static bool IsOwnedStaging(string installDirectory, string staging)
    {
        var name = Path.GetFileName(staging);
        return string.Equals(Path.GetDirectoryName(staging), installDirectory, StringComparison.OrdinalIgnoreCase) &&
               name.StartsWith(SelfUpdateService.StagingPrefix, StringComparison.Ordinal) &&
               Guid.TryParseExact(name[SelfUpdateService.StagingPrefix.Length..], "N", out _);
    }

    private static void ReplacePayload(string installDirectory, string payload)
    {
        var stagedWwwroot = Path.Combine(payload, "wwwroot");
        var destWwwroot = Path.Combine(installDirectory, "wwwroot");
        var stagedCom = Path.Combine(payload, "TerminalV.com");
        var destCom = Path.Combine(installDirectory, "TerminalV.com");
        foreach (var destination in new[] { destWwwroot, destCom })
        {
            if (Path.Exists(destination) && !SelfUpdateService.IsRegularTree(destination))
                throw new IOException("An update destination contains a junction or symbolic link.");
        }

        string? retiredUi = null;
        var movedUi = false;
        try
        {
            if (Directory.Exists(stagedWwwroot))
            {
                if (Directory.Exists(destWwwroot))
                {
                    retiredUi = destWwwroot + ".old-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff",
                        System.Globalization.CultureInfo.InvariantCulture);
                    Directory.Move(destWwwroot, retiredUi);
                }
                Directory.Move(stagedWwwroot, destWwwroot);
                movedUi = true;
            }

            if (File.Exists(stagedCom))
            {
                if (File.Exists(destCom)) new ExecutableReplacer().Replace(destCom, stagedCom);
                else File.Move(stagedCom, destCom);
            }
        }
        catch
        {
            // Keep the staged payload and marker retryable if the console shim is locked.
            if (movedUi) Directory.Move(destWwwroot, stagedWwwroot);
            if (retiredUi is not null && !Directory.Exists(destWwwroot)) Directory.Move(retiredUi, destWwwroot);
            throw;
        }
    }

    private static void CleanupRetired(string exe, string installDirectory)
    {
        var replacer = new ExecutableReplacer();
        replacer.RemoveRetiredCopies(exe);
        replacer.RemoveRetiredCopies(Path.Combine(installDirectory, "TerminalV.com"));
        foreach (var directory in Directory.EnumerateDirectories(installDirectory, SelfUpdateService.StagingPrefix + "*"))
        {
            if (IsOwnedStaging(installDirectory, directory)) SelfUpdateService.TryDeleteDirectory(directory);
        }
    }
}
