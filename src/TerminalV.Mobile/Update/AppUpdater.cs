using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MobileAppInfo = TerminalV.Mobile.Host.AppInfo;

namespace TerminalV.Mobile.Update;

/// <summary>
/// Mobile self-update: fixed GitHub repository, the Android APK asset and its SHA256
/// sidecar. The repository is fixed in code on purpose — a configurable download origin
/// would be the shortest path from a stray setting to an APK of somebody else's choosing.
/// Same rule as the desktop SelfUpdateKit wiring: the checksum is mandatory and a
/// mismatched or missing one refuses the install. Windows staging/rollback is not
/// applicable here; Android replaces the package through the system installer.
/// </summary>
internal static class AppUpdater
{
    internal const string Repository = "XYphrodite/TerminalV";
    internal const string AssetName = "TerminalV-Mobile.apk";
    internal const string ChecksumAssetName = AssetName + ".sha256";
    internal const string UserAgent = "TerminalV-mobile-update/1.0";
    internal const string FileProviderAuthority = "com.terminalv.mobile.fileprovider";

    private static readonly HttpClient Http = CreateClient();

    /// <summary>
    /// Installing an APK is an Android capability. Other mobile targets report the
    /// same "unsupported" state the desktop shows for a non-installed copy.
    /// </summary>
    public static bool IsSupported { get; } =
#if ANDROID
        true;
#else
        false;
#endif

    public sealed record Report(
        string Status,
        string? Current = null,
        string? Latest = null,
        string? Tag = null,
        string? Message = null);

    public static Task<Report> CheckAsync(CancellationToken ct) =>
        RunAsync(install: false, ct);

    public static Task<Report> ApplyAsync(CancellationToken ct) =>
        RunAsync(install: true, ct);

    private static async Task<Report> RunAsync(bool install, CancellationToken ct)
    {
        var current = NormalizeVersion(MobileAppInfo.Version);
        try
        {
            using var release = await GetJsonAsync(
                $"https://api.github.com/repos/{Repository}/releases/latest", ct).ConfigureAwait(false);
            if (!TryReadAssets(release, out var tag, out var apkUrl, out var shaUrl))
                return new Report("error", current, Message: "В выпуске нет " + AssetName + ".");

            var latest = NormalizeVersion(tag);
            if (!IsNewer(latest, current))
                return new Report("current", current, latest, tag);

            if (!install)
                return new Report("available", current, latest, tag);

            var apkPath = await DownloadAsync(apkUrl, shaUrl, ct).ConfigureAwait(false);
            Install(apkPath);
            return new Report("installing", current, latest, tag);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            return new Report("error", current, Message: ex.Message);
        }
    }

    private static HttpClient CreateClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        // Full UA string: GitHub rejects requests without one. ParseAdd keeps the
        // slash that a ProductInfoHeaderValue product name would reject.
        client.DefaultRequestHeaders.UserAgent.ParseAdd(UserAgent);
        // GitHub rejects API calls without an Accept header in some configurations.
        client.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
        return client;
    }

    private static async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        return await JsonDocument.ParseAsync(stream, cancellationToken: ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Reads the release tag and the direct download URLs of the APK and its checksum.
    /// Only the two known asset names are accepted: a release cannot redirect the update
    /// to a different payload by shipping an extra file.
    /// </summary>
    private static bool TryReadAssets(JsonDocument document, out string tag, out string apkUrl, out string shaUrl)
    {
        tag = apkUrl = shaUrl = "";
        var root = document.RootElement;
        if (root.TryGetProperty("tag_name", out var tagProperty))
            tag = tagProperty.GetString() ?? "";
        if (tag.Length == 0 || !root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var nameProperty) ? nameProperty.GetString() : null;
            var url = asset.TryGetProperty("browser_download_url", out var urlProperty) ? urlProperty.GetString() : null;
            if (url is null) continue;
            if (name == AssetName) apkUrl = url;
            else if (name == ChecksumAssetName) shaUrl = url;
        }

        return apkUrl.Length > 0;
    }

    /// <summary>
    /// Downloads the APK next to its checksum and verifies the digest before the file
    /// is allowed to be installed. The payload is staged as .part so an interrupted
    /// download can never be mistaken for a complete one.
    /// </summary>
    private static async Task<string> DownloadAsync(string apkUrl, string shaUrl, CancellationToken ct)
    {
        var directory = Path.Combine(CacheDirectory, "updates");
        Directory.CreateDirectory(directory);
        var finalPath = Path.Combine(directory, AssetName);
        var stagingPath = finalPath + ".part";

        try
        {
            var expected = await DownloadChecksumAsync(shaUrl, ct).ConfigureAwait(false);
            if (expected is null)
                throw new InvalidOperationException("Не удалось получить контрольную сумму обновления.");

            await DownloadToFileAsync(apkUrl, stagingPath, ct).ConfigureAwait(false);

            var actual = await HashAsync(stagingPath, ct).ConfigureAwait(false);
            if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("Контрольная сумма обновления не совпала.");

            if (File.Exists(finalPath)) File.Delete(finalPath);
            File.Move(stagingPath, finalPath);
            return finalPath;
        }
        catch
        {
            try { if (File.Exists(stagingPath)) File.Delete(stagingPath); } catch (IOException) { }
            throw;
        }
    }

    private static async Task<string?> DownloadChecksumAsync(string shaUrl, CancellationToken ct)
    {
        using var response = await Http.GetAsync(shaUrl, ct).ConfigureAwait(false);
        if (!response.IsSuccessStatusCode) return null;
        var text = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        // Sidecar is "<hex digest>  <file name>"; only the digest is authoritative.
        var parts = text.Trim().Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 && parts[0].Length == 64 ? parts[0] : null;
    }

    private static async Task DownloadToFileAsync(string url, string path, CancellationToken ct)
    {
        using var response = await Http.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var source = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var target = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);
        await source.CopyToAsync(target, ct).ConfigureAwait(false);
    }

    private static async Task<string> HashAsync(string path, CancellationToken ct)
    {
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None, 81920, useAsync: true);
        using var sha = SHA256.Create();
        var digest = await sha.ComputeHashAsync(stream, ct).ConfigureAwait(false);
        return Convert.ToHexString(digest).ToLowerInvariant();
    }

    private static string CacheDirectory =>
#if ANDROID
        Android.App.Application.Context?.CacheDir?.AbsolutePath ?? Path.GetTempPath();
#else
        Path.GetTempPath();
#endif

    /// <summary>
    /// Triggers the platform installer. Android refuses silent APK installs, so the
    /// user always sees a system confirmation — one tap instead of a trip to GitHub.
    /// </summary>
    private static void Install(string apkPath)
    {
#if ANDROID
        var context = Android.App.Application.Context
            ?? throw new InvalidOperationException("Приложение недоступно для установки.");
#pragma warning disable CA1416 // Guarded by the SdkInt check below.
        if (Android.OS.Build.VERSION.SdkInt >= Android.OS.BuildVersionCodes.O &&
            context.PackageManager?.CanRequestPackageInstalls() == false)
        {
            var settings = new Android.Content.Intent(Android.Provider.Settings.ActionManageUnknownAppSources);
            settings.SetData(Android.Net.Uri.Parse("package:" + context.PackageName));
            settings.AddFlags(Android.Content.ActivityFlags.NewTask);
            context.StartActivity(settings);
            throw new InvalidOperationException(
                "Разрешите установку приложений из этого источника и повторите обновление.");
        }
#pragma warning restore CA1416

        var file = new Java.IO.File(apkPath);
        var uri = AndroidX.Core.Content.FileProvider.GetUriForFile(context, FileProviderAuthority, file);
        var intent = new Android.Content.Intent(Android.Content.Intent.ActionView);
        intent.SetDataAndType(uri, "application/vnd.android.package-archive");
        intent.AddFlags(Android.Content.ActivityFlags.NewTask | Android.Content.ActivityFlags.GrantReadUriPermission);
        context.StartActivity(intent);
#else
        throw new PlatformNotSupportedException("Установка обновлений поддерживается только на Android.");
#endif
    }

    /// <summary>
    /// Tags are "vX.Y.Z"; AppInfo.Version is "X.Y.Z". Anything that is not a numeric
    /// triple compares equal to itself and never counts as an upgrade.
    /// </summary>
    private static string NormalizeVersion(string? value)
    {
        var trimmed = value?.Trim() ?? "";
        if (trimmed.StartsWith("v", StringComparison.OrdinalIgnoreCase)) trimmed = trimmed[1..];
        return trimmed;
    }

    private static bool IsNewer(string candidate, string installed)
    {
        if (Version.TryParse(candidate, out var a) && Version.TryParse(installed, out var b))
            return a > b;
        return !string.Equals(candidate, installed, StringComparison.OrdinalIgnoreCase);
    }
}
