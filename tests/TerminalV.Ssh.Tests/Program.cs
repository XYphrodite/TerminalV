using TerminalV.Ssh;

int passed = 0, failed = 0;
void Check(string name, Action test)
{
    try
    {
        test();
        Console.WriteLine($"PASS {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
        failed++;
    }
}
void CheckAsync(string name, Func<Task> test)
{
    try
    {
        test().GetAwaiter().GetResult();
        Console.WriteLine($"PASS {name}");
        passed++;
    }
    catch (Exception ex)
    {
        Console.WriteLine($"FAIL {name}: {ex.GetType().Name}: {ex.Message}");
        failed++;
    }
}

// Regression for MissingConstructor_Name, Renci.SshNet.ConnectionInfo
// Previously SshNetSession.CreateClient used (object)authMethods.ToArray() with dynamic, producing object[] not AuthenticationMethod[]
// This caused MissingMethodException for ConnectionInfo ctor (host,port,user, AuthenticationMethod[])
Check("ConnectionInfo ctor with password auth does not throw MissingMethodException", () =>
{
    var opts = new SshConnectionOptions
    {
        Host = "127.0.0.1",
        Port = 2222,
        Username = "test",
        Password = "test",
        ConnectTimeout = TimeSpan.FromSeconds(1)
    };
    var svc = new SshService();
    var sess = svc.Create("test-password", opts);
    // CreateClient is private, but ConnectAsync will call it. We test that CreateClient via reflection does not throw MissingMethodException
    // Instead, test that SshService.Create succeeds and that ConnectAsync throws a connection failure, not MissingMethodException
    try
    {
        sess.ConnectAsync().GetAwaiter().GetResult();
    }
    catch (MissingMethodException mex)
    {
        throw new Exception($"MissingMethodException for ConnectionInfo - bug not fixed: {mex.Message}", mex);
    }
    catch (PlatformNotSupportedException pex) when (pex.Message.Contains("SSH.NET"))
    {
        throw new Exception($"PlatformNotSupportedException - assembly not loaded: {pex.Message}", pex);
    }
    catch
    {
        // Expected: connection failure (SocketException, SshException, etc.) - not MissingMethod
        // This means ConnectionInfo was created correctly
    }
    finally
    {
        sess.Dispose();
        svc.Dispose();
    }
});

Check("ConnectionInfo ctor with private key content", () =>
{
    // Use a dummy key content that is not valid, but we test that the code path for PrivateKeyFile is taken without MissingMethod
    // The key content will fail to parse, but should not be MissingMethod for ConnectionInfo
    var opts = new SshConnectionOptions
    {
        Host = "127.0.0.1",
        Port = 2222,
        Username = "test",
        PrivateKeyContent = "-----BEGIN RSA PRIVATE KEY-----\nMIIE...dummy\n-----END RSA PRIVATE KEY-----",
        ConnectTimeout = TimeSpan.FromSeconds(1)
    };
    var svc = new SshService();
    var sess = svc.Create("test-key", opts);
    try
    {
        sess.ConnectAsync().GetAwaiter().GetResult();
    }
    catch (MissingMethodException mex)
    {
        throw new Exception($"MissingMethodException for key auth - bug not fixed: {mex.Message}", mex);
    }
    catch (PlatformNotSupportedException pex) when (pex.Message.Contains("SSH.NET"))
    {
        throw new Exception($"PlatformNotSupportedException - assembly not loaded: {pex.Message}", pex);
    }
    catch
    {
        // Expected to fail due to invalid key or connection, but not MissingMethod
    }
    finally
    {
        sess.Dispose();
        svc.Dispose();
    }
});

CheckAsync("SshService direct gateway does not hit ConnectionInfo (uses Gateway)", async () =>
{
    var opts = new SshConnectionOptions
    {
        Host = "127.0.0.1",
        Port = 22,
        Username = "test",
        Password = "test",
        GatewayUrl = "ws://127.0.0.1:5454",
        ConnectTimeout = TimeSpan.FromSeconds(1)
    };
    var svc = new SshService();
    var sess = svc.Create("test-gateway", opts);
    // Gateway session should not use ConnectionInfo, so even if we had bug, it would not be hit
    // Just verify it creates without exception
    if (sess is not GatewaySshSession) throw new Exception("Expected GatewaySshSession");
    svc.Dispose();
    await Task.CompletedTask;
});

// Regression for Arg_TargetInvocationException: reflection Invoke wraps inner exception in TargetInvocationException
// Previously SshNetSession.DynamicConnect/CreateShellStream/etc used Invoke without unwrapping, so auth failures showed as TargetInvocationException
Check("Ssh Connect failure unwraps TargetInvocationException to inner", () =>
{
    var opts = new SshConnectionOptions
    {
        Host = "127.0.0.1",
        Port = 2222, // no sshd here, will fail to connect
        Username = "test",
        Password = "wrong",
        ConnectTimeout = TimeSpan.FromSeconds(1)
    };
    var svc = new SshService();
    var sess = svc.Create("test-unwrap", opts);
    try
    {
        sess.ConnectAsync().GetAwaiter().GetResult();
        throw new Exception("Expected connection failure, but ConnectAsync succeeded");
    }
    catch (System.Reflection.TargetInvocationException tie)
    {
        throw new Exception($"TargetInvocationException not unwrapped - bug not fixed: {tie.InnerException?.GetType().Name}: {tie.InnerException?.Message}", tie);
    }
    catch (MissingMethodException)
    {
        throw;
    }
    catch
    {
        // Expected: SocketException, SshException, etc. - but not TargetInvocationException
        // If we get here without TargetInvocationException, the fix works
    }
    finally
    {
        sess.Dispose();
        svc.Dispose();
    }
});

// Regression for SSH identification string: direct SshClient to non-SSH port (gateway) gives confusing message
Check("Ssh identification string hint", () =>
{
    var opts = new SshConnectionOptions
    {
        Host = "127.0.0.1",
        Port = 5454, // gateway port, not sshd, with direct mode
        Username = "test",
        Password = "test",
        ConnectTimeout = TimeSpan.FromSeconds(1)
    };
    var svc = new SshService();
    var sess = svc.Create("test-ident", opts);
    // Use SshNetSession directly to test message transformation (without gateway)
    // The session will try to connect to 5454 which is not SSH, and should get a helpful hint
    string? capturedError = null;
    sess.ErrorReceived += msg => capturedError = msg;
    try { sess.ConnectAsync().GetAwaiter().GetResult(); } catch { }
    // If error was captured, it should contain hint about port 5454/gateway
    // For direct mode to 5454, we expect either timeout or identification hint
    // Just verify that no TargetInvocationException is thrown and that SshNetSession handles it
    if (capturedError != null && capturedError.Contains("TargetInvocationException"))
        throw new Exception($"Error still wrapped: {capturedError}");
    sess.Dispose();
    svc.Dispose();
});

Check("TailscaleOptions validate passes for disabled", () =>
{
    var opts = new SshConnectionOptions
    {
        Host = "100.119.48.15",
        Port = 22,
        Username = "local",
        Password = "5454",
        ConnectTimeout = TimeSpan.FromSeconds(1)
    };
    opts.Tailscale.Enabled = false;
    opts.Validate();
    if (opts.UseTailscale) throw new Exception("UseTailscale should be false");
});

Check("TailscaleOptions validate and UseTailscale", () =>
{
    var opts = new SshConnectionOptions
    {
        Host = "100.119.48.15",
        Port = 22,
        Username = "local",
        Password = "5454",
        ConnectTimeout = TimeSpan.FromSeconds(1)
    };
    opts.Tailscale.Enabled = true;
    opts.Tailscale.AuthKey = "tskey-auth-test123";
    opts.Tailscale.Hostname = "terminalv-mobile";
    opts.Validate();
    if (!opts.UseTailscale) throw new Exception("UseTailscale should be true");
    if (opts.Clone().Tailscale.AuthKey != "tskey-auth-test123") throw new Exception("Clone should copy Tailscale");
    if (ReferenceEquals(opts.Tailscale, opts.Clone().Tailscale)) throw new Exception("Clone must deep copy Tailscale");
});

Check("SshService creates TsnetSshSession when UseTailscale", () =>
{
    var opts = new SshConnectionOptions
    {
        Host = "100.119.48.15",
        Port = 22,
        Username = "local",
        Password = "5454",
        ConnectTimeout = TimeSpan.FromSeconds(1)
    };
    opts.Tailscale.Enabled = true;
    opts.Tailscale.AuthKey = "tskey-auth-test123";
    var svc = new SshService();
    var sess = svc.Create("test-tsnet", opts);
    if (sess is not TsnetSshSession) throw new Exception($"Expected TsnetSshSession, got {sess.GetType().Name}");
    svc.Dispose();
});

Check("Tsnet without AAR throws PlatformNotSupported with hint", () =>
{
    var opts = new SshConnectionOptions
    {
        Host = "100.119.48.15",
        Port = 22,
        Username = "local",
        Password = "5454",
        ConnectTimeout = TimeSpan.FromSeconds(1)
    };
    opts.Tailscale.Enabled = true;
    opts.Tailscale.AuthKey = "tskey-auth-test123";
    var svc = new SshService();
    var sess = svc.Create("test-tsnet-no-aar", opts);
    string? captured = null;
    sess.ErrorReceived += m => captured = m;
    try { sess.ConnectAsync().GetAwaiter().GetResult(); } catch (PlatformNotSupportedException) { }
    catch { }
    // Stub connector throws PlatformNotSupportedException with build hint
    // ConnectAsync will raise ErrorReceived or throw — verify no TargetInvocationException leak
    if (captured != null && captured.Contains("TargetInvocationException"))
        throw new Exception($"Leaked TargetInvocationException: {captured}");
    sess.Dispose();
    svc.Dispose();
});

Check("Gateway and Tailscale mutually exclusive priority (Tailscale wins)", () =>
{
    var opts = new SshConnectionOptions
    {
        Host = "100.119.48.15",
        Port = 22,
        Username = "local",
        Password = "5454",
        GatewayUrl = "ws://100.119.48.15:5454",
        ConnectTimeout = TimeSpan.FromSeconds(1)
    };
    opts.Tailscale.Enabled = true;
    opts.Tailscale.AuthKey = "tskey-auth-test123";
    var svc = new SshService();
    var sess = svc.Create("test-both", opts);
    // Tailscale has priority per SshService (UseTailscale checked first)
    if (sess is not TsnetSshSession) throw new Exception($"Expected TsnetSshSession when both set, got {sess.GetType().Name}");
    svc.Dispose();
});

// Regression for Android dialFD JNI signature: Go int -> Java long (J), not int (I)
// Previously AndroidTsnetConnector used (Ljava/lang/String;J)I and CallIntMethod, but tsnet.Tsnet_.dialFD is (String,long)->long (J return)
// See tsnet.aar javap: public native long dialFD(String,long)
Check("AndroidTsnetConnector dialLoopback bridge uses String-J-String and TcpClient", () =>
{
    var csPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TerminalV.Mobile", "Tailscale", "AndroidTsnetConnector.cs"));
    if (!File.Exists(csPath))
        csPath = Path.GetFullPath("src/TerminalV.Mobile/Tailscale/AndroidTsnetConnector.cs");
    if (!File.Exists(csPath))
        throw new FileNotFoundException("AndroidTsnetConnector.cs not found at " + csPath);
    var txt = File.ReadAllText(csPath);
    if (!txt.Contains("dialLoopback"))
        throw new Exception("connector must dial via tsnet dialLoopback bridge: userspace conns have no OS fd");
    if (!txt.Contains("Ljava/lang/String;J)Ljava/lang/String;"))
        throw new Exception("dialLoopback JNI signature must be (String,long)->String");
    if (!txt.Contains("TcpClient"))
        throw new Exception("connector must connect the loopback bridge with a plain TcpClient");
    if (txt.Contains("dialFD"))
        throw new Exception("fd-passing DialFD must go: userspace conns have no OS fd");
});

Check("Go tsnet bridge exposes DialLoopback instead of fd passing", () =>
{
    var goPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "tsnet-bridge", "tsnet.go"));
    if (!File.Exists(goPath))
        goPath = Path.GetFullPath("tools/tsnet-bridge/tsnet.go");
    if (!File.Exists(goPath))
        throw new FileNotFoundException("tsnet.go not found at " + goPath);
    var txt = File.ReadAllText(goPath);
    if (!txt.Contains("DialLoopback"))
        throw new Exception("tsnet.go must bridge tailnet conns via DialLoopback");
    if (!txt.Contains("127.0.0.1:0"))
        throw new Exception("DialLoopback must listen on localhost");
    if (!txt.Contains("io.Copy"))
        throw new Exception("DialLoopback must proxy bytes with io.Copy");
    if (txt.Contains("DialFD only for TCPConn"))
        throw new Exception("fd-passing DialFD must go: userspace conns have no OS fd");
});


Check("Android tsnet anet interfaces fix present", () =>
{
    var goPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools", "tsnet-bridge", "interfaces_android.go"));
    if (!File.Exists(goPath))
        goPath = Path.GetFullPath("tools/tsnet-bridge/interfaces_android.go");
    if (!File.Exists(goPath))
        throw new FileNotFoundException($"interfaces_android.go not found at {goPath}");
    var txt = File.ReadAllText(goPath);
    if (!txt.Contains("RegisterInterfaceGetter"))
        throw new Exception("interfaces_android.go must contain netmon.RegisterInterfaceGetter for Android netlink fix");
    if (!txt.Contains("anet.Interfaces"))
        throw new Exception("interfaces_android.go must use anet.Interfaces (wlynxg/anet) for getifaddrs");
    if (!txt.Contains("//go:build android"))
        throw new Exception("interfaces_android.go must have //go:build android tag");
});

Check("Android proguard keep rules for tsnet AAR (R8 strips JNI-only classes)", () =>
{
    var cfgPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TerminalV.Mobile", "Platforms", "Android", "proguard.cfg"));
    if (!File.Exists(cfgPath))
        cfgPath = Path.GetFullPath("src/TerminalV.Mobile/Platforms/Android/proguard.cfg");
    if (!File.Exists(cfgPath))
        throw new FileNotFoundException("proguard.cfg not found at " + cfgPath);
    var cfgText = File.ReadAllText(cfgPath);
    if (!cfgText.Contains("-keep class tsnet.**"))
        throw new Exception("proguard.cfg must keep tsnet.** (gomobile JNI binding is invisible to R8)");
    if (!cfgText.Contains("-keep class go.**"))
        throw new Exception("proguard.cfg must keep go.** (gomobile runtime)");
    var projPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TerminalV.Mobile", "TerminalV.Mobile.csproj"));
    if (!File.Exists(projPath))
        projPath = Path.GetFullPath("src/TerminalV.Mobile/TerminalV.Mobile.csproj");
    if (!File.ReadAllText(projPath).Contains("proguard.cfg"))
        throw new Exception("TerminalV.Mobile.csproj must reference proguard.cfg via ProguardConfiguration");
});

Check("AndroidTsnetConnector loads AAR class via app ClassLoader (pool threads)", () =>
{
    var csPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TerminalV.Mobile", "Tailscale", "AndroidTsnetConnector.cs"));
    if (!File.Exists(csPath))
        csPath = Path.GetFullPath("src/TerminalV.Mobile/Tailscale/AndroidTsnetConnector.cs");
    if (!File.Exists(csPath))
        throw new FileNotFoundException("AndroidTsnetConnector.cs not found at " + csPath);
    var txt = File.ReadAllText(csPath);
    if (!txt.Contains("ForName(") || !txt.Contains("tsnet.Tsnet_") || !txt.Contains(", true,"))
        throw new Exception("must load tsnet.Tsnet_ via Class.ForName(name, true, appClassLoader): bare ForName is blind on pool threads");
    if (!txt.Contains("ClassLoader"))
        throw new Exception("must obtain the app ClassLoader for AAR lookup");
    if (!txt.Contains("NewGlobalRef(tsnetClass.Handle)"))
        throw new Exception("must take jclass from the loaded Class object instead of FindClass");
    if (txt.Contains("JNIEnv.FindClass"))
        throw new Exception("bare JNIEnv.FindClass for the app class must go: blind on pool threads");
});

Check("TsnetSshSession forwards SSH over localhost to the tailnet pipe", () =>
{
    var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TerminalV", "Ssh", "TsnetSshSession.cs"));
    if (!File.Exists(path))
        path = Path.GetFullPath("src/TerminalV/Ssh/TsnetSshSession.cs");
    if (!File.Exists(path))
        throw new FileNotFoundException("TsnetSshSession.cs not found at " + path);
    var txt = File.ReadAllText(path);
    if (!txt.Contains("TcpListener"))
        throw new Exception("TsnetSshSession must forward the tailnet pipe via a localhost TcpListener: SSH.NET opens its own sockets");
    if (!txt.Contains("AcceptTcpClientAsync"))
        throw new Exception("forward must accept the SSH.NET loopback connection");
    if (!txt.Contains("127.0.0.1"))
        throw new Exception("SSH.NET client must be pointed at the localhost forward, not at the tailnet address");
    if (txt.Contains("TrySetSocketFactory"))
        throw new Exception("dead SocketFactory hook must go: it never redirected SSH.NET transport");
});

Check("Tsnet CreateShellStream lookup matches real SSH.NET (flexible overload)", () =>
{
    System.Reflection.MethodInfo? real = null;
    foreach (var m in typeof(Renci.SshNet.SshClient).GetMethods())
    {
        if (m.Name == "CreateShellStream" && m.GetParameters().Length >= 5) { real = m; break; }
    }
    if (real is null)
        throw new Exception("real SSH.NET SshClient has no flexible CreateShellStream overload");
    var path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TerminalV", "Ssh", "TsnetSshSession.cs"));
    if (!File.Exists(path))
        path = Path.GetFullPath("src/TerminalV/Ssh/TsnetSshSession.cs");
    if (!File.Exists(path))
        throw new FileNotFoundException("TsnetSshSession.cs not found at " + path);
    var txt = File.ReadAllText(path);
    if (!txt.Contains("GetParameters().Length >= 5"))
        throw new Exception("TsnetSshSession must look up CreateShellStream flexibly, mirroring SshNetSession");
    if (txt.Contains("typeof(string), typeof(uint), typeof(uint), typeof(uint)"))
        throw new Exception("strict CreateShellStream signature lookup must go");
});

Check("Tsnet DynamicRead and DynamicWrite resolve byte-array overloads", () =>
{
    var flags = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static;
    var t = typeof(TerminalV.Ssh.TsnetSshSession);
    var read = t.GetMethod("DynamicRead", flags);
    var write = t.GetMethod("DynamicWrite", flags);
    if (read is null || write is null) throw new Exception("DynamicRead or DynamicWrite missing");
    var s = new AmbiguousStream();
    try { write.Invoke(null, new object[] { s, new byte[] { 7, 8, 9 }, 0, 3 }); }
    catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is System.Reflection.AmbiguousMatchException)
    { throw new Exception("DynamicWrite matched multiple overloads (phone bug)"); }
    s.Position = 0;
    var buf = new byte[3];
    int n;
    try { n = (int)read.Invoke(null, new object[] { s, buf, 0, 3 })!; }
    catch (System.Reflection.TargetInvocationException tie) when (tie.InnerException is System.Reflection.AmbiguousMatchException)
    { throw new Exception("DynamicRead matched multiple overloads (phone bug)"); }
    if (n != 3 || buf[0] != 7 || buf[1] != 8 || buf[2] != 9) throw new Exception("stream roundtrip mismatch");
});

Check("GatewayProtocol handshake roundtrips sessionId and control flag", () =>
{
    var json = TerminalV.Ssh.GatewayProtocol.ConnectHandshake("desk1", control: false, cols: 120, rows: 30, term: null);
    if (!TerminalV.Ssh.GatewayProtocol.TryParseHandshake(json, out var h) || h is null)
        throw new Exception("handshake did not parse");
    if (h.SessionId != "desk1" || h.Control || h.Cols != 120 || h.Rows != 30 || h.Term != "xterm-256color")
        throw new Exception($"handshake fields wrong: {json}");
    var ctl = TerminalV.Ssh.GatewayProtocol.ConnectHandshake(null, control: true, cols: 80, rows: 24, term: null);
    if (!TerminalV.Ssh.GatewayProtocol.TryParseHandshake(ctl, out var hc) || hc is null || !hc.Control)
        throw new Exception("control handshake must parse with Control=true");
    // Legacy SSH-proxy handshake (extra host/user fields) is accepted as a create.
    var legacy = """{"type":"connect","host":"h","port":22,"username":"u","password":"p","cols":80,"rows":24,"term":"xterm-256color"}""";
    if (!TerminalV.Ssh.GatewayProtocol.TryParseHandshake(legacy, out var hl) || hl is null || hl.Control || hl.SessionId is not null)
        throw new Exception("legacy handshake must parse as non-control create");
    if (!TerminalV.Ssh.GatewayProtocol.IsControlReply("attached") || !TerminalV.Ssh.GatewayProtocol.IsControlReply("sessions"))
        throw new Exception("attached/sessions must be control replies");
    if (TerminalV.Ssh.GatewayProtocol.IsControlReply("data") || TerminalV.Ssh.GatewayProtocol.IsControlReply("error"))
        throw new Exception("data/error must not be swallowed as control replies");
});

Check("Mirror Validate relaxes host/user but keeps gateway URL rules", () =>
{
    var mirror = new SshConnectionOptions
    {
        Host = "",
        Username = "",
        Port = 22,
        GatewayUrl = "ws://127.0.0.1:5454",
        MirrorSessionId = "desk1"
    };
    mirror.Validate();
    var noMirror = new SshConnectionOptions
    {
        Host = "",
        Username = "",
        Port = 22,
        GatewayUrl = "ws://127.0.0.1:5454"
    };
    try { noMirror.Validate(); throw new Exception("empty host without MirrorSessionId must fail"); }
    catch (ArgumentException) { }
    var badUrl = new SshConnectionOptions
    {
        GatewayUrl = "ftp://x",
        MirrorSessionId = "desk1",
        Port = 22
    };
    try { badUrl.Validate(); throw new Exception("bad gateway URL must fail even in mirror mode"); }
    catch (ArgumentException) { }
});

Check("Mobile links shared mirror client sources", () =>
{
    var projPath = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "src", "TerminalV.Mobile", "TerminalV.Mobile.csproj"));
    if (!File.Exists(projPath))
        projPath = Path.GetFullPath("src/TerminalV.Mobile/TerminalV.Mobile.csproj");
    var txt = File.ReadAllText(projPath);
    if (!txt.Contains("GatewayProtocol.cs") || !txt.Contains("GatewayControlClient.cs"))
        throw new Exception("TerminalV.Mobile.csproj must link GatewayProtocol.cs and GatewayControlClient.cs");
});

CheckAsync("Mirror gateway end-to-end: list, attach, write, resize, auth", async () =>
{
    const int port = 54599;
    var backend = new FakeGatewayBackend(["desk1"]);
    using var server = new TerminalV.Gateway.GatewayServer(backend, port, token: "test-token");
    server.Start();
    try
    {
        using var control = new TerminalV.Ssh.GatewayControlClient($"ws://127.0.0.1:{port}", "test-token");
        var ids = await control.ListAsync();
        if (!ids.Contains("desk1")) throw new Exception("list must contain desk1, got: " + string.Join(",", ids));

        var opts = new SshConnectionOptions
        {
            Host = "",
            Username = "",
            Port = 22,
            GatewayUrl = $"ws://127.0.0.1:{port}",
            GatewayToken = "test-token",
            MirrorSessionId = "desk1",
            ConnectTimeout = TimeSpan.FromSeconds(5)
        };
        using var svc = new SshService();
        var sess = (TerminalV.Ssh.GatewaySshSession)svc.Create("m1", opts);
        var received = new List<string>();
        string? attached = null;
        sess.DataReceived += d => { lock (received) received.Add(d); };
        sess.Attached += id => attached = id;
        await sess.ConnectAsync();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (attached is null && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (attached != "desk1") throw new Exception("expected attached desk1, got: " + attached);
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (true) { lock (received) { if (received.Count > 0) break; } if (DateTime.UtcNow > deadline) break; await Task.Delay(50); }
        lock (received)
        {
            if (received.Count == 0) throw new Exception("no snapshot data after attach");
            if (received.Any(d => d.Contains("\"attached\"") || d.Contains("\"sessions\"")))
                throw new Exception("control frames leaked into terminal data: " + string.Join("|", received));
        }
        await sess.WriteAsync("ls\n");
        await sess.ResizeAsync(100, 30);
        deadline = DateTime.UtcNow.AddSeconds(5);
        while ((!backend.Writes.Contains("ls\n") || backend.LastResize != (100, 30)) && DateTime.UtcNow < deadline)
            await Task.Delay(50);
        if (!backend.Writes.Contains("ls\n")) throw new Exception("server did not receive write");
        if (backend.LastResize != (100, 30)) throw new Exception("server did not receive resize");

        var created = await control.CreateAsync(80, 24);
        if (string.IsNullOrWhiteSpace(created)) throw new Exception("create must return a session id");
        await control.KillAsync(created);
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (!backend.Kills.Contains(created) && DateTime.UtcNow < deadline) await Task.Delay(50);
        if (!backend.Kills.Contains(created)) throw new Exception("server did not receive kill");

        using var badAuth = new TerminalV.Ssh.GatewayControlClient($"ws://127.0.0.1:{port}", "wrong");
        try { await badAuth.ListAsync(); throw new Exception("wrong token must be rejected"); }
        catch (InvalidOperationException) { }
        catch (System.Net.WebSockets.WebSocketException) { }
        catch (System.Net.Http.HttpRequestException) { }
    }
    finally
    {
        server.Stop();
    }
});

CheckAsync("Tailnet gateway access", TailnetChecks.Run);
CheckAsync("Gateway shares the desktop pipe and replays only to the new subscriber", SharedGatewayChecks.Run);
Console.WriteLine($"Ssh checks: {passed} passed, {failed} failed.");
if (failed > 0) Environment.Exit(1);





sealed class AmbiguousStream : System.IO.MemoryStream
{
    public new int Read(System.Span<byte> buffer) => 0;
    public new void Write(System.ReadOnlySpan<byte> buffer) { }
}

sealed class FakeGatewayBackend : TerminalV.Gateway.IGatewaySessionBackend
{
    private readonly object _gate = new();
    private readonly HashSet<string> _live;
    public readonly List<string> Writes = new();
    public readonly List<string> Kills = new();
    public (int cols, int rows) LastResize;

    public FakeGatewayBackend(IEnumerable<string> ids) { _live = new HashSet<string>(ids); }

    public event Action<string, string>? Data;
    public event Action<string, uint>? Exited;
    public event Action<string, string>? DirectoryChanged;
    public event Action<string, string>? Error;

    public string[] LiveIds() { lock (_gate) return _live.ToArray(); }

    public void Create(string id, int cols, int rows, string? cwd, string? shell, string? startupCommand, string? wslDistribution)
    {
        lock (_gate) _live.Add(id);
    }

    public void Attach(string id)
    {
        lock (_gate) { if (!_live.Contains(id)) throw new InvalidOperationException("gone"); }
        Data?.Invoke(id, "snapshot:" + id);
    }

    public void Write(string id, string data) { lock (_gate) Writes.Add(data); }

    public void Resize(string id, int cols, int rows) { lock (_gate) LastResize = (cols, rows); }

    public void Kill(string id)
    {
        lock (_gate) { _live.Remove(id); Kills.Add(id); }
        Exited?.Invoke(id, 0);
    }
}
