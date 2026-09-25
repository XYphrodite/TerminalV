using System.Diagnostics;
using System.IO;
using System.Text.Json;
using TerminalV.Extensibility;

namespace TerminalV.Extensions;

internal sealed class ExtensionManager
{
    private readonly List<Entry> _entries = [];
    private readonly ExtensionStorage _registry;
    private readonly string _dataRoot;
    private readonly string _database;
    private readonly Action<object> _post;
    private readonly Action<string, string> _log;
    private readonly Task _started;
    private readonly object _gate = new();
    private Task? _stopTask;
    private volatile bool _stopping;

    public string DirectoryPath { get; }
    public IEnumerable<(string Host, string Directory)> UiMappings => _entries
        .Where(e => e.Manifest?.Ui is not null)
        .Select(e => (UiHost(e.Manifest!.Id), Path.Combine(e.Directory, "ui")));

    public ExtensionManager(string directory, string dataRoot, Action<object> post, Action<string, string> log)
    {
        DirectoryPath = directory;
        _dataRoot = dataRoot;
        _database = Path.Combine(dataRoot, "extensions.db");
        _post = post;
        _log = log;
        Directory.CreateDirectory(directory);
        _registry = new ExtensionStorage(_database, "$enabled");
        foreach (var package in Directory.EnumerateDirectories(directory).Order(StringComparer.OrdinalIgnoreCase))
        {
            var entry = new Entry(package);
            _entries.Add(entry);
            try
            {
                entry.Manifest = ExtensionManifest.Read(package);
                entry.Enabled = entry.StartupEnabled = _registry.Get(entry.Manifest.Id) == "true";
            }
            catch (Exception ex)
            {
                entry.Error = ex.Message;
                _log(entry.Id, ex.ToString());
            }
        }
        _started = Task.Run(StartAsync);
    }

    public async Task<object[]> CatalogAsync()
    {
        await _started.ConfigureAwait(false);
        lock (_gate)
            return _entries.Select(e => (object)new
            {
                id = e.Id, name = e.Manifest?.Name ?? e.Id, version = e.Manifest?.Version,
                description = e.Manifest?.Description, enabled = e.Enabled, active = e.Active,
                valid = e.Manifest is not null, error = e.Error,
                restartRequired = e.Enabled != e.StartupEnabled,
                uiUrl = e.Active && e.Manifest?.Ui is { } ui
                    ? $"https://{UiHost(e.Id)}/{string.Join("/", ui[3..].Split('/').Select(Uri.EscapeDataString))}" : null
            }).ToArray();
    }

    // A single response envelope handles both management and extension requests.
    public async Task HandleAsync(string type, string? id, string? requestId, string? method,
        JsonElement? args, bool enabled)
    {
        if (string.IsNullOrWhiteSpace(requestId)) return;
        try
        {
            if (_stopping) throw new InvalidOperationException("Extensions are stopping.");
            await _started.ConfigureAwait(false);
            object? result;
            switch (type)
            {
                case "extensions:list":
                    result = await CatalogAsync().ConfigureAwait(false);
                    break;
                case "extensions:set-enabled":
                    var configured = Find(id);
                    if (configured.Manifest is null) throw new InvalidOperationException("Fix the extension manifest first.");
                    lock (_gate)
                    {
                        _registry.Set(configured.Id, enabled ? "true" : null);
                        configured.Enabled = enabled;
                    }
                    result = await CatalogAsync().ConfigureAwait(false);
                    break;
                case "extensions:open-folder":
                    using (Process.Start(new ProcessStartInfo(DirectoryPath) { UseShellExecute = true })) { }
                    result = null;
                    break;
                case "extensions:storage":
                    var stored = RequireActive(id);
                    var key = args?.GetProperty("key").GetString() ?? "";
                    var storage = new ExtensionStorage(_database, stored.Id);
                    if (method == "get") result = storage.Get(key);
                    else if (method == "set")
                    {
                        storage.Set(key, args?.GetProperty("value").GetString());
                        result = null;
                    }
                    else throw new ArgumentException("Unknown storage operation.");
                    break;
                case "extensions:invoke":
                    var entry = RequireActive(id);
                    if (entry.Instance is null) throw new InvalidOperationException("This extension has no C# host.");
                    if (string.IsNullOrWhiteSpace(method) || method.Length > 128)
                        throw new ArgumentException("A method name is required (up to 128 characters).");
                    using (var timeout = CancellationTokenSource.CreateLinkedTokenSource(entry.Lifetime.Token))
                    {
                        timeout.CancelAfter(TimeSpan.FromSeconds(30));
                        Task<JsonElement?> invocation;
                        lock (entry.Calls)
                        {
                            if (_stopping) throw new InvalidOperationException("Extensions are stopping.");
                            var instance = entry.Instance ?? throw new InvalidOperationException("Extension is stopping.");
                            invocation = Task.Run(() => instance.InvokeAsync(method, args, timeout.Token));
                            entry.Calls.Add(invocation);
                        }
                        _ = invocation.ContinueWith(completed => { lock (entry.Calls) entry.Calls.Remove(completed); },
                            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
                        Observe(invocation, entry.Id);
                        result = await invocation.WaitAsync(timeout.Token).ConfigureAwait(false);
                    }
                    break;
                default:
                    throw new ArgumentException("Unknown extension request.");
            }
            _post(new { type = "extensions:result", extensionId = id, requestId, result });
        }
        catch (Exception ex)
        {
            _log(id ?? "manager", ex.ToString());
            _post(new { type = "extensions:result", extensionId = id, requestId,
                error = ex is OperationCanceledException ? "Extension request cancelled or timed out." : ex.Message });
        }
    }

    private async Task StartAsync()
    {
        // Start independently: one slow extension must not hold up other packages.
        await Task.WhenAll(_entries.Where(e => e.StartupEnabled).Select(StartEntryAsync)).ConfigureAwait(false);
    }

    private async Task StartEntryAsync(Entry entry)
    {
        Task? activation = null;
        try
        {
            activation = Task.Run(async () =>
            {
                var manifest = entry.Manifest!;
                if (manifest.Host is { } host)
                {
                    entry.LoadContext = new ExtensionLoadContext(ExtensionManifest.ResolveFile(entry.Directory, host.Assembly));
                    var assembly = entry.LoadContext.LoadFromAssemblyPath(ExtensionManifest.ResolveFile(entry.Directory, host.Assembly));
                    var type = assembly.GetType(host.Type, throwOnError: true)!;
                    if (!typeof(ITerminalVExtension).IsAssignableFrom(type) || type.IsAbstract)
                        throw new InvalidDataException("The host type must implement ITerminalVExtension.");
                    entry.Instance = (ITerminalVExtension)Activator.CreateInstance(type)!;
                    var dataDirectory = Path.Combine(_dataRoot, entry.Id);
                    Directory.CreateDirectory(dataDirectory);
                    var context = new ExtensionContext(entry.Id, dataDirectory,
                        new ExtensionStorage(_database, entry.Id), new ExtensionStorage(_database, entry.Id, secret: true),
                        (name, data) =>
                        {
                            if (_stopping || entry.Lifetime.IsCancellationRequested) return;
                            if (string.IsNullOrWhiteSpace(name) || name.Length > 128)
                                throw new ArgumentException("An event name is required (up to 128 characters).");
                            _post(new { type = "extensions:event", extensionId = entry.Id, @event = name,
                                data = JsonSerializer.SerializeToElement(data, ExtensionManifest.Json) });
                        }, message => _log(entry.Id, message));
                    await entry.Instance.ActivateAsync(context, entry.Lifetime.Token).ConfigureAwait(false);
                }
            });
            await activation.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
            entry.Active = true;
        }
        catch (Exception ex)
        {
            entry.Error = ex is TimeoutException ? "Extension activation exceeded 10 seconds." : ex.GetBaseException().Message;
            _log(entry.Id, ex.ToString());
            // Do not dispose a constructor/activation while it is still running.
            entry.Cleanup = Task.Run(async () =>
            {
                try { await entry.Lifetime.CancelAsync().ConfigureAwait(false); }
                finally { await CleanupAfterAsync(entry, activation ?? Task.CompletedTask).ConfigureAwait(false); }
            });
            Observe(entry.Cleanup, entry.Id);
        }
    }

    public Task StopAsync()
    {
        lock (_gate)
        {
            _stopping = true;
            return _stopTask ??= Task.Run(StopCoreAsync);
        }
    }

    private async Task StopCoreAsync()
    {
        await _started.ConfigureAwait(false);
        await Task.WhenAll(_entries.Where(e => e.Active || e.Cleanup is not null).Select(async entry =>
        {
            var cleanup = entry.Cleanup ??= Task.Run(async () =>
            {
                try { await entry.Lifetime.CancelAsync().ConfigureAwait(false); }
                finally
                {
                    Task[] calls;
                    lock (entry.Calls) calls = entry.Calls.ToArray();
                    // Cooperative requests must finish before their instance is disposed.
                    await CleanupAfterAsync(entry, Task.WhenAll(calls)).ConfigureAwait(false);
                }
            });
            Observe(cleanup, entry.Id);
            try { await cleanup.WaitAsync(TimeSpan.FromSeconds(3)).ConfigureAwait(false); }
            catch (Exception ex) { _log(entry.Id, ex.Message); }
        })).ConfigureAwait(false);
    }

    private async Task CleanupAfterAsync(Entry entry, Task activation)
    {
        try { await activation.ConfigureAwait(false); }
        catch { /* Activation error is already reported. */ }
        try
        {
            if (entry.Instance is not null) await entry.Instance.DisposeAsync().ConfigureAwait(false);
        }
        finally
        {
            entry.Instance = null;
            entry.Active = false;
            entry.LoadContext?.Unload();
            entry.LoadContext = null;
            entry.Lifetime.Dispose();
        }
    }

    private void Observe(Task task, string id) => _ = task.ContinueWith(
        failed => _log(id, failed.Exception!.ToString()), CancellationToken.None,
        TaskContinuationOptions.OnlyOnFaulted, TaskScheduler.Default);

    private Entry Find(string? id) => _entries.SingleOrDefault(e => e.Id == id)
        ?? throw new ArgumentException("Unknown extension.");

    private Entry RequireActive(string? id)
    {
        var entry = Find(id);
        if (_stopping || !entry.Active) throw new InvalidOperationException("Extension is not active.");
        return entry;
    }

    private static string UiHost(string id) => id + ".extensions.terminalv.local";

    private sealed class Entry(string directory)
    {
        public string Directory { get; } = directory;
        public string Id => Manifest?.Id ?? Path.GetFileName(Directory);
        public ExtensionManifest? Manifest;
        public bool Enabled;
        public bool StartupEnabled;
        public bool Active;
        public string? Error;
        public ExtensionLoadContext? LoadContext;
        public ITerminalVExtension? Instance;
        public HashSet<Task> Calls { get; } = [];
        public Task? Cleanup;
        public CancellationTokenSource Lifetime { get; } = new();
    }

    private sealed record ExtensionContext(string Id, string DataDirectory, IExtensionStorage Storage,
        IExtensionStorage Secrets, Action<string, object?> Send, Action<string> WriteLog) : IExtensionContext
    {
        public void Publish(string eventName, object? data) => Send(eventName, data);
        public void Log(string message) => WriteLog(message);
    }
}
