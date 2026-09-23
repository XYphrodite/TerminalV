using System.IO;
using System.Net.Sockets;
using System.Text;

namespace TerminalV.Ssh;

/// <summary>
/// SSH через вшитый tsnet (userspace WireGuard). Не требует системного Tailscale VPN,
/// работает параллельно с Happ. На платформе без Go AAR — бросает PlatformNotSupportedException с хинтом.
/// Использует ITailscaleConnector.DialAsync для транспорта, далее обычный SSH.NET ShellStream.
/// </summary>
public sealed class TsnetSshSession : SshSessionBase
{
    private readonly ITailscaleConnector _tailscale;
    private readonly bool _ownsConnector;
    private object? _client;
    private object? _shellStream;
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private Stream? _tsnetStream;
    private System.Net.Sockets.TcpListener? _forwardListener;
    private Task? _forwardTask;
    private CancellationTokenSource? _forwardCts;

    public TsnetSshSession(string id, SshConnectionOptions options, ITailscaleConnector? connector = null)
        : base(id, options)
    {
        if (!options.UseTailscale) throw new ArgumentException("Tailscale must be enabled for TsnetSshSession.", nameof(options));
        _tailscale = connector ?? CreatePlatformConnector();
        _ownsConnector = connector is null;
    }

    private static ITailscaleConnector CreatePlatformConnector()
    {
#if ANDROID
        // На Android попытаемся загрузить Go AAR через JNI. Если AAR не собран — fallback к stub с понятной ошибкой.
        try
        {
            var t = Type.GetType("TerminalV.Mobile.Tailscale.AndroidTsnetConnector, TerminalV.Mobile");
            if (t is not null && Activator.CreateInstance(t) is ITailscaleConnector c) return c;
        }
        catch { }
#endif
        return new TailscaleStubConnector();
    }

    public override async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(TsnetSshSession));
        if (State == SshSessionState.Connected) return;
        State = SshSessionState.Connecting;
        _options.Validate();
        _options.Tailscale.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);

            // 1. Start tsnet (idempotent)
            try
            {
                await _tailscale.StartAsync(_options.Tailscale, cancellationToken).ConfigureAwait(false);
            }
            catch (PlatformNotSupportedException)
            {
                throw;
            }
            catch (Exception ex)
            {
                State = SshSessionState.Faulted;
                RaiseError($"Tailscale tsnet: {ex.Message} — вшитый клиент не запустился. Проверь auth key / hostname, на телефоне не нужен отдельный Tailscale VPN.");
                throw;
            }

            // 2. Dial через tailnet userspace
            Stream tsStream;
            try
            {
                using var dialCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                dialCts.CancelAfter(_options.ConnectTimeout);
                tsStream = await _tailscale.DialAsync(_options.Host, _options.Port, dialCts.Token).ConfigureAwait(false);
                _tsnetStream = tsStream;
            }
            catch (PlatformNotSupportedException) { throw; }
            catch (Exception ex)
            {
                State = SshSessionState.Faulted;
                var msg = ex.Message;
                if (msg.Contains("not in tailnet", StringComparison.OrdinalIgnoreCase) || msg.Contains("no such host", StringComparison.OrdinalIgnoreCase))
                    msg += " — хост 100.x не найден в tailnet. Проверь tailscale status на xeon и auth key вшитого клиента.";
                RaiseError($"Tailscale dial { _options.Host}:{_options.Port}: {msg}");
                throw;
            }

            // 3. SSH.NET can only open its own sockets: forward the live tailnet pipe to
            // localhost and point the client there. Auth/terminal options are unchanged.
            try
            {
            var loopbackPort = StartLocalForward(tsStream);
            var client = CreateClientOverLoopback(loopbackPort);
                _client = client;
                await Task.Run(() => DynamicConnect(client), cancellationToken).ConfigureAwait(false);
                TrySetKeepAlive(client, _options.KeepAliveInterval);
                var stream = CreateShellStream(client, TerminalType, (uint)Columns, (uint)Rows);
                _shellStream = stream;
                State = SshSessionState.Connected;
                _readCts = new CancellationTokenSource();
                _readTask = Task.Factory.StartNew(() => ReadLoop(_readCts.Token), TaskCreationOptions.LongRunning).Unwrap();
            }
            catch (Exception ex)
            {
                State = SshSessionState.Faulted;
                var actual = ex is System.Reflection.TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                RaiseError(actual.Message);
                await DisconnectCoreAsync().ConfigureAwait(false);
                if (actual != ex) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(actual).Throw();
                throw;
            }
        }
        finally { _gate.Release(); }
    }

    public override async Task DisconnectAsync()
    {
        await _gate.WaitAsync().ConfigureAwait(false);
        try { await DisconnectCoreAsync().ConfigureAwait(false); State = SshSessionState.Disconnected; }
        finally { _gate.Release(); }
    }

    public override async Task WriteAsync(string data, CancellationToken cancellationToken = default)
    {
        if (IsDisposed) return;
        if (State != SshSessionState.Connected || _shellStream is null) throw new InvalidOperationException("SSH session not connected (tsnet).");
        if (string.IsNullOrEmpty(data)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var bytes = Encoding.UTF8.GetBytes(data);
            await Task.Run(() => DynamicWrite(_shellStream, bytes, 0, bytes.Length), cancellationToken).ConfigureAwait(false);
        }
        finally { _gate.Release(); }
    }

    protected override async Task OnResizeAsync(int cols, int rows, CancellationToken cancellationToken)
    {
        if (State != SshSessionState.Connected || _shellStream is null) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!TrySendWindowChange(_shellStream, (uint)cols, (uint)rows))
                RaiseError($"Resize to {cols}x{rows} requested but window-change not supported; reconnect to apply.");
        }
        finally { _gate.Release(); }
    }

    protected override void DisposeCore()
    {
        try { _readCts?.Cancel(); } catch { }
        try { _readTask?.Wait(500); } catch { }
        try { DisconnectCoreAsync().GetAwaiter().GetResult(); } catch { }
        if (_ownsConnector) try { _tailscale.Dispose(); } catch { }
        _gate.Dispose();
        _readCts?.Dispose();
        base.DisposeCore();
    }

    private async Task DisconnectCoreAsync()
    {
        await StopLocalForwardAsync().ConfigureAwait(false);
        try { _readCts?.Cancel(); } catch { }
        var stream = _shellStream; _shellStream = null;
        var client = _client; _client = null;
        var ts = _tsnetStream; _tsnetStream = null;
        try { if (stream is IDisposable d) d.Dispose(); } catch { }
        try { DynamicDisconnect(client); } catch { }
        try { if (client is IDisposable dc) dc.Dispose(); } catch { }
        try { if (ts is not null) await ts.DisposeAsync().ConfigureAwait(false); } catch { }
        // tsnet оставляем запущенным — переиспользуется между сессиями. Stop только при Dispose connector.
    }

    // Local forward: SSH.NET connects here, bytes are pumped to/from the tailnet pipe.
    private int StartLocalForward(Stream remote)
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        _forwardListener = listener;
        _forwardCts = new CancellationTokenSource();
        var token = _forwardCts.Token;
        _forwardTask = Task.Run(() => PumpForwardAsync(listener, remote, token));
        return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
    }

    private async Task StopLocalForwardAsync()
    {
        try { _forwardCts?.Cancel(); } catch { }
        try { _forwardListener?.Stop(); } catch { }
        var task = _forwardTask; _forwardTask = null;
        _forwardListener = null;
        try { _forwardCts?.Dispose(); } catch { }
        _forwardCts = null;
        if (task is not null)
        {
            try { await Task.WhenAny(task, Task.Delay(1000)).ConfigureAwait(false); } catch { }
        }
    }

    private static async Task PumpForwardAsync(System.Net.Sockets.TcpListener listener, Stream remote, CancellationToken ct)
    {
        System.Net.Sockets.TcpClient? inbound = null;
        try
        {
            inbound = await listener.AcceptTcpClientAsync(ct).ConfigureAwait(false);
            var local = inbound.GetStream();
            var up = CopyForwardAsync(local, remote, ct);
            var down = CopyForwardAsync(remote, local, ct);
            await Task.WhenAny(up, down).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
        finally { try { inbound?.Close(); } catch { } }
    }

    private static async Task CopyForwardAsync(Stream from, Stream to, CancellationToken ct)
    {
        var buf = new byte[8192];
        try
        {
            while (!ct.IsCancellationRequested)
            {
                var n = await from.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                if (n <= 0) return;
                await to.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                await to.FlushAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
        catch (Exception) { }
    }

    private async Task ReadLoop(CancellationToken token)
    {
        var buf = new byte[4096];
        while (!token.IsCancellationRequested && State == SshSessionState.Connected)
        {
            try
            {
                int read = 0;
                var stream = _shellStream;
                if (stream is null) break;
                read = await Task.Run(() => DynamicRead(stream, buf, 0, buf.Length), token).ConfigureAwait(false);
                if (read <= 0) { await Task.Delay(50, token).ConfigureAwait(false); continue; }
                var text = Encoding.UTF8.GetString(buf, 0, read);
                if (!string.IsNullOrEmpty(text)) RaiseData(text);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex)
            {
                if (!token.IsCancellationRequested)
                {
                    RaiseError(ex.Message);
                    State = SshSessionState.Faulted;
                    RaiseClosed(null);
                    if (_options.AutoReconnect)
                        _ = Task.Run(async () => { try { await ReconnectAsync().ConfigureAwait(false); } catch { } });
                }
                break;
            }
        }
        if (State == SshSessionState.Connected) { State = SshSessionState.Disconnected; RaiseClosed(null); }
    }

    // ---- SSH.NET via reflection (копия логики из SshNetSession) ----
    // SSH.NET speaks to the localhost forward; the bytes travel through the tailnet pipe.
    private object CreateClientOverLoopback(int loopbackPort)
    {
        var asm = TryLoadSshNet();
        if (asm is null) throw new PlatformNotSupportedException("SSH.NET (Renci.SshNet) is required but not referenced.");
        return CreateClientInternal(asm, loopbackPort);
    }

    private object CreateClientInternal(System.Reflection.Assembly asm, int loopbackPort)
    {
        var password = _options.Password ?? "";
        var hasKeyContent = !string.IsNullOrWhiteSpace(_options.PrivateKeyContent);
        var hasKeyFile = !string.IsNullOrWhiteSpace(_options.PrivateKeyPath);
        var keyPass = _options.PrivateKeyPassphrase;
        var authMethods = new List<object>();
        var authType = asm.GetType("Renci.SshNet.PasswordAuthenticationMethod")!;
        var privateKeyType = asm.GetType("Renci.SshNet.PrivateKeyFile");
        var privateKeyAuthType = asm.GetType("Renci.SshNet.PrivateKeyAuthenticationMethod");
        if (!string.IsNullOrEmpty(password))
            authMethods.Add(Activator.CreateInstance(authType, _options.Username, password)!);
        if (hasKeyContent)
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(_options.PrivateKeyContent!));
            var pk = string.IsNullOrEmpty(keyPass) ? Activator.CreateInstance(privateKeyType!, ms)! : Activator.CreateInstance(privateKeyType!, ms, keyPass)!;
            authMethods.Add(Activator.CreateInstance(privateKeyAuthType!, _options.Username, new[] { pk })!);
        }
        else if (hasKeyFile)
        {
            var path = _options.PrivateKeyPath!;
            if (!File.Exists(path)) throw new FileNotFoundException("Private key file not found.", path);
            var pk = string.IsNullOrEmpty(keyPass) ? Activator.CreateInstance(privateKeyType!, path)! : Activator.CreateInstance(privateKeyType!, path, keyPass)!;
            authMethods.Add(Activator.CreateInstance(privateKeyAuthType!, _options.Username, new[] { pk })!);
        }
        if (authMethods.Count == 0)
            authMethods.Add(Activator.CreateInstance(authType, _options.Username, password)!);
        var connInfoType = asm.GetType("Renci.SshNet.ConnectionInfo")!;
        var authBaseType = asm.GetType("Renci.SshNet.AuthenticationMethod")!;
        var typedArray = Array.CreateInstance(authBaseType, authMethods.Count);
        for (int i = 0; i < authMethods.Count; i++) typedArray.SetValue(authMethods[i], i);
        var connInfo = Activator.CreateInstance(connInfoType, "127.0.0.1", loopbackPort, _options.Username, typedArray)!;
        // Try to set socket factory to use tsnet stream if SSH.NET supports it (SocketFactory property)
        var sshClientType = asm.GetType("Renci.SshNet.SshClient")!;
        return Activator.CreateInstance(sshClientType, connInfo)!;
    }


    // Copy of reflection helpers from SshNetSession (duplicated to keep Tsnet self-contained)
    private static System.Reflection.Assembly? TryLoadSshNet()
    {
        try { return System.Reflection.Assembly.Load("Renci.SshNet"); } catch { return null; }
    }
    private static void TrySetKeepAlive(object client, TimeSpan interval)
    {
        try
        {
            var prop = client?.GetType().GetProperty("KeepAliveInterval");
            if (prop is not null && prop.CanWrite) prop.SetValue(client, interval);
        }
        catch { }
    }
    private static void DynamicConnect(object client)
    {
        try { client.GetType().GetMethod("Connect")!.Invoke(client, null); }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }
    }
    private static void DynamicDisconnect(object? client)
    {
        try { client?.GetType().GetMethod("Disconnect")?.Invoke(client, null); } catch { }
    }
    private static object CreateShellStream(object client, string term, uint cols, uint rows)
    {
        // Real SSH.NET signature: (string term, uint cols, uint rows, uint width, uint height,
        // IDictionary modes, int bufferSize). Look up flexibly like SshNetSession does.
        var mi = client.GetType().GetMethods().FirstOrDefault(m => m.Name == "CreateShellStream" && m.GetParameters().Length >= 5);
        if (mi is null) throw new MissingMethodException("SshClient.CreateShellStream not found.");
        var pars = mi.GetParameters();
        var args = new object?[pars.Length];
        for (int k = 0; k < pars.Length; k++)
        {
            var p = pars[k];
            if (p.ParameterType == typeof(string)) args[k] = term;
            else if (p.ParameterType == typeof(uint) && p.Name!.Contains("col", StringComparison.OrdinalIgnoreCase)) args[k] = cols;
            else if (p.ParameterType == typeof(uint) && p.Name!.Contains("row", StringComparison.OrdinalIgnoreCase)) args[k] = rows;
            else if (p.ParameterType == typeof(uint)) args[k] = (uint)0;
            else if (p.Name == "terminalModes" || p.ParameterType.Name.Contains("TerminalModes")) args[k] = null;
            else if (p.ParameterType == typeof(int)) args[k] = 1024;
            else args[k] = p.HasDefaultValue ? p.DefaultValue : null;
        }
        try { return mi.Invoke(client, args)!; }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }
    }
    private static void DynamicWrite(object stream, byte[] buf, int off, int len)
    {
        try { stream.GetType().GetMethod("Write", new[] { typeof(byte[]), typeof(int), typeof(int) })!.Invoke(stream, new object[] { buf, off, len }); }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }
    }
    private static int DynamicRead(object stream, byte[] buf, int off, int len)
    {
        try { return (int)stream.GetType().GetMethod("Read", new[] { typeof(byte[]), typeof(int), typeof(int) })!.Invoke(stream, new object[] { buf, off, len })!; }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }
    }
    private static bool TrySendWindowChange(object stream, uint cols, uint rows)
    {
        try
        {
            var m = stream.GetType().GetMethod("SendWindowChangeRequest");
            if (m is null) return false;
            m.Invoke(stream, new object[] { cols, rows, (uint)1024, (uint)1024 });
            return true;
        }
        catch { return false; }
    }
}
