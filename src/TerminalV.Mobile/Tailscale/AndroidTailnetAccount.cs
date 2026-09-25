#if ANDROID
using Android.Runtime;
using System.Text.Json;

namespace TerminalV.Mobile.Tailscale;

// One native account per application. Legacy SSH auth-key state is kept separate.
internal sealed class AndroidTailnetAccount : IDisposable
{
    private IntPtr _class, _instance;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private void EnsureInstance()
    {
        if (_instance != IntPtr.Zero) return;
        using var type = Java.Lang.Class.ForName("tsnet.Tsnet_", true, global::Android.App.Application.Context.ClassLoader!);
        _class = JNIEnv.NewGlobalRef(type.Handle);
        var local = JNIEnv.NewObject(_class, JNIEnv.GetMethodID(_class, "<init>", "()V"));
        _instance = JNIEnv.NewGlobalRef(local);
        JNIEnv.DeleteLocalRef(local);
    }
    public async Task<string> AccountAsync(string command, CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        try
        {
            return await Task.Run(() =>
            {
                EnsureInstance();
                var stateRoot = global::Android.App.Application.Context.NoBackupFilesDir?.AbsolutePath
                    ?? throw new IOException("Недоступно защищённое хранилище устройства.");
                var payload = JsonSerializer.Serialize(new { hostname = "terminalv-mobile", stateDir = Path.Combine(stateRoot, "tsnet-account") });
                var a = JNIEnv.NewString(command); var b = JNIEnv.NewString(payload);
                try
                {
                    var method = JNIEnv.GetMethodID(_class, "account", "(Ljava/lang/String;Ljava/lang/String;)Ljava/lang/String;");
                    var result = JNIEnv.CallObjectMethod(_instance, method, new JValue(a), new JValue(b));
                    return JNIEnv.GetString(result, JniHandleOwnership.TransferLocalRef) ?? "{}";
                }
                finally { JNIEnv.DeleteLocalRef(a); JNIEnv.DeleteLocalRef(b); }
            }, ct);
        }
        finally { _gate.Release(); }
    }
    public async Task<Stream> DialAsync(string host, int port, CancellationToken ct)
    {
        // Account lifetime is app-wide. Native Dial has its own bounded timeout.
        var endpoint = await Task.Run(() =>
        {
            if (_instance == IntPtr.Zero) throw new InvalidOperationException("Войдите через Tailscale.");
            var h = JNIEnv.NewString(host);
            try
            {
                var method = JNIEnv.GetMethodID(_class, "dialLoopback", "(Ljava/lang/String;J)Ljava/lang/String;");
                var result = JNIEnv.CallObjectMethod(_instance, method, new JValue(h), new JValue((long)port));
                return JNIEnv.GetString(result, JniHandleOwnership.TransferLocalRef) ?? throw new IOException("Нет адреса подключения.");
            }
            finally { JNIEnv.DeleteLocalRef(h); }
        }, ct).WaitAsync(ct);
        var tcp = new System.Net.Sockets.TcpClient();
        try
        {
            await tcp.ConnectAsync("127.0.0.1", int.Parse(endpoint[(endpoint.LastIndexOf(':') + 1)..]), ct);
            return new System.Net.Sockets.NetworkStream(tcp.Client, ownsSocket: true);
        }
        catch { tcp.Dispose(); throw; }
    }
    public void Dispose()
    {
        if (_instance == IntPtr.Zero) return;
        try { JNIEnv.CallVoidMethod(_instance, JNIEnv.GetMethodID(_class, "stop", "()V")); } catch { }
        JNIEnv.DeleteGlobalRef(_instance); JNIEnv.DeleteGlobalRef(_class);
        _instance = _class = IntPtr.Zero;
    }
}
#endif
