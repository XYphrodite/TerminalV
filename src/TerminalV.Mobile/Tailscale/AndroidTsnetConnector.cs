#if ANDROID
using System.IO;

namespace TerminalV.Mobile.Tailscale;

/// <summary>
/// Android stub — вшитый Go AAR (org.terminalv.tsnet) пока не собран (tools/tsnet-bridge).
/// Собирается на Android без Android SDK ошибок: не трогает Android.App / Java.Lang.
/// </summary>
public sealed class AndroidTsnetConnector : ITailscaleConnector
{
    private bool _disposed;
    public bool IsRunning => false;
    public string? LastError => "Вшитый Tailscale не собран: Go AAR org.terminalv.tsnet отсутствует. Собери: tools/tsnet-bridge/build-aar.ps1 (Go + gomobile + Android SDK на xeon).";

    public Task StartAsync(TailscaleOptions options, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AndroidTsnetConnector));
        options.Validate();
        throw new PlatformNotSupportedException(LastError!);
    }

    public Task StopAsync() => Task.CompletedTask;

    public Task<Stream> DialAsync(string host, int port, CancellationToken ct = default)
        => throw new PlatformNotSupportedException("Tailscale Dial требует собранный Go AAR. Собери tools/tsnet-bridge на xeon. Пока: порт 22 без 'Через Tailscale' + отдельный Tailscale VPN, или 'Через шлюз'.");

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
#endif
