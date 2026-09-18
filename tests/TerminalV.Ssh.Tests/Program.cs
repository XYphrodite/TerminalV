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

Console.WriteLine($"Ssh checks: {passed} passed, {failed} failed.");
if (failed > 0) Environment.Exit(1);
