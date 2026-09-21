using TerminalV.Ssh;

namespace TerminalV.Mobile.Tailscale;

/// <summary>
/// Платформенный сервис вшитого Tailscale. На Android — Go AAR (tsnet), на остальных — stub.
/// Жизненный цикл: Start с authKey → Dial для SSH.
/// </summary>
public interface ITailscaleService : IDisposable
{
    Task StartAsync(TailscaleOptions options, CancellationToken ct = default);
    Task StopAsync();
    Task<Stream> DialAsync(string host, int port, CancellationToken ct = default);
    bool IsRunning { get; }
    string? LastError { get; }
    event Action<string>? StateChanged;
}
