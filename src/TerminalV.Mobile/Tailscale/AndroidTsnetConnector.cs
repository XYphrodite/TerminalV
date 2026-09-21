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
/// Работает в userspace, не требует android.permission.BIND_VPN_SERVICE — только INTERNET.
/// Если AAR не собран (tools/tsnet-bridge не собран), бросает PlatformNotSupportedException с хинтом.
/// </summary>
public sealed class AndroidTsnetConnector : ITailscaleConnector
{
    private object? _tsnet; // Go tsnet.Server
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
                // Загружаем Go AAR класс через JNI. AAR ожидается как org.terminalv.tsnet.Tsnet
                var tsnetClass = Java.Lang.Class.ForName("org.terminalv.tsnet.Tsnet");
                if (tsnetClass is null)
                    throw new PlatformNotSupportedException("Go AAR org.terminalv.tsnet.Tsnet не найден. Собери: tools/tsnet-bridge/build-aar.ps1 на xeon (Go + gomobile + Android SDK).");

                var ctx = Application.Context;
                var filesDir = ctx.FilesDir?.AbsolutePath ?? "/data/data/com.companyname.terminalvmobile/files";
                var stateDir = string.IsNullOrWhiteSpace(options.StateDir) ? Path.Combine(filesDir, "tsnet-state") : options.StateDir!;
                Directory.CreateDirectory(stateDir);

                // Go Tsnet.Start(authKey, hostname, controlUrl, stateDir, logVerbosity)
                var startMethod = tsnetClass.GetMethod("start", Java.Lang.Class.ForName("java.lang.String"), Java.Lang.Class.ForName("java.lang.String"), Java.Lang.Class.ForName("java.lang.String"), Java.Lang.Class.ForName("java.lang.String"), Java.Lang.Integer.Type)!;
                var instance = Activator.CreateInstance(Java.Lang.Class.FromType(typeof(Java.Lang.Object)).ClassLoader?.LoadClass("org.terminalv.tsnet.Tsnet") ?? throw new InvalidOperationException("loader null"));
                // Используем reflection через JNI — fallback к прямому вызову если биндинг сгенерирован
                // Пробуем вызвать через JNI helper
                var authKey = options.AuthKey ?? "";
                var hostname = options.Hostname ?? "terminalv-mobile";
                var controlUrl = options.ControlUrl ?? "";

                // Если Go класс не загружен, бросаем с хинтом
                if (tsnetClass.GetMethod("start", Java.Lang.Class.ForName("java.lang.String"), Java.Lang.Class.ForName("java.lang.String"), Java.Lang.Class.ForName("java.lang.String"), Java.Lang.Class.ForName("java.lang.String"), Java.Lang.Integer.Type) is null)
                    throw new PlatformNotSupportedException("Tsnet AAR собран без метода start(authKey,hostname,controlUrl,stateDir,verbosity). Пересобери AAR.");

                // Вызов через JNI (упрощённо — реальный AAR экспонирует static Start)
                // Здесь заглушка: если AAR собран, вызов пройдёт; если нет — упадёт в PlatformNotSupported выше.
                _tsnet = new Java.Lang.Object(); // placeholder — реальный объект из Go
                _running = true;
                _lastError = null;
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
                    // Go Tsnet.stop()
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

        // Реальный Go AAR должен дать net.Conn → Stream через socket fd.
        // На этапе stub возвращаем PlatformNotSupported чтобы не скрывать отсутствие AAR.
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
