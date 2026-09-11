using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;

namespace TerminalV.Update;

internal sealed class GitHubReleaseSource : IReleaseSource, IDisposable
{
    internal const string Repository = "XYphrodite/TerminalV";
    internal const string PackageAsset = "TerminalV-win-x64.zip";
    internal const string ChecksumAsset = "TerminalV-win-x64.zip.sha256";
    private const int MaximumTextAsset = 4096;

    private readonly HttpClient _client;

    public GitHubReleaseSource()
    {
        _client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        _client.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TerminalV-update", "1.0"));
    }

    public async Task<ReleaseDescriptor> ResolveAsync(string? tag, CancellationToken cancellationToken)
    {
        var resolvedTag = await ResolveTagAsync(tag, cancellationToken).ConfigureAwait(false);
        if (!ReleaseVersion.TryParse(resolvedTag, out var version))
        {
            throw new InvalidDataException("The published release tag is not a supported version.");
        }

        return new ReleaseDescriptor(
            resolvedTag,
            version,
            ReleaseAssetUrl(resolvedTag, PackageAsset),
            ReleaseAssetUrl(resolvedTag, ChecksumAsset));
    }

    private async Task<string> ResolveTagAsync(string? tag, CancellationToken cancellationToken)
    {
        if (tag is not null)
        {
            if (!ReleaseVersion.TryParse(tag, out var requested))
            {
                throw new InvalidDataException("The requested version is not a supported release tag.");
            }

            return $"v{requested}";
        }

        var latest = new Uri($"https://github.com/{Repository}/releases/latest");
        using var handler = new HttpClientHandler { AllowAutoRedirect = false };
        using var probe = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(30) };
        probe.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("TerminalV-update", "1.0"));

        using var response = await probe
            .GetAsync(latest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);

        var location = response.Headers.Location;
        if (location is not null && !location.IsAbsoluteUri)
        {
            location = new Uri(latest, location);
        }

        var path = location?.AbsolutePath ?? response.RequestMessage?.RequestUri?.AbsolutePath ?? "";
        const string marker = "/releases/tag/";
        var index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        if (index < 0)
        {
            using var followed = await _client
                .GetAsync(latest, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            path = followed.RequestMessage?.RequestUri?.AbsolutePath ?? "";
            index = path.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
        }

        if (index < 0)
        {
            throw new InvalidDataException("The latest release tag could not be resolved from GitHub.");
        }

        var resolved = path[(index + marker.Length)..].Trim('/');
        var slash = resolved.IndexOf('/');
        if (slash >= 0)
        {
            resolved = resolved[..slash];
        }

        if (!ReleaseVersion.TryParse(resolved, out var parsed))
        {
            throw new InvalidDataException("The latest release tag is not a supported version.");
        }

        return $"v{parsed}";
    }

    private static Uri ReleaseAssetUrl(string tag, string asset)
    {
        var address = new Uri($"https://github.com/{Repository}/releases/download/{tag}/{asset}");
        if (address.Scheme != Uri.UriSchemeHttps)
        {
            throw new InvalidDataException("The release asset address is not HTTPS.");
        }

        return address;
    }

    public async Task DownloadAsync(Uri address, string destinationPath, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationPath);

        using var response = await _client
            .GetAsync(address, HttpCompletionOption.ResponseHeadersRead, cancellationToken)
            .ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw Refused("The release asset could not be downloaded", response);
        }

        await using var source = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        await using var destination = new FileStream(destinationPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        await source.CopyToAsync(destination, cancellationToken).ConfigureAwait(false);
    }

    public async Task<string> ReadTextAsync(Uri address, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(address);

        using var response = await _client.GetAsync(address, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw Refused("The release checksum could not be downloaded", response);
        }

        var content = await response.Content.ReadAsStringAsync(cancellationToken).ConfigureAwait(false);
        if (content.Length > MaximumTextAsset)
        {
            throw new InvalidDataException("The release checksum asset is unexpectedly large.");
        }

        return content;
    }

    public void Dispose() => _client.Dispose();

    private static InvalidDataException Refused(string subject, HttpResponseMessage response) =>
        new(DescribeFailure(subject, response.StatusCode, response.Headers, DateTimeOffset.Now));

    internal static string DescribeFailure(
        string subject,
        HttpStatusCode status,
        HttpResponseHeaders headers,
        DateTimeOffset now)
    {
        if (IsRateLimited(status, headers))
        {
            if (ResetsAt(headers, now) is not { } reset)
            {
                return $"{subject}: GitHub's rate limit for unauthenticated requests is used up. " +
                       "It refills within the hour; try again then.";
            }

            var wait = reset - now;
            var again = wait <= TimeSpan.FromMinutes(1)
                ? "under a minute from now"
                : $"{(int)Math.Ceiling(wait.TotalMinutes)} minutes from now";
            return $"{subject}: GitHub's rate limit for unauthenticated requests is used up. " +
                   $"It resets at {reset.ToLocalTime():HH:mm} local time, {again}; try again then.";
        }

        return status switch
        {
            HttpStatusCode.NotFound =>
                $"{subject}: GitHub has nothing published at that address.",
            HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized =>
                $"{subject}: GitHub refused the request ({(int)status} {status}).",
            _ => $"{subject}: GitHub answered {(int)status} {status}."
        };
    }

    private static bool IsRateLimited(HttpStatusCode status, HttpResponseHeaders headers)
    {
        if (status == HttpStatusCode.TooManyRequests)
        {
            return true;
        }

        return status == HttpStatusCode.Forbidden &&
               headers.TryGetValues("X-RateLimit-Remaining", out var remaining) &&
               int.TryParse(remaining.FirstOrDefault(), out var left) && left == 0;
    }

    private static DateTimeOffset? ResetsAt(HttpResponseHeaders headers, DateTimeOffset now)
    {
        if (headers.TryGetValues("X-RateLimit-Reset", out var reset) &&
            long.TryParse(reset.FirstOrDefault(), out var seconds) &&
            seconds is > 0 and < 253_402_300_800)
        {
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }

        return headers.RetryAfter switch
        {
            { Delta: { } delta } => now + delta,
            { Date: { } date } => date,
            _ => null
        };
    }
}
