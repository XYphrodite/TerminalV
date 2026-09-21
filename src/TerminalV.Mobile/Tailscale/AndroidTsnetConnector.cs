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
    private nint _classHandle;
    private nint _instanceHandle;
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
                var tsnetClass = Java.Lang.Class.ForName("tsnet.Tsnet_");
                if (tsnetClass is null)
                    throw new PlatformNotSupportedException("Go AAR tsnet.Tsnet_ не найден. Проверь что tsnet.aar в libs и apk собран с AAR.");

                var clazz = JNIEnv.FindClass("tsnet/Tsnet_");
                if (clazz == 0)
                    throw new PlatformNotSupportedException("JNI FindClass tsnet/Tsnet_ failed. AAR не подключён.");

                var ctor = JNIEnv.GetMethodID(clazz, "<init>", "()V");
                if (ctor == 0)
                    throw new MissingMethodException("tsnet.Tsnet_::<init> not found");

                var instance = JNIEnv.NewObject(clazz, ctor);
                if (instance == 0)
                    throw new InvalidOperationException("Failed to create tsnet.Tsnet_ instance");

                _classHandle = clazz;
                _instanceHandle = instance;

                var ctx = global::Android.App.Application.Context;
                var filesDir = ctx.FilesDir?.AbsolutePath ?? "/data/data/com.companyname.terminalvmobile/files";
                var stateDir = string.IsNullOrWhiteSpace(options.StateDir) ? Path.Combine(filesDir, "tsnet-state") : options.StateDir!;
                Directory.CreateDirectory(stateDir);

                var mid = JNIEnv.GetMethodID(clazz, "start", "(Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;Ljava/lang/String;J)V");
                if (mid == 0)
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
                    if (JNIEnv.ExceptionOccurred() != 0)
                    {
                        var ex = JNIEnv.ExceptionOccurred();
                        JNIEnv.ExceptionClear();
                        var msg = ex.ToString() ?? "tsnet start failed";
                        JNIEnv.DeleteLocalRef(ex);
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
                if (_instanceHandle != 0 && _classHandle != 0)
                {
                    var mid = JNIEnv.GetMethodID(_classHandle, "stop", "()V");
                    if (mid != 0)
                    {
                        JNIEnv.CallVoidMethod(_instanceHandle, mid);
                        if (JNIEnv.ExceptionOccurred() != 0) JNIEnv.ExceptionClear();
                    }
                }
            }
            catch { }
            finally
            {
                if (_instanceHandle != 0) JNIEnv.DeleteGlobalRef(_instanceHandle);
                _instanceHandle = 0;
                _classHandle = 0;
                _running = false;
            }
        });
    }

    public Task<Stream> DialAsync(string host, int port, CancellationToken ct = default)
    {
        if (!_running || _instanceHandle == 0 || _classHandle == 0)
            throw new InvalidOperationException("Tailscale не запущен. Сначала StartAsync с auth key.");

        return Task.Run<Stream>(() =>
        {
            try
            {
                var mid = JNIEnv.GetMethodID(_classHandle, "dialFD", "(Ljava/lang/String;J)I");
                if (mid == 0)
                    throw new MissingMethodException("tsnet.Tsnet_.dialFD not found");

                var jHost = JNIEnv.NewString(host);
                try
                {
                    var fd = JNIEnv.CallIntMethod(_instanceHandle, mid, new JValue(jHost), new JValue((long)port));
                    if (JNIEnv.ExceptionOccurred() != 0)
                    {
                        var ex = JNIEnv.ExceptionOccurred();
                        JNIEnv.ExceptionClear();
                        throw new InvalidOperationException($"tsnet dial {host}:{port} failed: {ex}");
                    }
                    if (fd < 0)
                        throw new IOException($"tsnet dial {host}:{port} returned fd {fd}");

                    var pfdClass = JNIEnv.FindClass("android/os/ParcelFileDescriptor");
                    var adoptFd = JNIEnv.GetStaticMethodID(pfdClass, "adoptFd", "(I)Landroid/os/ParcelFileDescriptor;");
                    var pfd = JNIEnv.CallStaticObjectMethod(pfdClass, adoptFd, new JValue(fd));
                    if (pfd == 0)
                        throw new IOException("ParcelFileDescriptor.adoptFd returned null");

                    var pfdObj = new Android.OS.ParcelFileDescriptor(pfd, JniHandleOwnership.TransferLocalRef);
                    var input = new Android.OS.ParcelFileDescriptor.AutoCloseInputStream(pfdObj);
                    var output = new Android.OS.ParcelFileDescriptor.AutoCloseOutputStream(pfdObj);
                    // Combine into a single Stream (read from input, write to output) — use a simple duplex wrapper
                    return new ParcelFdStream(input, output);
                }
                finally
                {
                    JNIEnv.DeleteLocalRef(jHost);
                }
            }
            catch (Exception ex) when (ex is not PlatformNotSupportedException)
            {
                throw new IOException($"Tailscale dial {host}:{port} failed: {ex.Message}", ex);
            }
        }, ct);
    }

    private sealed class ParcelFdStream : Stream
    {
        private readonly Stream _input;
        private readonly Stream _output;
        public ParcelFdStream(Stream input, Stream output) { _input = input; _output = output; }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override void Flush() => _output.Flush();
        public override int Read(byte[] buffer, int offset, int count) => _input.Read(buffer, offset, count);
        public override void Write(byte[] buffer, int offset, int count) => _output.Write(buffer, offset, count);
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        protected override void Dispose(bool disposing)
        {
            if (disposing) { try { _input.Dispose(); } catch { } try { _output.Dispose(); } catch { } }
            base.Dispose(disposing);
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { StopAsync().GetAwaiter().GetResult(); } catch { }
        if (_instanceHandle != 0) JNIEnv.DeleteGlobalRef(_instanceHandle);
        _instanceHandle = 0;
    }
}
#endif
