#if ANDROID
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Android.Runtime;
using TerminalV.Ssh;

namespace TerminalV.Mobile.Tailscale;

/// <summary>
/// Android реализация через Go AAR (gomobile) tsnet.Tsnet_.
/// Работает в userspace, не требует BIND_VPN_SERVICE — только INTERNET.
/// </summary>
public sealed class AndroidTsnetConnector : ITailscaleConnector
{
    private IntPtr _classHandle;
    private IntPtr _instanceHandle;
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
                Java.Lang.Class? tsnetClass = null; // loaded below via app ClassLoader (pool threads cannot use bare ForName)
                // Pool-thread note: bare Class.ForName uses the bootstrap loader on .NET pool threads
                // and never sees app classes. Load the AAR class via the app ClassLoader instead.
                var appCtx = global::Android.App.Application.Context;
                var loader = appCtx.ClassLoader
                    ?? Java.Lang.Class.FromType(typeof(Java.Lang.Object)).ClassLoader;
                if (loader is null)
                    throw new PlatformNotSupportedException("App ClassLoader unavailable.");
                try { tsnetClass = Java.Lang.Class.ForName("tsnet.Tsnet_", true, loader); }
                catch (Java.Lang.ClassNotFoundException) { tsnetClass = null; }
                if (tsnetClass is null)
                    throw new PlatformNotSupportedException("Go AAR tsnet.Tsnet_ не найден. Проверь что tsnet.aar в libs и apk собран с AAR.");

                // tsnet.Tsnet_ has public Tsnet_() and methods: start(String,String,String,String,long), stop(), dialLoopback(String,long)
                var clazz = JNIEnv.NewGlobalRef(tsnetClass.Handle); // jclass of the loaded Class, no FindClass (blind on pool threads)
                if (clazz == IntPtr.Zero)
                    throw new PlatformNotSupportedException("NewGlobalRef tsnet/Tsnet_ failed. AAR не подключён.");

                var ctor = JNIEnv.GetMethodID(clazz, "<init>", "()V");
                if (ctor == IntPtr.Zero)
                    throw new MissingMethodException("tsnet.Tsnet_::<init> not found");

                var localInstance = JNIEnv.NewObject(clazz, ctor);
                if (localInstance == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to create tsnet.Tsnet_ instance");
                var instance = JNIEnv.NewGlobalRef(localInstance);
                JNIEnv.DeleteLocalRef(localInstance);
                if (instance == IntPtr.Zero)
                    throw new InvalidOperationException("Failed to create tsnet.Tsnet_ instance");

                _classHandle = clazz;
                _instanceHandle = instance;

                var ctx = global::Android.App.Application.Context;
                var filesDir = ctx.FilesDir?.AbsolutePath ?? "/data/data/com.companyname.terminalvmobile/files";
                var stateDir = string.IsNullOrWhiteSpace(options.StateDir) ? Path.Combine(filesDir, "tsnet-state") : options.StateDir!;
                Directory.CreateDirectory(stateDir);

                var mid = JNIEnv.GetMethodID(clazz, "start", "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;J)V");
                if (mid == IntPtr.Zero)
                    throw new MissingMethodException("tsnet.Tsnet_.start not found");

                var jAuthKey = JNIEnv.NewString(options.AuthKey ?? "");
                var jHostname = JNIEnv.NewString(options.Hostname ?? "terminalv-mobile");
                var jControlUrl = JNIEnv.NewString(options.ControlUrl ?? "");
                var jStateDir = JNIEnv.NewString(stateDir);
                try
                {
                    JValue[] args =
                    {
                        new JValue(jAuthKey),
                        new JValue(jHostname),
                        new JValue(jControlUrl),
                        new JValue(jStateDir),
                        new JValue((long)(options.LogVerbosity))
                    };
                    JNIEnv.CallVoidMethod(instance, mid, args);
                    if (JNIEnv.ExceptionOccurred() != IntPtr.Zero)
                    {
                        var ex = JNIEnv.ExceptionOccurred();
                        JNIEnv.ExceptionClear();
                        var msg = ex.ToString() ?? "tsnet start failed";
                        throw new InvalidOperationException(msg);
                    }
                }
                finally
                {
                    JNIEnv.DeleteLocalRef(jAuthKey);
                    JNIEnv.DeleteLocalRef(jHostname);
                    JNIEnv.DeleteLocalRef(jControlUrl);
                    JNIEnv.DeleteLocalRef(jStateDir);
                }

                _running = true;
                _lastError = null;
            }
            catch (Java.Lang.ClassNotFoundException)
            {
                throw new PlatformNotSupportedException("Вшитый Tailscale не собран: Go AAR tsnet.Tsnet_ отсутствует. Собери: tools/tsnet-bridge/build-aar.ps1 (требует Go + gomobile + Android SDK на xeon).");
            }
            catch (PlatformNotSupportedException) { throw; }
            catch (Exception ex)
            {
                _lastError = ex.Message;
                throw new InvalidOperationException($"Tailscale tsnet start failed: {ex.Message}", ex);
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
                if (_instanceHandle != IntPtr.Zero && _classHandle != IntPtr.Zero)
                {
                    var mid = JNIEnv.GetMethodID(_classHandle, "stop", "()V");
                    if (mid != IntPtr.Zero)
                    {
                        JNIEnv.CallVoidMethod(_instanceHandle, mid);
                        if (JNIEnv.ExceptionOccurred() != IntPtr.Zero) JNIEnv.ExceptionClear();
                    }
                }
            }
            catch { }
            finally
            {
                if (_instanceHandle != IntPtr.Zero) JNIEnv.DeleteGlobalRef(_instanceHandle);
                _instanceHandle = IntPtr.Zero;
                if (_classHandle != IntPtr.Zero) JNIEnv.DeleteGlobalRef(_classHandle);
                _classHandle = IntPtr.Zero;
                _running = false;
            }
        });
    }

    public Task<Stream> DialAsync(string host, int port, CancellationToken ct = default)
    {
        if (!_running || _instanceHandle == IntPtr.Zero || _classHandle == IntPtr.Zero)
            throw new InvalidOperationException("Tailscale не запущен. Сначала StartAsync с auth key.");

        return Task.Run<Stream>(async () =>
        {
            try
            {
                var mid = JNIEnv.GetMethodID(_classHandle, "dialLoopback", "(Ljava/lang/String;J)Ljava/lang/String;");
                if (mid == IntPtr.Zero)
                    throw new MissingMethodException("tsnet.Tsnet_.dialLoopback not found");

                // tsnet conns live in userspace: no OS fd exists, so Go bridges the tailnet
                // conn to a localhost listener and we connect with a plain socket.
                var jHost = JNIEnv.NewString(host);
                string endpoint;
                try
                {
                    var jAddr = JNIEnv.CallObjectMethod(_instanceHandle, mid, new JValue(jHost), new JValue((long)port));
                    if (JNIEnv.ExceptionOccurred() != IntPtr.Zero)
                    {
                        var ex = JNIEnv.ExceptionOccurred();
                        JNIEnv.ExceptionClear();
                        throw new InvalidOperationException("tsnet dial " + host + ":" + port + " failed: " + ex);
                    }
                    if (jAddr == IntPtr.Zero)
                        throw new IOException("tsnet dialLoopback returned null address");
                    try { endpoint = JNIEnv.GetString(jAddr, JniHandleOwnership.DoNotTransfer) ?? ""; }
                    finally { JNIEnv.DeleteLocalRef(jAddr); }
                    if (string.IsNullOrEmpty(endpoint))
                        throw new IOException("tsnet dialLoopback returned empty address");
                }
                finally
                {
                    JNIEnv.DeleteLocalRef(jHost);
                }
                var sep = endpoint.LastIndexOf(":");
                if (sep < 0 || !int.TryParse(endpoint.Substring(sep + 1), out var loopPort))
                    throw new IOException("tsnet dialLoopback returned bad address: " + endpoint);
                var tcp = new System.Net.Sockets.TcpClient();
                try
                {
                    await tcp.ConnectAsync("127.0.0.1", loopPort).WaitAsync(ct).ConfigureAwait(false);
                }
                catch
                {
                    tcp.Close();
                    throw;
                }
                return new System.Net.Sockets.NetworkStream(tcp.Client, ownsSocket: true);
            }
            catch (Exception ex) when (ex is not PlatformNotSupportedException)
            {
                throw new IOException($"Tailscale dial {host}:{port} failed: {ex.Message}", ex);
            }
        }, ct);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        if (_instanceHandle != IntPtr.Zero) JNIEnv.DeleteGlobalRef(_instanceHandle);
        _instanceHandle = IntPtr.Zero;
        if (_classHandle != IntPtr.Zero) JNIEnv.DeleteGlobalRef(_classHandle);
        _classHandle = IntPtr.Zero;
    }
}
#endif
