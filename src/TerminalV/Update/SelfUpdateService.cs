using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;

namespace TerminalV.Update;

internal enum SelfUpdateStatus
{
    AlreadyCurrent,
    UpdateAvailable,
    Updated
}

internal sealed record SelfUpdateReport(
    SelfUpdateStatus Status,
    ReleaseVersion Installed,
    ReleaseVersion Release,
    string Tag);

internal sealed class SelfUpdateService
{
    internal const string StagingPrefix = ".terminalv-update-";
    internal const string PendingMarker = ".terminalv-pending-ui";
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(30);

    private readonly string _executablePath;
    private readonly ReleaseVersion _installedVersion;
    private readonly IReleaseSource _source;
    private readonly IExecutableReplacer _replacer;

    public SelfUpdateService(
        string executablePath,
        ReleaseVersion installedVersion,
        IReleaseSource source,
        IExecutableReplacer? replacer = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(executablePath);
        if (!Path.IsPathFullyQualified(executablePath))
        {
            throw new ArgumentException("The executable path must be fully qualified.", nameof(executablePath));
        }

        _executablePath = Path.GetFullPath(executablePath);
        _installedVersion = installedVersion;
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _replacer = replacer ?? new ExecutableReplacer();
    }

    public async Task<SelfUpdateReport> CheckAsync(CancellationToken cancellationToken)
    {
        _replacer.RemoveRetiredCopies(_executablePath);
        var release = await _source.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
        if (release.Version <= _installedVersion)
        {
            return new SelfUpdateReport(SelfUpdateStatus.AlreadyCurrent, _installedVersion, release.Version, release.Tag);
        }

        return new SelfUpdateReport(SelfUpdateStatus.UpdateAvailable, _installedVersion, release.Version, release.Tag);
    }

    public async Task<SelfUpdateReport> ApplyAsync(
        CancellationToken cancellationToken,
        Action<long, long?>? progress = null)
    {
        var release = await _source.ResolveAsync(null, cancellationToken).ConfigureAwait(false);
        if (release.Version <= _installedVersion)
        {
            return new SelfUpdateReport(SelfUpdateStatus.AlreadyCurrent, _installedVersion, release.Version, release.Tag);
        }

        await InstallAsync(release, cancellationToken, progress).ConfigureAwait(false);
        return new SelfUpdateReport(SelfUpdateStatus.Updated, _installedVersion, release.Version, release.Tag);
    }

    private async Task InstallAsync(
        ReleaseDescriptor release,
        CancellationToken cancellationToken,
        Action<long, long?>? progress)
    {
        var installDirectory = Path.GetDirectoryName(_executablePath)
            ?? throw new InvalidOperationException("The install directory could not be determined.");
        var marker = Path.Combine(installDirectory, PendingMarker);
        if (Path.Exists(marker))
        {
            throw new InvalidOperationException("A previous update is pending. Restart TerminalV before updating again.");
        }

        var staging = Path.Combine(installDirectory, StagingPrefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(staging);
        var keepStaging = false;
        try
        {
            var zipPath = Path.Combine(staging, GitHubReleaseSource.PackageAsset);
            await _source.DownloadAsync(release.PackageUrl, zipPath, cancellationToken, progress).ConfigureAwait(false);
            var checksum = await _source.ReadTextAsync(release.ChecksumUrl, cancellationToken).ConfigureAwait(false);
            await VerifyZipAsync(zipPath, ReleaseChecksum.Parse(checksum), cancellationToken).ConfigureAwait(false);

            var payload = Path.Combine(staging, "payload");
            ZipFile.ExtractToDirectory(zipPath, payload);
            File.Delete(zipPath);

            foreach (var required in new[] { AppVersion.ExecutableName, "TerminalV.com", Path.Combine("wwwroot", "index.html") })
            {
                if (!File.Exists(Path.Combine(payload, required)))
                {
                    throw new InvalidDataException($"The downloaded release does not contain {required}.");
                }
            }

            var stagedExe = Path.Combine(payload, AppVersion.ExecutableName);
            if (!await ProbeAsync(stagedExe, cancellationToken).ConfigureAwait(false))
            {
                throw new InvalidDataException("The downloaded release did not run.");
            }

            var retired = _replacer.Replace(_executablePath, stagedExe);
            try
            {
                if (!await ProbeAsync(_executablePath, cancellationToken).ConfigureAwait(false))
                {
                    throw new InvalidDataException("The downloaded release did not run after installation.");
                }

                // Publish the marker atomically. Failure must roll back the EXE as well.
                var stagedMarker = Path.Combine(staging, "pending");
                File.WriteAllText(stagedMarker, payload);
                File.Move(stagedMarker, marker);
                keepStaging = true;
            }
            catch
            {
                _replacer.Restore(retired, _executablePath);
                throw;
            }

        }
        finally
        {
            if (!keepStaging)
            {
                TryDeleteDirectory(staging);
            }
        }
    }

    private static async Task VerifyZipAsync(string zipPath, string expectedHash, CancellationToken cancellationToken)
    {
        var info = new FileInfo(zipPath);
        if (!info.Exists || info.Length == 0)
        {
            throw new InvalidDataException("The downloaded release is empty.");
        }

        string actual;
        await using (var stream = new FileStream(zipPath, FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var header = new byte[2];
            if (await stream.ReadAsync(header, cancellationToken).ConfigureAwait(false) != 2 ||
                header[0] != (byte)'P' || header[1] != (byte)'K')
            {
                throw new InvalidDataException("The downloaded release is not a zip archive.");
            }

            stream.Position = 0;
            actual = ReleaseChecksum.Format(await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false));
        }

        if (!ReleaseChecksum.Matches(expectedHash, actual))
        {
            throw new InvalidDataException("The downloaded release failed its checksum.");
        }
    }

    private static async Task<bool> ProbeAsync(string executablePath, CancellationToken cancellationToken)
    {
        var start = new ProcessStartInfo(executablePath)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        start.ArgumentList.Add("--help");

        using var process = Process.Start(start);
        if (process is null)
        {
            return false;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        try
        {
            await process.WaitForExitAsync(timeout.Token).ConfigureAwait(false);
            await Task.WhenAll(stdout, stderr).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try
            {
                process.Kill(entireProcessTree: true);
                await process.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false);
            }
            catch (InvalidOperationException)
            {
            }

            cancellationToken.ThrowIfCancellationRequested();
            return false;
        }

        return process.ExitCode == 0;
    }

    internal static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path) && IsRegularTree(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    // Never follow junctions/symlinks when moving or retiring an update directory.
    internal static bool IsRegularTree(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) return false;
        if (!Directory.Exists(path)) return true;
        foreach (var entry in Directory.EnumerateFileSystemEntries(path))
        {
            if (!IsRegularTree(entry)) return false;
        }
        return true;
    }
}
