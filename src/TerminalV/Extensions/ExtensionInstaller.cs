using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text.Json;

namespace TerminalV.Extensions;

internal sealed class ExtensionInstallResult
{
    public required string Id { get; init; }
    public required string Name { get; init; }
    public required string Version { get; init; }
    public required string TargetDirectory { get; init; }
    public bool Replaced { get; init; }
    public bool Enabled { get; init; }
    public bool ChecksumVerified { get; init; }
}

/// <summary>
/// Installs an extension package from a local ZIP or an HTTPS link.
/// Extracts to a staging directory, validates the manifest, then moves into place.
/// </summary>
internal static class ExtensionInstaller
{
    private const long MaxDownloadBytes = 100 * 1024 * 1024;
    private const long MaxExtractBytes = 200 * 1024 * 1024;
    private const int MaxZipEntries = 5000;
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TerminalV-ext-install/1.0");
        return client;
    }

    public static ExtensionInstallResult Install(
        string source,
        string extensionsRoot,
        bool enable = true,
        bool replace = false,
        string? expectedSha256 = null,
        string? dataRoot = null,
        Action<string>? log = null)
    {
        if (string.IsNullOrWhiteSpace(source))
            throw new ArgumentException("A package URL or ZIP path is required.");
        if (string.IsNullOrWhiteSpace(extensionsRoot))
            throw new ArgumentException("Extensions root is required.");

        Directory.CreateDirectory(extensionsRoot);
        var work = Path.Combine(Path.GetTempPath(), "terminalv-ext-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        try
        {
            var zipPath = Path.Combine(work, "package.zip");
            var checksumVerified = false;
            if (IsRemote(source))
            {
                log?.Invoke($"Downloading {source}");
                checksumVerified = Download(source, zipPath, expectedSha256, log);
            }
            else
            {
                var local = Path.GetFullPath(source);
                if (!File.Exists(local))
                    throw new FileNotFoundException("Extension package not found.", source);
                File.Copy(local, zipPath, overwrite: true);
                checksumVerified = VerifyChecksum(zipPath, expectedSha256);
            }

            var extractDir = Path.Combine(work, "extract");
            ExtractZip(zipPath, extractDir);
            var packageDir = FindPackageDirectory(extractDir);
            using var manifestDoc = JsonDocument.Parse(File.ReadAllText(Path.Combine(packageDir, "extension.json")));
            var id = manifestDoc.RootElement.TryGetProperty("id", out var idValue) ? idValue.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
                throw new InvalidDataException("extension.json is missing id.");

            // Manifest.Read requires the package directory name to match the id.
            var named = Path.Combine(work, "pkg", id);
            Directory.CreateDirectory(Path.GetDirectoryName(named)!);
            Directory.Move(packageDir, named);
            var manifest = ExtensionManifest.Read(named);

            var target = Path.Combine(extensionsRoot, id);
            var replaced = false;
            string? backup = null;
            if (Directory.Exists(target))
            {
                if (!replace)
                    throw new InvalidOperationException(
                        $"Extension {id} is already installed. Close TerminalV and rerun with --replace to update.");
                backup = target + ".old-" + Guid.NewGuid().ToString("N");
                Directory.Move(target, backup);
                replaced = true;
            }

            try
            {
                MoveDirectory(named, target);
            }
            catch
            {
                if (backup is not null && Directory.Exists(backup) && !Directory.Exists(target))
                    MoveDirectory(backup, target);
                throw;
            }

            if (backup is not null)
            {
                try { Directory.Delete(backup, recursive: true); }
                catch { /* leftover backup is harmless */ }
            }

            // Re-validate after the move so the installed tree is what the manager will load.
            ExtensionManifest.Read(target);

            var enabled = false;
            if (enable)
            {
                if (string.IsNullOrWhiteSpace(dataRoot))
                    throw new ArgumentException("dataRoot is required to enable an extension.");
                var database = Path.Combine(dataRoot, "extensions.db");
                var registry = new ExtensionStorage(database, "$enabled");
                registry.Set(manifest.Id, "true");
                enabled = true;
            }

            return new ExtensionInstallResult
            {
                Id = manifest.Id,
                Name = manifest.Name,
                Version = manifest.Version,
                TargetDirectory = target,
                Replaced = replaced,
                Enabled = enabled,
                ChecksumVerified = checksumVerified
            };
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }
            catch { /* temp cleanup is best-effort */ }
        }
    }

    private static bool IsRemote(string source)
        => Uri.TryCreate(source, UriKind.Absolute, out var uri)
            && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp);

    private static bool Download(string source, string destination, string? expectedSha256, Action<string>? log)
    {
        if (!Uri.TryCreate(source, UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttps && uri.Scheme != Uri.UriSchemeHttp))
            throw new ArgumentException("Only http(s) links are supported.");
        if (uri.Scheme == Uri.UriSchemeHttp)
            log?.Invoke("Warning: plain http link; prefer https.");

        using var response = Http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
        response.EnsureSuccessStatusCode();
        var declared = response.Content.Headers.ContentLength;
        if (declared is > MaxDownloadBytes)
            throw new InvalidDataException("Extension package exceeds 100 MB.");

        using var body = response.Content.ReadAsStream();
        using var file = File.Create(destination);
        var buffer = new byte[81920];
        long total = 0;
        int read;
        while ((read = body.Read(buffer, 0, buffer.Length)) > 0)
        {
            total += read;
            if (total > MaxDownloadBytes)
                throw new InvalidDataException("Extension package exceeds 100 MB.");
            file.Write(buffer, 0, read);
        }

        if (expectedSha256 is null && uri.AbsolutePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            // Optional SHA256 sidecar used by TerminalV releases.
            var sidecar = uri.ToString() + ".sha256";
            try
            {
                using var sidecarResponse = Http.GetAsync(sidecar).GetAwaiter().GetResult();
                if (sidecarResponse.IsSuccessStatusCode)
                {
                    var text = sidecarResponse.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    expectedSha256 = text.Trim().Split(' ', '\t', '\n', '\r')[0];
                    log?.Invoke("Checksum sidecar found.");
                }
            }
            catch
            {
                // Sidecar is optional.
            }
        }

        return VerifyChecksum(destination, expectedSha256);
    }

    private static bool VerifyChecksum(string zipPath, string? expectedSha256)
    {
        if (string.IsNullOrWhiteSpace(expectedSha256)) return false;
        var expected = expectedSha256.Trim().ToLowerInvariant();
        if (expected.StartsWith("sha256:", StringComparison.Ordinal))
            expected = expected["sha256:".Length..];
        using var stream = File.OpenRead(zipPath);
        var hash = Convert.ToHexString(SHA256.HashData(stream)).ToLowerInvariant();
        if (!string.Equals(hash, expected, StringComparison.Ordinal))
            throw new InvalidDataException("SHA256 mismatch; package was not installed.");
        return true;
    }

    private static void ExtractZip(string zipPath, string destination)
    {
        Directory.CreateDirectory(destination);
        using var archive = ZipFile.OpenRead(zipPath);
        if (archive.Entries.Count == 0)
            throw new InvalidDataException("Extension package is empty.");
        if (archive.Entries.Count > MaxZipEntries)
            throw new InvalidDataException("Extension package has too many entries.");
        var destinationRoot = Path.GetFullPath(destination);
        long total = 0;
        foreach (var entry in archive.Entries)
        {
            var relative = entry.FullName.Replace('\\', '/');
            if (relative.StartsWith('/') || relative.Contains("..", StringComparison.Ordinal)
                || relative.Contains(':') || Path.IsPathRooted(relative))
                throw new InvalidDataException("Unsafe path in extension package.");
            if (string.IsNullOrEmpty(entry.Name)) continue;
            total += entry.Length;
            if (total > MaxExtractBytes)
                throw new InvalidDataException("Unpacked extension package exceeds 200 MB.");
            var output = Path.GetFullPath(Path.Combine(destinationRoot, relative));
            if (!output.StartsWith(destinationRoot + Path.DirectorySeparatorChar, StringComparison.Ordinal)
                && output != destinationRoot)
                throw new InvalidDataException("Unsafe path in extension package.");
            Directory.CreateDirectory(Path.GetDirectoryName(output)!);
            entry.ExtractToFile(output, overwrite: true);
        }
    }

    private static string FindPackageDirectory(string extractDir)
    {
        if (File.Exists(Path.Combine(extractDir, "extension.json")))
            return extractDir;
        var roots = Directory.GetDirectories(extractDir);
        foreach (var root in roots)
        {
            if (File.Exists(Path.Combine(root, "extension.json")))
                return root;
        }
        throw new InvalidDataException("extension.json not found in the package.");
    }

    private static void MoveDirectory(string source, string destination)
    {
        try
        {
            Directory.Move(source, destination);
        }
        catch (IOException)
        {
            // Temp and LocalApplicationData can sit on different volumes.
            Directory.CreateDirectory(destination);
            foreach (var file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(source, file);
                var target = Path.Combine(destination, relative);
                Directory.CreateDirectory(Path.GetDirectoryName(target)!);
                File.Copy(file, target, overwrite: true);
            }
            Directory.Delete(source, recursive: true);
        }
    }
}
