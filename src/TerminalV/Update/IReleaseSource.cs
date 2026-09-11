namespace TerminalV.Update;

internal sealed record ReleaseDescriptor(string Tag, ReleaseVersion Version, Uri PackageUrl, Uri ChecksumUrl);

internal interface IReleaseSource
{
    Task<ReleaseDescriptor> ResolveAsync(string? tag, CancellationToken cancellationToken);

    Task DownloadAsync(Uri address, string destinationPath, CancellationToken cancellationToken);

    Task<string> ReadTextAsync(Uri address, CancellationToken cancellationToken);
}
