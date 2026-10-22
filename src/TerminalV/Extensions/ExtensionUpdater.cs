using System.IO;
using System.Net;
using System.Net.Http;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerminalV.Extensions;

internal sealed record ExtensionUpdateInfo(
    string Id,
    string Name,
    string InstalledVersion,
    string AvailableVersion,
    string PackageUrl,
    string? Sha256);

/// <summary>Compares extension manifest versions (x.y.z[-pre][+build]).</summary>
internal static class ExtensionVersion
{
    private static readonly Regex Pattern = new(
        @"^(\d+)\.(\d+)\.(\d+)(?:-([0-9A-Za-z.-]+))?(?:\+[0-9A-Za-z.-]+)?$", RegexOptions.CultureInvariant);

    public static bool TryParse(string? text, out (long Major, long Minor, long Patch, string? Pre) version)
    {
        version = default;
        var match = Pattern.Match(text ?? "");
        if (!match.Success) return false;
        if (!long.TryParse(match.Groups[1].Value, out var major)
            || !long.TryParse(match.Groups[2].Value, out var minor)
            || !long.TryParse(match.Groups[3].Value, out var patch))
            return false;
        version = (major, minor, patch, match.Groups[4].Success ? match.Groups[4].Value : null);
        return true;
    }

    /// <summary>Positive when <paramref name="left"/> is newer. Unparseable versions never win.</summary>
    public static int Compare(string? left, string? right)
    {
        var l = TryParse(left, out var a);
        var r = TryParse(right, out var b);
        if (!l || !r) return l == r ? 0 : l ? 1 : -1;
        var core = a.Major.CompareTo(b.Major);
        if (core != 0) return core;
        core = a.Minor.CompareTo(b.Minor);
        if (core != 0) return core;
        core = a.Patch.CompareTo(b.Patch);
        if (core != 0) return core;
        // A release is newer than any of its pre-releases.
        if (a.Pre is null || b.Pre is null) return a.Pre is null ? (b.Pre is null ? 0 : 1) : -1;
        return string.CompareOrdinal(a.Pre, b.Pre);
    }
}

/// <summary>
/// Looks for newer extension packages in the latest TerminalV GitHub release and stages them.
/// Staged packages live next to the extensions folder and are moved into place on the next
/// launch, before any extension DLL is loaded, so a running extension is never replaced.
/// </summary>
internal static class ExtensionUpdater
{
    public const string DefaultBaseUrl = "https://github.com/XYphrodite/TerminalV/releases/latest/download";
    private const string IndexAsset = "extensions.json";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("TerminalV-ext-update/1.0");
        return client;
    }

    public static string PendingRoot(string extensionsRoot)
    {
        var full = Path.GetFullPath(extensionsRoot)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return Path.Combine(Path.GetDirectoryName(full) ?? full, "extensions-pending");
    }

    private static readonly Regex BackupName = new(@"\.old-[0-9a-f]{32}$", RegexOptions.CultureInvariant);

    /// <summary>Replaced packages are renamed to "&lt;id&gt;.old-&lt;guid&gt;" before deletion; these are not extensions.</summary>
    public static bool IsBackupDirectory(string path) => BackupName.IsMatch(Path.GetFileName(path));

    /// <summary>Deletes leftover backups of replaced packages (a locked DLL can make the first delete fail).</summary>
    public static void CleanupBackups(string extensionsRoot)
    {
        if (!Directory.Exists(extensionsRoot)) return;
        foreach (var directory in Directory.EnumerateDirectories(extensionsRoot).Where(IsBackupDirectory).ToArray())
        {
            try { Directory.Delete(directory, recursive: true); }
            catch { /* still locked by a running window; retried on the next launch */ }
        }
    }

    public static string? PendingVersion(string extensionsRoot, string id)
    {
        var directory = Path.Combine(PendingRoot(extensionsRoot), id);
        if (!Directory.Exists(directory)) return null;
        try { return ExtensionManifest.Read(directory).Version; }
        catch { return null; }
    }

    /// <summary>
    /// Returns installed extensions that have a newer package in the latest release.
    /// Extensions that the release does not publish are skipped. Network failures throw.
    /// </summary>
    public static IReadOnlyList<ExtensionUpdateInfo> Check(
        string extensionsRoot, string? baseUrl = null, Action<string>? log = null)
    {
        var root = (string.IsNullOrWhiteSpace(baseUrl) ? DefaultBaseUrl : baseUrl).TrimEnd('/');
        var installed = ReadInstalled(extensionsRoot);
        var updates = new List<ExtensionUpdateInfo>();
        if (installed.Count == 0) return updates;

        var index = FetchIndex(root, log);
        foreach (var manifest in installed)
        {
            string? version;
            string url;
            string? sha = null;
            if (index is not null)
            {
                if (!index.TryGetValue(manifest.Id, out var entry)) continue;
                version = entry.Version;
                url = $"{root}/{Uri.EscapeDataString(entry.Asset)}";
                sha = entry.Sha256;
            }
            else
            {
                // Releases without extensions.json: read the version from the package itself.
                url = $"{root}/{Uri.EscapeDataString(manifest.Id)}.zip";
                version = ProbeVersion(url, log);
                if (version is null) continue;
            }

            if (ExtensionVersion.Compare(version, manifest.Version) > 0)
                updates.Add(new ExtensionUpdateInfo(manifest.Id, manifest.Name, manifest.Version, version, url, sha));
        }
        return updates;
    }

    /// <summary>Downloads, verifies and validates the package into the pending folder.</summary>
    public static ExtensionInstallResult Stage(
        ExtensionUpdateInfo update, string extensionsRoot, Action<string>? log = null)
    {
        var result = ExtensionInstaller.Install(update.PackageUrl, PendingRoot(extensionsRoot),
            enable: false, replace: true, expectedSha256: update.Sha256, log: log);
        if (!string.Equals(result.Id, update.Id, StringComparison.Ordinal))
        {
            try { Directory.Delete(result.TargetDirectory, recursive: true); }
            catch { /* best effort */ }
            throw new InvalidDataException($"The downloaded package is {result.Id}, expected {update.Id}.");
        }
        return result;
    }

    /// <summary>Moves staged packages into the extensions folder. Returns the ids that were updated.</summary>
    public static IReadOnlyList<string> ApplyPending(string extensionsRoot, Action<string>? log = null)
    {
        var applied = new List<string>();
        var pending = PendingRoot(extensionsRoot);
        if (!Directory.Exists(pending)) return applied;
        Directory.CreateDirectory(extensionsRoot);

        foreach (var package in Directory.EnumerateDirectories(pending).ToArray())
        {
            try
            {
                var manifest = ExtensionManifest.Read(package);
                var target = Path.Combine(extensionsRoot, manifest.Id);
                string? backup = null;
                if (Directory.Exists(target))
                {
                    string? current = null;
                    try { current = ExtensionManifest.Read(target).Version; }
                    catch { /* a broken installed copy is replaced */ }
                    if (current is not null && ExtensionVersion.Compare(manifest.Version, current) <= 0)
                    {
                        Directory.Delete(package, recursive: true);
                        continue;
                    }
                    backup = target + ".old-" + Guid.NewGuid().ToString("N");
                    Directory.Move(target, backup);
                }

                try
                {
                    ExtensionInstaller.MoveDirectory(package, target);
                }
                catch
                {
                    if (backup is not null && Directory.Exists(backup) && !Directory.Exists(target))
                        ExtensionInstaller.MoveDirectory(backup, target);
                    throw;
                }

                if (backup is not null)
                {
                    try { Directory.Delete(backup, recursive: true); }
                    catch { /* leftover backup is harmless */ }
                }
                applied.Add(manifest.Id);
                log?.Invoke($"Extension {manifest.Id} updated to {manifest.Version}.");
            }
            catch (InvalidDataException ex)
            {
                // A corrupt staged package can never be applied; drop it.
                log?.Invoke($"Discarded invalid staged extension {Path.GetFileName(package)}: {ex.Message}");
                try { Directory.Delete(package, recursive: true); }
                catch { /* best effort */ }
            }
            catch (Exception ex)
            {
                // Typically files locked by another running TerminalV window; retry next launch.
                log?.Invoke($"Staged extension {Path.GetFileName(package)} not applied: {ex.Message}");
            }
        }

        try
        {
            if (!Directory.EnumerateFileSystemEntries(pending).Any()) Directory.Delete(pending);
        }
        catch { /* best effort */ }
        return applied;
    }

    private static List<ExtensionManifest> ReadInstalled(string extensionsRoot)
    {
        var list = new List<ExtensionManifest>();
        if (!Directory.Exists(extensionsRoot)) return list;
        foreach (var directory in Directory.EnumerateDirectories(extensionsRoot).Where(d => !IsBackupDirectory(d))
            .Order(StringComparer.OrdinalIgnoreCase))
        {
            try { list.Add(ExtensionManifest.Read(directory)); }
            catch { /* broken packages are reported by the manager, not updated */ }
        }
        return list;
    }

    private sealed record IndexEntry(string Version, string Asset, string? Sha256);

    private static Dictionary<string, IndexEntry>? FetchIndex(string root, Action<string>? log)
    {
        using var response = Http.GetAsync($"{root}/{IndexAsset}").GetAwaiter().GetResult();
        if (response.StatusCode == HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        var text = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
        if (text.Length > 256 * 1024) return null;
        try
        {
            using var doc = JsonDocument.Parse(text);
            if (!doc.RootElement.TryGetProperty("extensions", out var items) || items.ValueKind != JsonValueKind.Array)
                return null;
            var map = new Dictionary<string, IndexEntry>(StringComparer.Ordinal);
            foreach (var item in items.EnumerateArray())
            {
                var id = Str(item, "id");
                var version = Str(item, "version");
                var asset = Str(item, "asset");
                if (id is null || version is null || asset is null) continue;
                if (asset.Contains('/') || asset.Contains('\\') || !asset.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
                    continue;
                map[id] = new IndexEntry(version, asset, Str(item, "sha256"));
            }
            return map;
        }
        catch (JsonException ex)
        {
            log?.Invoke($"Ignoring malformed {IndexAsset}: {ex.Message}");
            return null;
        }
    }

    private static string? ProbeVersion(string url, Action<string>? log)
    {
        var temp = Path.Combine(Path.GetTempPath(), "terminalv-ext-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            return ExtensionInstaller.Install(url, temp, enable: false, replace: true, log: null).Version;
        }
        catch (HttpRequestException ex) when (ex.StatusCode == HttpStatusCode.NotFound)
        {
            return null;
        }
        catch (InvalidDataException ex)
        {
            log?.Invoke($"Skipping {url}: {ex.Message}");
            return null;
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); }
            catch { /* best effort */ }
        }
    }

    private static string? Str(JsonElement element, string name)
        => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value)
            && value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString())
            ? value.GetString() : null;
}
