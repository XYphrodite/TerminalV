using System.IO;
using System.Text;

namespace TerminalV.Ssh;

/// <summary>
/// Direct OpenSSH session via SSH.NET SshClient + ShellStream.
/// Handles host/port/user/auth, shell type xterm-256color, cols/rows, reconnect.
/// Requires NuGet package SSH.NET (Renci.SshNet).
/// </summary>
public sealed class SshNetSession : SshSessionBase
{
    private object? _client; // Renci.SshNet.SshClient when package present
    private object? _shellStream; // Renci.SshNet.ShellStream
    private CancellationTokenSource? _readCts;
    private Task? _readTask;
    private readonly SemaphoreSlim _gate = new(1, 1);

    public SshNetSession(string id, SshConnectionOptions options) : base(id, options) { }

    public override async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsDisposed) throw new ObjectDisposedException(nameof(SshNetSession));
        if (State == SshSessionState.Connected) return;
        State = SshSessionState.Connecting;
        _options.Validate();
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await DisconnectCoreAsync().ConfigureAwait(false);
            var client = CreateClient();
            _client = client;
            // Connect with timeout
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(_options.ConnectTimeout);
            await Task.Run(() => DynamicConnect(client), timeoutCts.Token).ConfigureAwait(false);
            // Keep-alive
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
            // Improve gateway/port confusion: SSH identification string missing means client connected to non-SSH port (e.g., gateway 5454 with direct mode)
            var msg = actual.Message;
            if (msg.Contains("SSH identification string", StringComparison.OrdinalIgnoreCase) || msg.Contains("Protocol Version Exchange", StringComparison.OrdinalIgnoreCase))
            {
                msg = $"{msg} — проверь порт: 22 для прямого SSH (выкл. 'Через шлюз'), 5454 — только с включённым 'Через шлюз' и запущенным TerminalV на ПК (сейчас 5454 не слушает).";
            }
            else if (msg.Contains("Connection timed out", StringComparison.OrdinalIgnoreCase) && _options.Port == 5454 && !_options.UseGateway)
            {
                msg = $"{msg} — порт 5454 — это шлюз TerminalV, включи 'Через шлюз' или смени порт на 22 для прямого SSH.";
            }
            RaiseError(msg);
            await DisconnectCoreAsync().ConfigureAwait(false);
            if (actual != ex) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(actual).Throw();
            throw;
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
        if (State != SshSessionState.Connected || _shellStream is null) throw new InvalidOperationException("SSH session not connected.");
        if (string.IsNullOrEmpty(data)) return;
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // SSH.NET ShellStream.Write is synchronous; offload
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
            SshShell.Resize(_shellStream, (uint)cols, (uint)rows);
        }
        finally { _gate.Release(); }
    }

    protected override void DisposeCore()
    {
        try { _readCts?.Cancel(); } catch { }
        try { _readTask?.Wait(500); } catch { }
        try { DisconnectCoreAsync().GetAwaiter().GetResult(); } catch { }
        _gate.Dispose();
        _readCts?.Dispose();
        base.DisposeCore();
    }

    private Task DisconnectCoreAsync()
    {
        try { _readCts?.Cancel(); } catch { }
        var stream = _shellStream; _shellStream = null;
        var client = _client; _client = null;
        try { if (stream is IDisposable d) d.Dispose(); } catch { }
        try { DynamicDisconnect(client); } catch { }
        try { if (client is IDisposable dc) dc.Dispose(); } catch { }
        return Task.CompletedTask;
    }

    private async Task ReadLoop(CancellationToken token)
    {
        var buf = new byte[4096];
        var decoder = Encoding.UTF8.GetDecoder();
        var chars = new char[Encoding.UTF8.GetMaxCharCount(buf.Length)];
        while (!token.IsCancellationRequested && State == SshSessionState.Connected)
        {
            try
            {
                int read = 0;
                var stream = _shellStream;
                if (stream is null) break;
                read = await Task.Run(() => DynamicRead(stream, buf, 0, buf.Length), token).ConfigureAwait(false);
                if (read <= 0) { await Task.Delay(50, token).ConfigureAwait(false); continue; }
                // SSH reads may end in the middle of a Unicode character.
                var count = decoder.GetChars(buf, 0, read, chars, 0, flush: false);
                if (count > 0) RaiseData(new string(chars, 0, count));
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

    // ---- SSH.NET via reflection so project builds even when package absent ----
    private object CreateClient()
    {
        var asm = TryLoadSshNet();
        if (asm is null) throw new PlatformNotSupportedException("SSH.NET (Renci.SshNet) not referenced. Add PackageReference Include=\"SSH.NET\".");
        var password = _options.Password ?? "";
        var hasKeyContent = !string.IsNullOrWhiteSpace(_options.PrivateKeyContent);
        var hasKeyFile = !string.IsNullOrWhiteSpace(_options.PrivateKeyPath);
        var keyPass = _options.PrivateKeyPassphrase;

        var authMethods = new List<object>();
        var authType = asm.GetType("Renci.SshNet.PasswordAuthenticationMethod")!;
        var privateKeyType = asm.GetType("Renci.SshNet.PrivateKeyFile");
        var privateKeyAuthType = asm.GetType("Renci.SshNet.PrivateKeyAuthenticationMethod");

        if (!string.IsNullOrEmpty(password))
        {
            authMethods.Add(Activator.CreateInstance(authType, _options.Username, password)!);
            authMethods.Add(SshKeyboardAuthentication.Create(_options.Username, password));
        }

        if (hasKeyContent)
        {
            using var ms = new MemoryStream(Encoding.UTF8.GetBytes(_options.PrivateKeyContent!));
            var pk = string.IsNullOrEmpty(keyPass)
                ? Activator.CreateInstance(privateKeyType, ms)!
                : Activator.CreateInstance(privateKeyType, ms, keyPass)!;
            authMethods.Add(Activator.CreateInstance(privateKeyAuthType, _options.Username, new[] { pk })!);
        }
        else if (hasKeyFile)
        {
            var path = _options.PrivateKeyPath!;
            if (!File.Exists(path)) throw new FileNotFoundException("Private key file not found.", path);
            var pk = string.IsNullOrEmpty(keyPass)
                ? Activator.CreateInstance(privateKeyType, path)!
                : Activator.CreateInstance(privateKeyType, path, keyPass)!;
            authMethods.Add(Activator.CreateInstance(privateKeyAuthType, _options.Username, new[] { pk })!);
        }

        if (authMethods.Count == 0)
            authMethods.Add(Activator.CreateInstance(authType, _options.Username, password)!);

        var connInfoType = asm.GetType("Renci.SshNet.ConnectionInfo")!;
        // Build AuthenticationMethod[] of the correct runtime type for the ConnectionInfo ctor (params AuthenticationMethod[])
        var authBaseType = asm.GetType("Renci.SshNet.AuthenticationMethod")!;
        var typedArray = Array.CreateInstance(authBaseType, authMethods.Count);
        for (int i = 0; i < authMethods.Count; i++) typedArray.SetValue(authMethods[i], i);
        var connInfo = Activator.CreateInstance(connInfoType, _options.Host, _options.Port, _options.Username, typedArray)!;

        // ConnectionInfo Timeout
        try { connInfoType.GetProperty("Timeout")?.SetValue(connInfo, _options.ConnectTimeout); } catch { }

        var clientType = asm.GetType("Renci.SshNet.SshClient")!;
        var client = Activator.CreateInstance(clientType, connInfo)!;
        return client;
    }

    private static System.Reflection.Assembly? TryLoadSshNet()
    {
        try { return System.Reflection.Assembly.Load("Renci.SshNet") ?? System.Reflection.Assembly.Load("SSH.NET"); } catch { }
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
            if (a.GetName().Name is "Renci.SshNet" or "SSH.NET") return a;
        return null;
    }

    private static void DynamicConnect(object client)
    {
        try { client.GetType().GetMethod("Connect")!.Invoke(client, null); }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }
    }
    private static void DynamicDisconnect(object? client) { try { client?.GetType().GetMethod("Disconnect")?.Invoke(client, null); } catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null) { } catch { } }
    private static void TrySetKeepAlive(object client, TimeSpan interval)
    {
        if (interval <= TimeSpan.Zero) return;
        try { client.GetType().GetProperty("KeepAliveInterval")?.SetValue(client, interval); } catch { }
    }
    private static object CreateShellStream(object client, string term, uint cols, uint rows)
    {
        // CreateShellStream(string term, uint cols, uint rows, uint width, uint height, IDictionary<TerminalModes,uint> modes, int bufferSize)
        var mi = client.GetType().GetMethods().FirstOrDefault(m => m.Name == "CreateShellStream" && m.GetParameters().Length >= 5);
        if (mi is null) throw new MissingMethodException("SshClient.CreateShellStream not found.");
        var pars = mi.GetParameters();
        var args = new object?[pars.Length];
        for (int i = 0; i < pars.Length; i++)
        {
            var p = pars[i];
            if (p.ParameterType == typeof(string)) args[i] = term;
            else if (p.ParameterType == typeof(uint) && p.Name!.Contains("col", StringComparison.OrdinalIgnoreCase)) args[i] = cols;
            else if (p.ParameterType == typeof(uint) && p.Name!.Contains("row", StringComparison.OrdinalIgnoreCase)) args[i] = rows;
            else if (p.ParameterType == typeof(uint)) args[i] = (uint)0;
            else if (p.Name == "terminalModes" || p.ParameterType.Name.Contains("TerminalModes")) args[i] = null;
            else if (p.ParameterType == typeof(int)) args[i] = 1024;
            else args[i] = p.HasDefaultValue ? p.DefaultValue : null;
        }
        // Ensure correct positions for common overload: (term, cols, rows, width, height, modes, buffer)
        // Try explicit 7-arg overload
        try
        {
            var modesType = client.GetType().Assembly.GetType("Renci.SshNet.Common.TerminalModes") ?? typeof(object);
            // fallback: call with 7 args pattern
            var m7 = client.GetType().GetMethod("CreateShellStream", new[] { typeof(string), typeof(uint), typeof(uint), typeof(uint), typeof(uint), typeof(IDictionary<,>).MakeGenericType(modesType, typeof(uint)), typeof(int) });
        } catch { }
        try { return mi.Invoke(client, args)!; }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }
    }
    private static void DynamicWrite(object stream, byte[] buf, int off, int count)
    {
        var shell = (Stream)stream;
        shell.Write(buf, off, count);
        // ShellStream's byte overload buffers input; interactive keystrokes must
        // reach the server immediately, even when the buffer is not full.
        shell.Flush();
    }
    private static int DynamicRead(object stream, byte[] buf, int off, int count)
    {
        try { return (int)stream.GetType().GetMethod("Read", new[] { typeof(byte[]), typeof(int), typeof(int) })!.Invoke(stream, new object[] { buf, off, count })!; }
        catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException != null) { System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(tie.InnerException).Throw(); throw; }
    }
}
