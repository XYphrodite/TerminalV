using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TerminalV.Extensibility;
using TerminalV.Extensions;

if (args.Length == 0)
{
    // DLL unloading is cooperative. Match package replacement in the real app:
    // stop the process before removing its package files on Windows.
    var testRoot = Path.Combine(Path.GetTempPath(), "terminalv-extension-tests-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(testRoot);
    try
    {
        var executable = Environment.ProcessPath ?? throw new InvalidOperationException("Missing test executable.");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true };
        if (string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase))
            start.ArgumentList.Add(typeof(BrokenExtension).Assembly.Location);
        start.ArgumentList.Add("--worker");
        start.ArgumentList.Add(testRoot);
        using var worker = Process.Start(start) ?? throw new InvalidOperationException("Could not start extension checks.");
        await worker.WaitForExitAsync();
        return worker.ExitCode;
    }
    finally
    {
        // Only remove the unique directory created by this parent, after worker exit.
        Directory.Delete(testRoot, recursive: true);
    }
}
if (args.Length != 2 || args[0] != "--worker") throw new ArgumentException("Invalid test arguments.");
var root = Path.GetFullPath(args[1]);
var checks = 0;
var managers = new List<ExtensionManager>();
try
{
    var packages = Path.Combine(root, "extensions");
    var data = Path.Combine(root, "extension-data");
    var database = Path.Combine(data, "extensions.db");
    Directory.CreateDirectory(packages);
    var hello = Package("terminalv.hello", typeof(HelloExtension.HelloExtension));
    Package("test.broken", typeof(BrokenExtension));
    Package("test.lifecycle", typeof(LifecycleExtension), ui: false);
    Package("test.ui-only", null);
    var malformed = Path.Combine(packages, "test.invalid");
    Directory.CreateDirectory(malformed);
    File.WriteAllText(Path.Combine(malformed, "extension.json"), "{bad json");

    Check("manifest validates SDK version and both entry points", () =>
    {
        var manifest = ExtensionManifest.Read(hello);
        Assert(manifest.ApiVersion == 1 && manifest.Ui == "ui/main.js", "Manifest fields missing");
        var original = File.ReadAllText(Path.Combine(hello, "extension.json"));
        File.WriteAllText(Path.Combine(hello, "extension.json"), original.Replace("\"apiVersion\":1", "\"apiVersion\":99"));
        Throws(() => ExtensionManifest.Read(hello));
        File.WriteAllText(Path.Combine(hello, "extension.json"), original);
    });
    Check("null or missing apiVersion fails with a clear error instead of JsonException", () =>
    {
        var original = File.ReadAllText(Path.Combine(hello, "extension.json"));
        foreach (var patched in new[]
        {
            original.Replace("\"apiVersion\":1", "\"apiVersion\":null"),
            original.Replace("\"apiVersion\":1,", "")
        })
        {
            File.WriteAllText(Path.Combine(hello, "extension.json"), patched);
            var thrown = false;
            try
            {
                ExtensionManifest.Read(hello);
            }
            catch (InvalidDataException)
            {
                thrown = true;
            }
            Assert(thrown, "Expected InvalidDataException for a bad apiVersion");
        }
        File.WriteAllText(Path.Combine(hello, "extension.json"), original);
    });
    Check("installer survives a manifest whose numeric fields are JSON null", () =>
    {
        var zip = Path.Combine(root, "null-api.zip");
        var staging = Path.Combine(root, "null-api-pkg");
        Directory.CreateDirectory(staging);
        File.WriteAllText(Path.Combine(staging, "extension.json"), """
            {"id":"test.null-api","name":"n","version":"1.0.0","apiVersion":null,"ui":"ui/main.js"}
            """);
        Directory.CreateDirectory(Path.Combine(staging, "ui"));
        File.WriteAllText(Path.Combine(staging, "ui", "main.js"), "export function activate() {}");
        System.IO.Compression.ZipFile.CreateFromDirectory(staging, zip);
        var fresh = Path.Combine(root, "install-null-api");
        var installData = Path.Combine(root, "install-null-api-data");
        Directory.CreateDirectory(fresh);
        Directory.CreateDirectory(installData);
        var leaked = false;
        try
        {
            ExtensionInstaller.Install(zip, fresh, enable: false, dataRoot: installData);
        }
        catch (InvalidOperationException ex) when (ex.Message.Contains("Number", StringComparison.Ordinal))
        {
            leaked = true;
        }
        catch (InvalidDataException)
        {
            // expected: clear validation error
        }
        Assert(!leaked, "STJ null leak: InvalidOperationException Number/Null");
    });
    Check("paths cannot escape the package or use alternate streams", () =>
    {
        foreach (var path in new[] { "../outside.dll", "host/../../outside.dll", "C:/outside.dll", "ui/main.js:other", "ui\\main.js" })
            Throws(() => ExtensionManifest.ResolveFile(hello, path));
    });
    Check("installer unpacks a zip, validates the manifest and enables the package", () =>
    {
        var zip = Path.Combine(root, "terminalv.hello.zip");
        System.IO.Compression.ZipFile.CreateFromDirectory(hello, zip, System.IO.Compression.CompressionLevel.Fastest, includeBaseDirectory: true);
        var fresh = Path.Combine(root, "install-root");
        var installData = Path.Combine(root, "install-data");
        Directory.CreateDirectory(fresh);
        Directory.CreateDirectory(installData);
        var result = ExtensionInstaller.Install(zip, fresh, enable: true, replace: false, dataRoot: installData);
        Assert(result.Id == "terminalv.hello", "Wrong installed id");
        Assert(result.Enabled && !result.Replaced, "Install flags incorrect");
        Assert(File.Exists(Path.Combine(result.TargetDirectory, "extension.json")), "Package not on disk");
        Assert(new ExtensionStorage(Path.Combine(installData, "extensions.db"), "$enabled").Get("terminalv.hello") == "true", "Enable flag not stored");
        Throws(() => ExtensionInstaller.Install(zip, fresh, enable: true, replace: false, dataRoot: installData));
        var replaced = ExtensionInstaller.Install(zip, fresh, enable: false, replace: true, dataRoot: installData);
        Assert(replaced.Replaced && !replaced.Enabled, "Replace did not keep the package without re-enabling");
    });
    Check("installer rejects unsafe zip entries and a missing manifest", () =>
    {
        var fresh = Path.Combine(root, "install-bad");
        var installData = Path.Combine(root, "install-bad-data");
        Directory.CreateDirectory(fresh);
        Directory.CreateDirectory(installData);
        var evil = Path.Combine(root, "evil.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(evil, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("../escape.json");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("{}");
        }
        Throws(() => ExtensionInstaller.Install(evil, fresh, enable: false, dataRoot: installData));
        var empty = Path.Combine(root, "empty.zip");
        using (var archive = System.IO.Compression.ZipFile.Open(empty, System.IO.Compression.ZipArchiveMode.Create))
        {
            var entry = archive.CreateEntry("readme.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("no manifest");
        }
        Throws(() => ExtensionInstaller.Install(empty, fresh, enable: false, dataRoot: installData));
    });
    Check("installer refuses a checksum mismatch", () =>
    {
        var zip = Path.Combine(root, "terminalv.hello.zip");
        var fresh = Path.Combine(root, "install-hash");
        var installData = Path.Combine(root, "install-hash-data");
        Directory.CreateDirectory(fresh);
        Directory.CreateDirectory(installData);
        Throws(() => ExtensionInstaller.Install(zip, fresh, enable: false, expectedSha256: new string('0', 64), dataRoot: installData));
    });
    Check("storage separates extensions and secrets, persists and deletes", () =>
    {
        var a = new ExtensionStorage(database, "test.a");
        var b = new ExtensionStorage(database, "test.b");
        var secrets = new ExtensionStorage(database, "test.a", secret: true);
        a.Set("key", "public");
        secrets.Set("key", "my-test-secret");
        Assert(b.Get("key") is null, "Leaked across extension namespaces");
        Assert(new ExtensionStorage(database, "test.a").Get("key") == "public", "Did not persist");
        Assert(secrets.Get("key") == "my-test-secret", "DPAPI roundtrip failed");
        using var connection = new SqliteConnection($"Data Source={database}");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM extension_values WHERE scope='test.a' AND secret=1";
        var ciphertext = (string)command.ExecuteScalar()!;
        Assert(!ciphertext.Contains("my-test-secret"), "Secret was stored in plaintext");
        a.Set("key", null);
        Assert(a.Get("key") is null && secrets.Get("key") == "my-test-secret", "Deletion crossed storage areas");
    });

    var messages = new ConcurrentQueue<JsonElement>();
    var manager = NewManager();
    var catalog = JsonSerializer.SerializeToElement(await manager.CatalogAsync());
    Check("discovery leaves new packages disabled and reports malformed packages", () =>
    {
        Assert(catalog.GetArrayLength() == 5, "Missing package");
        Assert(catalog.EnumerateArray().All(e => !e.GetProperty("active").GetBoolean()), "Auto-enabled a package");
        Assert(Item(catalog, "test.invalid").GetProperty("error").ValueKind == JsonValueKind.String, "Missing manifest error");
        Assert(manager.UiMappings.Count() == 3, "Unexpected UI mappings");
    });
    var disabled = await Request(manager, "extensions:invoke", "terminalv.hello", "getState");
    Assert(disabled.TryGetProperty("error", out _), "Invoked a disabled DLL");
    await Request(manager, "extensions:set-enabled", "terminalv.hello", enabled: true);
    await Request(manager, "extensions:set-enabled", "test.broken", enabled: true);
    await Request(manager, "extensions:set-enabled", "test.lifecycle", enabled: true);
    await Request(manager, "extensions:set-enabled", "test.ui-only", enabled: true);
    catalog = JsonSerializer.SerializeToElement(await manager.CatalogAsync());
    Check("enabling is persistent and explicitly pending restart", () =>
    {
        var item = Item(catalog, "terminalv.hello");
        Assert(item.GetProperty("enabled").GetBoolean() && item.GetProperty("restartRequired").GetBoolean(), "Missing pending state");
        Assert(!item.GetProperty("active").GetBoolean(), "Loaded without restart");
    });
    await manager.StopAsync();
    manager = NewManager();
    catalog = JsonSerializer.SerializeToElement(await manager.CatalogAsync());
    Check("DLL loads against shared SDK and one broken extension does not stop another", () =>
    {
        Assert(Item(catalog, "terminalv.hello").GetProperty("active").GetBoolean(), "Hello extension failed to activate");
        Assert(Item(catalog, "test.broken").GetProperty("error").GetString()!.Contains("deliberate"), "Missing activation failure");
        Assert(Item(catalog, "terminalv.hello").GetProperty("uiUrl").GetString() ==
            "https://terminalv.hello.extensions.terminalv.local/main.js", "Incorrect module URL");
        Assert(Item(catalog, "test.ui-only").GetProperty("active").GetBoolean(), "UI-only extension failed to activate");
        Assert(Item(catalog, "test.lifecycle").GetProperty("uiUrl").ValueKind == JsonValueKind.Null, "Host-only package exposed UI");
    });
    var increment = await Request(manager, "extensions:invoke", "terminalv.hello", "increment");
    Check("RPC and events cross the real DLL boundary with request identity", () =>
    {
        Assert(increment.GetProperty("result").GetProperty("count").GetInt32() == 1, "Incorrect result");
        Assert(increment.GetProperty("extensionId").GetString() == "terminalv.hello", "Missing extension identity");
        Assert(messages.Any(m => m.GetProperty("type").GetString() == "extensions:event"
            && m.GetProperty("event").GetString() == "changed"
            && m.GetProperty("data").GetProperty("count").GetInt32() == 1), "Missing host event");
    });
    var read = await Request(manager, "extensions:storage", "terminalv.hello", "get", new { key = "count" });
    Assert(read.GetProperty("result").GetString() == "1", "UI and C# storage differ");
    var failure = await Request(manager, "extensions:invoke", "terminalv.hello", "unknown");
    Assert(failure.TryGetProperty("error", out _), "Missing RPC error");
    var missing = await Request(manager, "extensions:invoke", "unknown.id", "getState");
    Assert(missing.TryGetProperty("error", out _), "Unknown extension request succeeded");
    var waiting = Request(manager, "extensions:invoke", "test.lifecycle", "wait");
    using (var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
        while (!messages.Any(m => m.TryGetProperty("event", out var name) && name.GetString() == "waiting"))
            await Task.Delay(10, deadline.Token);
    await manager.StopAsync().WaitAsync(TimeSpan.FromSeconds(5));
    var cancelled = await waiting;
    Check("shutdown cancels in-flight requests before disposing the DLL", () =>
    {
        Assert(cancelled.TryGetProperty("error", out _), "Pending request did not cancel");
        Assert(new ExtensionStorage(database, "test.lifecycle").Get("disposed") == "true", "Dispose ran before request stopped");
    });
    manager = NewManager();
    await manager.CatalogAsync();
    var restored = await Request(manager, "extensions:invoke", "terminalv.hello", "getState");
    Check("state survives restart; disabling stops activation on the next launch", () =>
        Assert(restored.GetProperty("result").GetProperty("count").GetInt32() == 1, "Lost state"));
    await Request(manager, "extensions:set-enabled", "terminalv.hello", enabled: false);
    var stillActive = await Request(manager, "extensions:invoke", "terminalv.hello", "getState");
    Assert(stillActive.TryGetProperty("result", out _), "Disabled running instance before restart");
    await manager.StopAsync();
    manager = NewManager();
    catalog = JsonSerializer.SerializeToElement(await manager.CatalogAsync());
    Assert(!Item(catalog, "terminalv.hello").GetProperty("active").GetBoolean(), "Disabled extension started");
    // ---- extension updates: version rules, release lookup, staging and apply-on-restart ----
    Check("extension versions compare numerically and rank releases above pre-releases", () =>
    {
        Assert(ExtensionVersion.Compare("1.10.0", "1.9.9") > 0, "1.10.0 must beat 1.9.9");
        Assert(ExtensionVersion.Compare("1.2.0", "1.2.0+build") == 0, "Build metadata must be ignored");
        Assert(ExtensionVersion.Compare("2.0.0", "2.0.0-beta.1") > 0, "Release must beat pre-release");
        Assert(ExtensionVersion.Compare("1.0.0", "1.0.1") < 0, "1.0.0 is older than 1.0.1");
        Assert(ExtensionVersion.Compare("garbage", "1.0.0") < 0, "Unparseable versions never win");
    });

    var updateRoot = Path.Combine(root, "upd");
    string UpdPackage(string parent, string id, string version)
    {
        var directory = Path.Combine(parent, id);
        Directory.CreateDirectory(Path.Combine(directory, "ui"));
        File.WriteAllText(Path.Combine(directory, "ui", "main.js"), "export function activate() {}");
        File.WriteAllText(Path.Combine(directory, "extension.json"), JsonSerializer.Serialize(new
        {
            id, name = id, version, apiVersion = 1, ui = "ui/main.js"
        }));
        return directory;
    }
    var served = Path.Combine(updateRoot, "served");
    var remoteZip = Path.Combine(updateRoot, "test.upd.zip");
    Directory.CreateDirectory(updateRoot);
    System.IO.Compression.ZipFile.CreateFromDirectory(UpdPackage(served, "test.upd", "1.1.0"), remoteZip,
        System.IO.Compression.CompressionLevel.Fastest, includeBaseDirectory: true);
    var remoteHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(remoteZip))).ToLowerInvariant();
    var serveIndex = true;
    var updatePort = Random.Shared.Next(20000, 60000);
    using var listener = new System.Net.HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{updatePort}/");
    listener.Start();
    var updateBase = $"http://127.0.0.1:{updatePort}";
    _ = Task.Run(async () =>
    {
        while (listener.IsListening)
        {
            System.Net.HttpListenerContext context;
            try { context = await listener.GetContextAsync(); }
            catch { return; }
            var path = context.Request.Url!.AbsolutePath;
            byte[]? body = null;
            if (path == "/extensions.json" && serveIndex)
                body = System.Text.Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new
                {
                    extensions = new[] { new { id = "test.upd", version = "1.1.0", asset = "test.upd.zip", sha256 = remoteHash } }
                }));
            else if (path == "/test.upd.zip") body = File.ReadAllBytes(remoteZip);
            context.Response.StatusCode = body is null ? 404 : 200;
            if (body is not null) await context.Response.OutputStream.WriteAsync(body);
            context.Response.Close();
        }
    });

    string NewTree(string name, params (string Id, string Version)[] installed)
    {
        var tree = Path.Combine(updateRoot, name);
        var extensions = Path.Combine(tree, "extensions");
        Directory.CreateDirectory(extensions);
        foreach (var (id, version) in installed) UpdPackage(extensions, id, version);
        return extensions;
    }

    var treeA = NewTree("a", ("test.upd", "1.0.0"), ("test.other", "1.0.0"));
    Check("release index reports only newer, published extensions", () =>
    {
        var found = ExtensionUpdater.Check(treeA, updateBase);
        Assert(found.Count == 1 && found[0].Id == "test.upd", "Expected one update for test.upd");
        Assert(found[0].InstalledVersion == "1.0.0" && found[0].AvailableVersion == "1.1.0", "Wrong versions");
        Assert(found[0].Sha256 == remoteHash, "Index checksum was not carried over");
        UpdPackage(treeA, "test.same", "1.1.0");
        Assert(ExtensionUpdater.Check(treeA, updateBase).Count == 1, "An extension that is not in the release must be skipped");
        Directory.Delete(Path.Combine(treeA, "test.same"), recursive: true);
    });
    Check("without an index the package manifest is probed instead", () =>
    {
        serveIndex = false;
        try
        {
            var found = ExtensionUpdater.Check(treeA, updateBase);
            Assert(found.Count == 1 && found[0].Id == "test.upd" && found[0].AvailableVersion == "1.1.0",
                "Fallback probe did not find the update");
        }
        finally { serveIndex = true; }
    });
    Check("up to date and unreachable releases", () =>
    {
        var current = NewTree("current", ("test.upd", "1.1.0"));
        Assert(ExtensionUpdater.Check(current, updateBase).Count == 0, "No update expected at the same version");
        Throws(() => ExtensionUpdater.Check(treeA, "http://127.0.0.1:1"));
    });
    Check("staging verifies the checksum and leaves the installed copy untouched", () =>
    {
        var update = ExtensionUpdater.Check(treeA, updateBase).Single();
        Throws(() => ExtensionUpdater.Stage(update with { Sha256 = new string('0', 64) }, treeA));
        Assert(ExtensionUpdater.PendingVersion(treeA, "test.upd") is null, "A bad package must not be staged");
        Throws(() => ExtensionUpdater.Stage(update with { Id = "test.other" }, treeA));
        Assert(ExtensionUpdater.PendingVersion(treeA, "test.other") is null, "A mismatched id must not stay staged");
        ExtensionUpdater.Stage(update, treeA);
        Assert(ExtensionUpdater.PendingVersion(treeA, "test.upd") == "1.1.0", "Update was not staged");
        Assert(ExtensionManifest.Read(Path.Combine(treeA, "test.upd")).Version == "1.0.0", "Installed copy changed before restart");
    });
    Check("staged packages apply once, never downgrade, and the pending folder is cleaned", () =>
    {
        var applied = ExtensionUpdater.ApplyPending(treeA);
        Assert(applied.Count == 1 && applied[0] == "test.upd", "Staged update was not applied");
        Assert(ExtensionManifest.Read(Path.Combine(treeA, "test.upd")).Version == "1.1.0", "Version did not change");
        Assert(!Directory.Exists(ExtensionUpdater.PendingRoot(treeA)), "Pending folder left behind");
        Assert(!Directory.EnumerateDirectories(treeA).Any(d => d.Contains(".old-")), "Backup left behind");
        var pending = ExtensionUpdater.PendingRoot(treeA);
        UpdPackage(pending, "test.upd", "1.0.5");
        Assert(ExtensionUpdater.ApplyPending(treeA).Count == 0, "An older staged package must not be applied");
        Assert(ExtensionManifest.Read(Path.Combine(treeA, "test.upd")).Version == "1.1.0", "Downgraded");
        Assert(!Directory.Exists(pending), "Rejected package left behind");
        UpdPackage(pending, "test.upd", "1.2.0");
        File.WriteAllText(Path.Combine(pending, "test.upd", "extension.json"), "{bad");
        Assert(ExtensionUpdater.ApplyPending(treeA).Count == 0, "A corrupt package must not be applied");
        Assert(!Directory.Exists(pending), "Corrupt package left behind");
    });

    var treeB = NewTree("b", ("test.upd", "1.0.0"));
    var managerB = new ExtensionManager(treeB, Path.Combine(updateRoot, "b", "data"),
        payload => messages.Enqueue(JsonSerializer.SerializeToElement(payload)), (_, _) => { },
        updateBaseUrl: updateBase, autoCheckUpdates: false);
    managers.Add(managerB);
    var checked1 = (await Request(managerB, "extensions:check-updates", null!)).GetProperty("result");
    var itemB = Item(checked1, "test.upd");
    Check("manager reports an available update in the catalog", () =>
    {
        Assert(itemB.GetProperty("updateVersion").GetString() == "1.1.0", "updateVersion missing");
        Assert(itemB.GetProperty("stagedVersion").ValueKind == JsonValueKind.Null, "Nothing is staged yet");
        Assert(itemB.GetProperty("version").GetString() == "1.0.0", "Installed version must not change yet");
    });
    var staged1 = (await Request(managerB, "extensions:update", "test.upd")).GetProperty("result");
    Check("manager stages the update and reports it as waiting for a restart", () =>
    {
        var item = Item(staged1, "test.upd");
        Assert(item.GetProperty("stagedVersion").GetString() == "1.1.0", "stagedVersion missing");
        Assert(item.GetProperty("version").GetString() == "1.0.0", "Running version changed");
    });
    var unknown = await Request(managerB, "extensions:update", "test.nothing");
    Check("updating an extension without a known update reports an error", () =>
        Assert(unknown.TryGetProperty("error", out _), "Expected an error"));
    await managerB.StopAsync();
    var restartedB = new ExtensionManager(treeB, Path.Combine(updateRoot, "b", "data"),
        payload => messages.Enqueue(JsonSerializer.SerializeToElement(payload)), (_, _) => { },
        updateBaseUrl: updateBase, autoCheckUpdates: false);
    managers.Add(restartedB);
    var afterRestart = Item(JsonSerializer.SerializeToElement(await restartedB.CatalogAsync()), "test.upd");
    Check("the staged update is applied on the next manager start", () =>
    {
        Assert(afterRestart.GetProperty("version").GetString() == "1.1.0", "Update was not applied on restart");
        Assert(afterRestart.GetProperty("stagedVersion").ValueKind == JsonValueKind.Null, "Still marked as staged");
    });
    var leftover = Path.Combine(treeB, "test.upd.old-" + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(Path.Combine(leftover, "host"));
    File.WriteAllText(Path.Combine(leftover, "host", "stale.dll"), "x");
    var cleaned = new ExtensionManager(treeB, Path.Combine(updateRoot, "b", "data"),
        payload => messages.Enqueue(JsonSerializer.SerializeToElement(payload)), (_, _) => { },
        updateBaseUrl: updateBase, autoCheckUpdates: false);
    managers.Add(cleaned);
    var cleanedCatalog = JsonSerializer.SerializeToElement(await cleaned.CatalogAsync());
    Check("leftover backups of replaced packages are cleaned and never listed as extensions", () =>
    {
        Assert(cleanedCatalog.GetArrayLength() == 1, "A backup folder was listed as an extension");
        Assert(!Directory.Exists(leftover), "Backup folder was not removed");
        Assert(ExtensionUpdater.IsBackupDirectory(leftover) && !ExtensionUpdater.IsBackupDirectory(treeB), "Backup name detection");
    });    listener.Stop();
    Console.WriteLine($"Passed {checks} extension checks.");

    ExtensionManager NewManager()
    {
        var instance = new ExtensionManager(packages, data,
            payload => messages.Enqueue(JsonSerializer.SerializeToElement(payload)), (_, _) => { });
        managers.Add(instance);
        return instance;
    }

    async Task<JsonElement> Request(ExtensionManager target, string type, string id, string? method = null,
        object? args = null, bool enabled = false)
    {
        var requestId = Guid.NewGuid().ToString("N");
        await target.HandleAsync(type, id, requestId, method,
            args is null ? null : JsonSerializer.SerializeToElement(args), enabled);
        return messages.Single(m => m.TryGetProperty("requestId", out var value) && value.GetString() == requestId);
    }

    string Package(string id, Type? entry, bool ui = true)
    {
        var directory = Path.Combine(packages, id);
        Directory.CreateDirectory(Path.Combine(directory, "ui"));
        Directory.CreateDirectory(Path.Combine(directory, "host"));
        File.WriteAllText(Path.Combine(directory, "ui", "main.js"), "export function activate() {}");
        var assembly = entry is null ? null : Path.GetFileName(entry.Assembly.Location);
        if (entry is not null) File.Copy(entry.Assembly.Location, Path.Combine(directory, "host", assembly!));
        File.WriteAllText(Path.Combine(directory, "extension.json"), JsonSerializer.Serialize(new
        {
            id, name = id, version = "1.0.0", apiVersion = 1, ui = ui ? "ui/main.js" : null,
            host = entry is null ? null : new { assembly = "host/" + assembly, type = entry.FullName }
        }));
        return directory;
    }
}
finally
{
    foreach (var manager in managers) await manager.StopAsync();
    SqliteConnection.ClearAllPools();
}

return 0;

void Check(string name, Action action) { action(); checks++; Console.WriteLine("PASS " + name); }
static void Assert(bool condition, string message) { if (!condition) throw new Exception(message); }
static void Throws(Action action)
{
    try { action(); }
    catch (Exception) { return; }
    throw new Exception("Expected an exception");
}
static JsonElement Item(JsonElement catalog, string id) => catalog.EnumerateArray().Single(e => e.GetProperty("id").GetString() == id);

public sealed class BrokenExtension : ITerminalVExtension
{
    public Task ActivateAsync(IExtensionContext context, CancellationToken lifetime) => throw new Exception("deliberate activation failure");
    public Task<JsonElement?> InvokeAsync(string method, JsonElement? args, CancellationToken token) => throw new NotImplementedException();
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

public sealed class LifecycleExtension : ITerminalVExtension
{
    private IExtensionContext _context = null!;
    private bool _waiting;
    public Task ActivateAsync(IExtensionContext context, CancellationToken lifetime)
    {
        _context = context;
        return Task.CompletedTask;
    }
    public async Task<JsonElement?> InvokeAsync(string method, JsonElement? args, CancellationToken token)
    {
        _waiting = true;
        _context.Publish("waiting", null);
        try { await Task.Delay(Timeout.Infinite, token); }
        finally { _waiting = false; }
        return null;
    }
    public ValueTask DisposeAsync()
    {
        if (_waiting) throw new Exception("Dispose raced with an active request");
        _context.Storage.Set("disposed", "true");
        return ValueTask.CompletedTask;
    }
}
