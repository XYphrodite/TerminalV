using TerminalV.Ssh;

namespace TerminalV.Mobile.Tailscale;

/// <summary>
/// Фасад над ITailscaleConnector. Выбирает реализацию по платформе:
/// Android → AndroidTsnetConnector (Go AAR, userspace), остальные → Stub.
/// Синглтон в DI, переиспользуется между SSH сессиями (tsnet один на процесс).
/// </summary>
public sealed class TailscaleService : ITailscaleService, ITailscaleConnector
{
    private readonly ITailscaleConnector _inner;
    private bool _disposed;

    public TailscaleService()
    {
        _inner = CreatePlatformConnector();
    }

    private static ITailscaleConnector CreatePlatformConnector()
    {
#if ANDROID
        try
        {
            // Пытаемся загрузить Android-биндинг, сгенерированный gomobile (org.terminalv.tsnet)
            var t = Type.GetType("TerminalV.Mobile.Tailscale.AndroidTsnetConnector, TerminalV.Mobile");
            if (t is not null && Activator.CreateInstance(t) is ITailscaleConnector c) return c;
        }
        catch { }
        // Fallback: проверим Java класс напрямую через JNI позже
#endif
        return new TailscaleStubConnector();
    }

    public bool IsRunning => _inner.IsRunning;
    public string? LastError => _inner.LastError;
    public event Action<string>? StateChanged;

    public async Task StartAsync(TailscaleOptions options, CancellationToken ct = default)
    {
        options.Validate();
        try
        {
            await _inner.StartAsync(options, ct).ConfigureAwait(false);
            StateChanged?.Invoke("running");
        }
        catch (Exception ex)
        {
            StateChanged?.Invoke($"error: {ex.Message}");
            throw;
        }
    }

    public Task StopAsync() => _inner.StopAsync();
    public Task<Stream> DialAsync(string host, int port, CancellationToken ct = default) => _inner.DialAsync(host, port, ct);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _inner.Dispose(); } catch { }
    }
}
