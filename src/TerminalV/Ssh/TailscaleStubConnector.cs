using System.IO;
namespace TerminalV.Ssh;

/// <summary>
/// Заглушка для платформ без Go AAR (Windows, сборка без gomobile, тесты).
/// Бросает понятную ошибку вместо PlatformNotSupported из глубины.
/// </summary>
public sealed class TailscaleStubConnector : ITailscaleConnector
{
    private bool _disposed;

    public bool IsRunning => false;
    public string? LastError => "Вшитый Tailscale не собран для этой платформы. Собери Go AAR: tools/tsnet-bridge/build-aar.ps1 (требует Go + gomobile + Android SDK на xeon).";

    public Task StartAsync(TailscaleOptions options, CancellationToken ct = default)
    {
        options.Validate();
        throw new PlatformNotSupportedException(LastError);
    }

    public Task StopAsync() => Task.CompletedTask;

    public Task<Stream> DialAsync(string host, int port, CancellationToken ct = default)
        => throw new PlatformNotSupportedException(LastError);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
    }
}
