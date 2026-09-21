using System.IO;
namespace TerminalV.Ssh;

/// <summary>
/// Абстракция над вшитым tsnet. Реализация на Android — Go AAR (gomobile) с userspace netstack,
/// на остальных — stub. Не требует системного VPNService, работает рядом с Happ.
/// </summary>
public interface ITailscaleConnector : IDisposable
{
    /// <summary>Запустить tsnet. Идемпотентно. Бросает если уже faulted.</summary>
    Task StartAsync(TailscaleOptions options, CancellationToken ct = default);

    /// <summary>Остановить и освободить TUN/state.</summary>
    Task StopAsync();

    /// <summary>Dial TCP через tailnet (userspace). Используется для SSH.</summary>
    Task<Stream> DialAsync(string host, int port, CancellationToken ct = default);

    bool IsRunning { get; }
    string? LastError { get; }
}
