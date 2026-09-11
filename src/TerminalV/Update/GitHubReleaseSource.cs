using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;

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
        string address;
        if (tag is null)
        {
            address = $"https://api.github.com/repos/{Repository}/releases/latest";
        }
        else
        {
            if (!ReleaseVersion.TryParse(tag, out var requested))
            {
                throw new InvalidDataException("The requested version is not a supported release tag.");
            }

            address = $"https://api.github.com/repos/{Repository}/releases/tags/v{requested}";
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, address);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");

        using var response = await _client.SendAsync(request, cancellationToken).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode)
        {
            throw Refused("The release could not be read from GitHub", response);
        }

        await using var body = await response.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
        using var document = await JsonDocument.ParseAsync(body, cancellationToken: cancellationToken).ConfigureAwait(false);
        var root = document.RootElement;

        if (!root.TryGetProperty("tag_name", out var tagName) || tagName.GetString() is not { } resolvedTag)
        {
            throw new InvalidDataException("The release carries no tag.");
        }

        if (!ReleaseVersion.TryParse(resolvedTag, out var version))
        {
            throw new InvalidDataException("The published release tag is not a supported version.");
        }

        return new ReleaseDescriptor(
            resolvedTag,
            version,
            AssetUrl(root, PackageAsset),
            AssetUrl(root, ChecksumAsset));
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

    private static Uri AssetUrl(JsonElement release, string name)
    {
        if (!release.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidDataException("The release carries no assets.");
        }

        foreach (var asset in assets.EnumerateArray())
        {
            if (!asset.TryGetProperty("name", out var assetName) ||
                !string.Equals(assetName.GetString(), name, StringComparison.Ordinal))
            {
                continue;
            }

            if (!asset.TryGetProperty("browser_download_url", out var url) || url.GetString() is not { } value)
            {
                break;
            }

            if (!Uri.TryCreate(value, UriKind.Absolute, out var address) ||
                address.Scheme != Uri.UriSchemeHttps ||
                !(address.Host is "github.com" ||
                  address.Host.EndsWith(".githubusercontent.com", StringComparison.Ordinal)))
            {
                throw new InvalidDataException("The release asset address is not a GitHub download URL.");
            }

            return address;
        }

        throw new InvalidDataException("The release does not carry the expected assets.");
    }
}
