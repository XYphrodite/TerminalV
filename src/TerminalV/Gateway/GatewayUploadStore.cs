using System.IO;
using TerminalV.Ssh;

namespace TerminalV.Gateway;

internal sealed class GatewayUploadTooLargeException(string message) : InvalidOperationException(message);

internal static class GatewayUploadStore
{
    public const long MaxUploadBytes = SshFileUploader.MaxUploadBytes;

    public static string DefaultDirectory { get; } =
        Path.Combine(Path.GetTempPath(), "TerminalV", "pastes");

    public static async Task<string> SaveStreamAsync(Stream source, string? fileName,
        string? directory = null, long maxBytes = MaxUploadBytes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (maxBytes < 1)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        var targetDir = string.IsNullOrWhiteSpace(directory) ? DefaultDirectory : directory;
        Directory.CreateDirectory(targetDir);

        var ext = SshFileUploader.SanitizeExtension(fileName);
        var path = Path.Combine(targetDir, $"paste-{Guid.NewGuid():N}{ext}");
        try
        {
            var buffer = new byte[65536];
            long total = 0;
            await using var file = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None,
                bufferSize: 65536, useAsync: true);
            while (true)
            {
                var read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                    break;
                total += read;
                if (total > maxBytes)
                    throw new GatewayUploadTooLargeException("Файл слишком большой.");
                await file.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
            }
            await file.FlushAsync(cancellationToken).ConfigureAwait(false);
            return path;
        }
        catch
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
            throw;
        }
    }
}
