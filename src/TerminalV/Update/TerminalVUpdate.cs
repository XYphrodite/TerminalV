using System.IO;
using SelfUpdateKit;

namespace TerminalV.Update;

/// <summary>
/// TerminalV wiring for the shared SelfUpdateKit: fixed repository, package asset names for
/// both variants, payload layout, and the installed variant. Update rules (mandatory
/// checksum, staged probe with rollback, deferred payload) come from the library, not
/// from here. The repository is fixed in code on purpose: a configurable download origin
/// would be the shortest path from a stray setting to an executable of somebody else's
/// choosing.
/// </summary>
internal static class TerminalVUpdate
{
    internal const string Repository = "XYphrodite/TerminalV";
    internal const string FullPackageAsset = "TerminalV-win-x64.zip";
    internal const string LightPackageAsset = "TerminalV-win-x64-light.zip";
    internal const string StagingPrefix = ".terminalv-update-";
    internal const string PendingMarker = ".terminalv-pending-ui";
    internal const string VariantMarker = ".terminalv-variant";
    internal const string ProbeExecutable = "TerminalV.exe";

    internal enum Variant
    {
        Full,
        Light,
    }

    public static ReleaseSourceOptions Options(Variant variant)
    {
        var asset = variant == Variant.Light ? LightPackageAsset : FullPackageAsset;
        return new ReleaseSourceOptions
        {
            Repository = Repository,
            ExecutableAssetNames = { asset },
            ChecksumAssetName = asset + ".sha256",
            ChecksumMode = ChecksumMode.SidecarAsset,
            UseDirectDownloadUrls = true,
            UserAgent = "TerminalV-update/1.0",
            ExpectedMagic = [(byte)'P', (byte)'K'],
            PackageFileNames =
            {
                "TerminalV.exe",
                "TerminalV.com",
                Path.Combine("wwwroot", "index.html"),
            },
            ProbeExecutableNames = { ProbeExecutable },
            PendingMarkerFileName = PendingMarker,
            StagingPrefix = StagingPrefix,
        };
    }

    /// <summary>
    /// Which package the installation came from. The installer records it next to the
    /// executable; an installation from before variants defaults to the full package, so
    /// an update never silently changes what kind of build is installed.
    /// </summary>
    public static Variant InstalledVariant(string? processPath)
    {
        try
        {
            if (!string.IsNullOrWhiteSpace(processPath) && Path.IsPathFullyQualified(processPath))
            {
                var directory = Path.GetDirectoryName(Path.GetFullPath(processPath));
                if (!string.IsNullOrEmpty(directory))
                {
                    var marker = Path.Combine(directory, VariantMarker);
                    if (File.Exists(marker) &&
                        string.Equals(File.ReadAllText(marker).Trim(), "light", StringComparison.OrdinalIgnoreCase))
                        return Variant.Light;
                }
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return Variant.Full;
    }
}
