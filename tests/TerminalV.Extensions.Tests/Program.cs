using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using TerminalV.Extensibility;
using TerminalV.Extensions;

var root = Path.Combine(Path.GetTempPath(), "terminalv-extension-tests-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
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
    Check("paths cannot escape the package or use alternate streams", () =>
    {
        foreach (var path in new[] { "../outside.dll", "host/../../outside.dll", "C:/outside.dll", "ui/main.js:other", "ui\\main.js" })
            Throws(() => ExtensionManifest.ResolveFile(hello, path));
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
    // Collectible load contexts release Windows DLL handles when collected.
    for (var attempt = 0; attempt < 3; attempt++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
    SqliteConnection.ClearAllPools();
    // Only delete the unique temporary test directory created above.
    Directory.Delete(root, recursive: true);
}

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
