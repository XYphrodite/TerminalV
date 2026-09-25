using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace TerminalV.Extensions;

internal sealed class ExtensionManifest
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    public string Id { get; set; } = "";
    public string Name { get; set; } = "";
    public string Version { get; set; } = "";
    public string? Description { get; set; }
    public int ApiVersion { get; set; }
    // UI modules and their assets must live below the package's ui/ directory.
    public string? Ui { get; set; }
    public ExtensionHostEntry? Host { get; set; }

    public static ExtensionManifest Read(string directory)
    {
        var path = ResolveFile(directory, "extension.json");
        if (new FileInfo(path).Length > 64 * 1024)
            throw new InvalidDataException("extension.json exceeds 64 KiB.");
        var manifest = JsonSerializer.Deserialize<ExtensionManifest>(File.ReadAllText(path), Json)
            ?? throw new InvalidDataException("Empty extension manifest.");
        if (!Regex.IsMatch(manifest.Id ?? "", @"^[a-z0-9]([a-z0-9-]{0,38}[a-z0-9])?\.[a-z0-9]([a-z0-9-]{0,38}[a-z0-9])?$"))
            throw new InvalidDataException("Extension id must be publisher.name (lowercase letters, digits, hyphens).");
        if (!string.Equals(Path.GetFileName(directory), manifest.Id, StringComparison.Ordinal))
            throw new InvalidDataException("The package directory must match its extension id.");
        if (manifest.ApiVersion != 1)
            throw new InvalidDataException($"Unsupported extension API version: {manifest.ApiVersion}.");
        if (string.IsNullOrWhiteSpace(manifest.Name) || manifest.Name.Length > 100
            || !Regex.IsMatch(manifest.Version ?? "", @"^\d+\.\d+\.\d+(-[0-9A-Za-z.-]+)?(\+[0-9A-Za-z.-]+)?$"))
            throw new InvalidDataException("A name (up to 100 characters) and version (x.y.z) are required.");
        if (manifest.Ui is null && manifest.Host is null)
            throw new InvalidDataException("An extension needs a ui or host entry point.");
        if (manifest.Ui is not null)
        {
            if (!manifest.Ui.StartsWith("ui/", StringComparison.Ordinal)
                || !manifest.Ui.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The ui entry must be a JavaScript module below ui/.");
            ResolveFile(Path.Combine(directory, "ui"), manifest.Ui[3..]);
        }
        if (manifest.Host is not null)
        {
            if (string.IsNullOrWhiteSpace(manifest.Host.Type)
                || string.IsNullOrWhiteSpace(manifest.Host.Assembly)
                || !manifest.Host.Assembly.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("The host entry requires an assembly DLL and a type.");
            ResolveFile(directory, manifest.Host.Assembly);
        }
        return manifest;
    }

    internal static string ResolveFile(string root, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative)
            || relative.Contains(':') || relative.Contains('\\'))
            throw new InvalidDataException("Package paths must use relative forward-slash paths.");
        var segments = relative.Split('/');
        if (segments.Any(part => part is "" or "." or ".." || part.EndsWith('.') || part.EndsWith(' ')))
            throw new InvalidDataException("Invalid package path.");
        var current = Path.GetFullPath(root);
        RejectLink(current);
        foreach (var segment in segments)
        {
            current = Path.Combine(current, segment);
            RejectLink(current);
        }
        if (!File.Exists(current)) throw new FileNotFoundException("Extension file not found.", relative);
        return current;
    }

    private static void RejectLink(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Extension entry paths cannot contain links or junctions.");
    }
}

internal sealed class ExtensionHostEntry
{
    public string Assembly { get; set; } = "";
    public string Type { get; set; } = "";
}
