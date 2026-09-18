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

Console.WriteLine($"Ssh checks: {passed} passed, {failed} failed.");
if (failed > 0) Environment.Exit(1);
