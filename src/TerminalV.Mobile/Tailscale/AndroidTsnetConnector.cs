#if ANDROID
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.App;
using Android.Content;
using TerminalV.Ssh;

namespace TerminalV.Mobile.Tailscale;

/// <summary>
/// Android реализация через Go AAR (gomobile) org.terminalv.tsnet.Tsnet.
/// Работает в userspace, не требует BIND_VPN_SERVICE — только INTERNET.
/// Если AAR не собран (tools/tsnet-bridge не собран), бросает PlatformNotSupportedException с хинтом.
/// </summary>
public sealed class AndroidTsnetConnector : ITailscaleConnector
{
    private object? _tsnet;
    private bool _running;
    private string? _lastError;
    private bool _disposed;

    public bool IsRunning => _running;
    public string? LastError => _lastError;

    public Task StartAsync(TailscaleOptions options, CancellationToken ct = default)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(AndroidTsnetConnector));
        if (_running) return Task.CompletedTask;
        options.Validate();

        return Task.Run(() =>
        {
            try
            {
                var tsnetClass = Java.Lang.Class.ForName("org.terminalv.tsnet.Tsnet");
                if (tsnetClass is null)
                    throw new PlatformNotSupportedException("Go AAR org.terminalv.tsnet.Tsnet не найден. Собери: tools/tsnet-bridge/build-aar.ps1 на xeon (Go + gomobile + Android SDK).");

                var ctx = global::Android.App.Application.Context;
                var filesDir = ctx.FilesDir?.AbsolutePath ?? "/data/data/com.companyname.terminalvmobile/files";
                var stateDir = string.IsNullOrWhiteSpace(options.StateDir) ? Path.Combine(filesDir, "tsnet-state") : options.StateDir!;
                Directory.CreateDirectory(stateDir);

                // Stub: AAR not yet integrated — throw with hint to use external VPN or gateway
                throw new PlatformNotSupportedException("Вшитый Tailscale не собран: Go AAR org.terminalv.tsnet отсутствует. Собери: tools/tsnet-bridge/build-aar.ps1 (требует Go + gomobile + Android SDK на xeon). Текущий apk — stub, требует отдельного Tailscale VPN.");
            }
            catch (Java.Lang.ClassNotFoundException)
            {
                throw new PlatformNotSupportedException("Вшитый Tailscale не собран: Go AAR org.terminalv.tsnet отсутствует. Собери: tools/tsnet-bridge/build-aar.ps1 (требует Go + gomobile + Android SDK на xeon). Текущий apk — stub, требует отдельного Tailscale VPN.");
            }
            catch (PlatformNotSupportedException) { throw; }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                throw new InvalidOperationException($"Tailscale tsnet start failed: {ex.Message} — проверь auth key / INTERNET permission.", ex);
            }
        }, ct);
    }

    public Task StopAsync()
    {
        if (!_running) return Task.CompletedTask;
        return Task.Run(() =>
        {
            try
            {
                if (_tsnet is not null)
                {
                    var cls = _tsnet.GetType();
                    var m = cls.GetMethod("stop") ?? cls.GetMethod("Stop");
                    m?.Invoke(_tsnet, null);
                }
            }
            catch { }
            finally
            {
                _running = false;
                _tsnet = null;
            }
        });
    }

    public Task<Stream> DialAsync(string host, int port, CancellationToken ct = default)
    {
        if (!_running || _tsnet is null)
            throw new InvalidOperationException("Tailscale не запущен. Сначала StartAsync с auth key.");

        throw new PlatformNotSupportedException("Tailscale Dial требует собранный Go AAR (tsnet). Собери tools/tsnet-bridge на xeon и пересобери apk. Пока используй: Порт 22 без 'Через Tailscale' + отдельный Tailscale VPN, или 'Через шлюз' ws://100.119.48.15:5454.");
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
    }
}
#endif
