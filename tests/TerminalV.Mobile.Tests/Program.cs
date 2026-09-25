using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Microsoft.JSInterop;
using TerminalV.Data;
using TerminalV.Gateway;
using TerminalV.Mobile.Host;
using TerminalV.Ssh;

using (var emptySsh = new SshService())
using (var emptyBridge = new MobileBridge(emptySsh, new MobileDataStore()))
{
    var emptyJs = new JsRuntime();
    emptyBridge.SetJS(emptyJs);
    await emptyBridge.SendInitAsync();
    Require(ConnectionState(emptyJs) == "unconfigured", "an untouched install does not claim a default computer is connected");
}

using var listener = new TcpListener(IPAddress.Loopback, 0);
listener.Start();
var port = ((IPEndPoint)listener.LocalEndpoint).Port;
listener.Stop();
var backend = new Backend();
using var server = new GatewayServer(backend, port, "mobile-test");
server.Start();
Preferences.Default.Set("useGateway", true);
Preferences.Default.Set("gatewayUrl", $"ws://127.0.0.1:{port}");
Preferences.Default.Set("gatewayToken", "mobile-test");
Preferences.Default.Set("tailnetComputerName", "Old computer");
using (var legacyClient = new GatewayControlClient($"ws://127.0.0.1:{port}", "mobile-test"))
{
    var legacyCatalog = await legacyClient.ListCatalogAsync();
    Require(legacyCatalog.Sessions is null && legacyCatalog.Ids.SequenceEqual(["desktop"]),
        "gateways without metadata retain the legacy live-id list");
}
var store = new MobileDataStore();
store.SaveSessions([new SessionRecord { Id = "desktop", Title = "Saved", Buffer = "old screen" }]);
using var ssh = new SshService();
using var bridge = new MobileBridge(ssh, store);
var js = new JsRuntime();
bridge.SetJS(js);

await bridge.SendInitAsync();
var appInfo = js.Messages.First();
Require(appInfo.GetProperty("type").GetString() == "app-info", "local app metadata arrives before gateway initialization");
Require(appInfo.GetProperty("version").GetString() == Microsoft.Maui.ApplicationModel.AppInfo.Current.VersionString,
    "mobile version comes from the installed package");
var init = js.Messages.Last(m => m.GetProperty("type").GetString() == "init");
Require(init.GetProperty("version").GetString() == appInfo.GetProperty("version").GetString(),
    "session synchronization preserves the local app version");
Require(init.GetProperty("liveIds").EnumerateArray().Any(v => v.GetString() == "desktop"), "saved session is available for reattachment");
var connectionUpdates = js.Messages.Where(IsConnectionState).ToArray();
Require(connectionUpdates.First().GetProperty("state").GetString() == "connecting" && ConnectionState(js) == "connected",
    "gateway synchronization publishes connecting before the successful connection");
Require(connectionUpdates.Last().GetProperty("name").GetString() == "127.0.0.1",
    "manual gateway summary uses its host instead of a previously paired computer");
Require(backend.AttachCount == 0, "snapshot waits until UI creates its terminal");
await bridge.Handle("""{"type":"attach","id":"desktop"}""");
await bridge.Handle("""{"type":"write","id":"desktop","data":"hello\r"}""");
await Until(() => backend.Writes.Contains("hello\r"));
await Until(() => js.Messages.Any(m => m.GetProperty("type").GetString() == "data"));
Require(backend.AttachCount == 1, "reattachment happens exactly once");
Require(backend.CreateCount == 0, "restoring a desktop session does not create a new shell");
await bridge.Handle("""{"type":"attach","id":"desktop"}""");
var before = js.Messages.Count(m => m.GetProperty("type").GetString() == "data");
backend.Emit("next chunk");
await Until(() => js.Messages.Count(m => m.GetProperty("type").GetString() == "data") > before);
await Task.Delay(100);
Require(js.Messages.Count(m => m.GetProperty("type").GetString() == "data") == before + 1, "duplicate attach does not duplicate output subscriptions");

var previousSession = ssh.TryGet("desktop")!;
await bridge.SendInitAsync(reconnect: true);
await bridge.Handle("""{"type":"attach","id":"desktop"}""");
await bridge.Handle("""{"type":"write","id":"desktop","data":"after reconnect"}""");
await Until(() => backend.Writes.Contains("after reconnect"));
await Until(() => js.Messages.Count(m => m.GetProperty("type").GetString() == "data" && m.GetProperty("data").GetString() == "snapshot") >= 2);
Require(backend.CreateCount == 0, "settings refresh keeps the same desktop shell");
var beforeStaleEvents = js.Messages.Count;
// Deliver delayed callbacks from the retired real transport to reproduce the
// replacement race deterministically, without substituting the SSH service.
RaiseTransportEvent(previousSession, "RaiseData", "stale output");
RaiseTransportEvent(previousSession, "RaiseError", "stale failure");
RaiseTransportEvent(previousSession, "RaiseClosed", null);
Require(js.Messages.Count == beforeStaleEvents && ConnectionState(js) == "connected",
    "late output/error/close from a replaced transport cannot affect the new connection");

var exitCount = js.Messages.Count(m => m.GetProperty("type").GetString() == "exit");
backend.Exit();
await Until(() => js.Messages.Count(m => m.GetProperty("type").GetString() == "exit") > exitCount);
Require(ConnectionState(js) == "connected", "normal shell completion is not reported as a computer connection failure");
var attachCount = backend.AttachCount;
var connectionCount = js.Messages.Count(IsConnectionState);
await bridge.Handle("""{"type":"connection-check"}""");
await Until(() => js.Messages.Count(IsConnectionState) >= connectionCount + 2);
Require(backend.AttachCount == attachCount && backend.CreateCount == 0,
    "checking the sidebar connection does not create or attach shells");

Preferences.Default.Set("gatewayToken", "wrong");
await bridge.SendInitAsync(reconnect: true);
init = js.Messages.Last(m => m.GetProperty("type").GetString() == "init");
Require(!string.IsNullOrEmpty(init.GetProperty("connectionError").GetString()), "authentication failure reaches UI");
Require(ConnectionState(js) == "disconnected", "gateway authentication failure updates the connection summary");

Preferences.Default.Set("tailnetGateway", true);
Preferences.Default.Set("tailnetComputerName", "Xeon");
await bridge.SendInitAsync(reconnect: true);
Require(ConnectionState(js) == "disconnected" && js.Messages.Last(IsConnectionState).GetProperty("name").GetString() == "Xeon",
    "a saved Tailscale computer name does not imply a working connection");
await bridge.SendInitAsync(reconnect: true, connect: false);
await bridge.Handle("""{"type":"connection-check"}""");
Require(ConnectionState(js) == "idle", "logout stays idle and a sidebar check cannot sign the account in again");

Preferences.Default.Set("tailnetGateway", false);
Preferences.Default.Set("gatewayUrl", $"ws://url-user:url-secret@127.0.0.1:{port}/?token=query-secret");
await bridge.SendInitAsync(reconnect: true, connect: false);
var safeSummary = js.Messages.Last(IsConnectionState);
Require(safeSummary.GetProperty("name").GetString() == "127.0.0.1" && !safeSummary.GetRawText().Contains("secret"),
    "connection metadata excludes credentials and query tokens from gateway URLs");

Preferences.Default.Set("useGateway", false);
Preferences.Default.Set("host", "127.0.0.1");
Preferences.Default.Set("port", 1);
await bridge.SendInitAsync(reconnect: true);
Require(ConnectionState(js) == "idle" && js.Messages.Last(IsConnectionState).GetProperty("name").GetString() == "127.0.0.1",
    "configured direct SSH stays idle until a real session connects");
await bridge.Handle("""{"type":"create","id":"direct","cols":80,"rows":24}""");
Require(ssh.TryGet("direct") is SshNetSession, "direct SSH is not overridden by desktop GatewayEnabled setting");
await Until(() => ConnectionState(js) == "disconnected");
Require(true, "failed direct SSH connection updates the summary");

Preferences.Default.Set("useGateway", true);
Preferences.Default.Set("gatewayUrl", $"ws://127.0.0.1:{port}");
Preferences.Default.Set("gatewayToken", "mobile-test");
await bridge.SendInitAsync(reconnect: true);
Require(ConnectionState(js) == "connected", "explicit reconnect recovers the gateway summary after a failure");
server.Stop();
await bridge.Handle("""{"type":"connection-check"}""");
await Until(() => ConnectionState(js) == "disconnected");
Require(true, "on-demand checking detects a stopped gateway even without open sessions");
await Task.Delay(20); // Let the completed check release its single-flight guard.

// Hold a TCP connection before the WebSocket handshake completes. Checking must
// return immediately so the ordered host-message queue remains usable.
using var stalledListener = new TcpListener(IPAddress.Loopback, 0);
stalledListener.Start();
var stalledPort = ((IPEndPoint)stalledListener.LocalEndpoint).Port;
Preferences.Default.Set("gatewayUrl", $"ws://127.0.0.1:{stalledPort}");
var check = bridge.Handle("""{"type":"connection-check"}""");
Require(check.IsCompleted, "a slow connection check does not block terminal messages");
using (var acceptTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
using (var pendingPeer = await stalledListener.AcceptTcpClientAsync(acceptTimeout.Token))
{
    await bridge.SendInitAsync(reconnect: true, connect: false);
    pendingPeer.Close();
    await Task.Delay(150);
    Require(ConnectionState(js) == "idle", "a pending old connection check cannot replace the logged-out state");
}
await CheckDesktopCatalog();
Console.WriteLine("Mobile bridge checks passed.");

static async Task CheckDesktopCatalog()
{
    using var portListener = new TcpListener(IPAddress.Loopback, 0);
    portListener.Start();
    var port = ((IPEndPoint)portListener.LocalEndpoint).Port;
    portListener.Stop();
    string[] desktopIds = ["desktop", "second", "hidden-1", "hidden-2", "hidden-3", "hidden-4"];
    string[] orphanIds = ["orphan-1", "orphan-2", "orphan-3"];
    var backend = new Backend { SessionIds = desktopIds.Concat(orphanIds).ToArray() };
    GatewaySessionInfo[] catalog = backend.SessionIds.Select(id => new GatewaySessionInfo { Id = id, Title = "Old desktop title" }).ToArray();
    using var server = new GatewayServer(backend, port, "catalog-test", sessionCatalog: () => catalog);
    server.Start();
    Preferences.Default.Set("useGateway", true);
    Preferences.Default.Set("gatewayUrl", $"ws://127.0.0.1:{port}");
    Preferences.Default.Set("gatewayToken", "catalog-test");
    Preferences.Default.Set("tailnetGateway", false);
    var store = new MobileDataStore();
    store.SaveSessions(backend.SessionIds.Select(id => new SessionRecord
    {
        Id = id, Title = "Cached phone title", CustomTitle = "Old phone label", Hidden = false,
        Buffer = id == "second" ? null : "cached " + id, Cwd = id == "desktop" ? "/saved/work" : null
    }).Append(new SessionRecord { Id = "cache-only", Title = "Stale cache", Buffer = "old output" }).ToList());
    using var ssh = new SshService();
    using var bridge = new MobileBridge(ssh, store);
    var js = new JsRuntime();
    bridge.SetJS(js);
    await bridge.SendInitAsync();
    await bridge.Handle("""{"type":"attach","id":"orphan-1"}""");
    await bridge.Handle("""{"type":"write","id":"orphan-1","data":"orphan connected"}""");
    await Until(() => backend.Writes.Contains("orphan connected"));
    var retired = ssh.TryGet("orphan-1")!;
    Require(retired is not null, "catalog test has an attached phone transport before desktop removal");

    catalog =
    [
        new() { Id = "desktop", Title = "PowerShell", CustomTitle = "Deploy", SortOrder = 20, Active = true },
        new() { Id = "second", Title = "WSL", SortOrder = 10 },
        new() { Id = "hidden-1", Title = "Hidden one", CustomTitle = "Archive", Hidden = true, SortOrder = 30 },
        new() { Id = "hidden-2", Title = "Hidden two", Hidden = true, SortOrder = 40 },
        new() { Id = "hidden-3", Title = "Hidden three", Hidden = true, SortOrder = 50 },
        new() { Id = "hidden-4", Title = "Hidden four", Hidden = true, SortOrder = 60 }
    ];
    using (var client = new GatewayControlClient($"ws://127.0.0.1:{port}", "catalog-test"))
    {
        var reply = await client.ListCatalogAsync();
        Require(reply.Ids.SequenceEqual(["second", "desktop"]) && reply.Sessions is { Count: 6 },
            "catalog includes hidden desktop tabs while legacy ids include only visible live tabs");
    }
    await bridge.SendInitAsync();
    var init = js.Messages.Last(m => m.GetProperty("type").GetString() == "init");
    var sessions = store.LoadSessions();
    Require(init.GetProperty("sessionsAuthoritative").GetBoolean(),
        "successful desktop catalog init explicitly replaces the existing phone view");
    Require(sessions.Count == 6 && sessions.Count(s => !s.Hidden) == 2 && sessions.Count(s => s.Hidden) == 4,
        "nine host processes reconcile to the desktop's two visible and four hidden tabs");
    Require(sessions.Select(s => s.Id).SequenceEqual(["second", "desktop", "hidden-1", "hidden-2", "hidden-3", "hidden-4"]),
        "desktop order replaces phone order and stale cached/orphan sessions disappear");
    var desktop = sessions.Single(s => s.Id == "desktop");
    Require(desktop.Title == "PowerShell" && desktop.CustomTitle == "Deploy" && desktop.Active &&
        desktop.Buffer == "cached desktop" && desktop.Cwd == "/saved/work",
        "desktop titles and active state replace stale metadata while local buffer and cwd survive");
    Require(sessions.Single(s => s.Id == "second").CustomTitle is null &&
        sessions.Single(s => s.Id == "hidden-1").CustomTitle == "Archive",
        "desktop custom titles can be cleared and hidden tab titles stay intact");
    Require(init.GetProperty("liveIds").EnumerateArray().Select(v => v.GetString()!).ToHashSet().SetEquals(desktopIds),
        "hidden live tabs remain attachable and orphan processes are absent from mobile live ids");
    Require(ssh.TryGet("orphan-1") is null && backend.KillCount == 0,
        "removed catalog tabs dispose phone transports without killing desktop processes");
    Require(store.LoadArchivedSessions().Any(s => s.Id == "cache-only" && s.Buffer == "old output") &&
        store.LoadArchivedSessions().Any(s => s.Id == "orphan-1" && s.Buffer == "cached orphan-1"),
        "retired phone screens remain in a separate archive instead of the desktop session list");
    await bridge.Handle("""{"type":"archive-sessions","sessions":[{"id":"orphan-1","buffer":"latest visible screen"}]}""");
    await bridge.Handle("""{"type":"archive-sessions","sessions":[{"id":"orphan-1","buffer":""}]}""");
    Require(store.LoadArchivedSessions().Single(s => s.Id == "orphan-1").Buffer == "latest visible screen",
        "UI retirement preserves its last rendered screen and an empty archive update cannot erase it");
    var beforeStaleEvents = js.Messages.Count;
    RaiseTransportEvent(retired!, "RaiseData", "removed tab output");
    RaiseTransportEvent(retired!, "RaiseError", "removed tab error");
    RaiseTransportEvent(retired!, "RaiseClosed", null);
    Require(js.Messages.Count == beforeStaleEvents && ConnectionState(js) == "connected",
        "retired catalog observers cannot affect connection state or terminal output");
    await bridge.Handle("""{"type":"attach","id":"hidden-1"}""");
    await bridge.Handle("""{"type":"write","id":"hidden-1","data":"hidden connected"}""");
    await Until(() => backend.Writes.Contains("hidden connected"));
    Require(backend.CreateCount == 0, "attaching a hidden desktop tab mirrors its existing shell");

    var attachedHidden = ssh.TryGet("hidden-1");
    var attachesBeforePoll = backend.AttachCount;
    catalog[0].Title = "Renamed on desktop";
    catalog[1].Hidden = true;
    catalog[1].CustomTitle = "Hidden on desktop";
    catalog[1].SortOrder = 80;
    backend.SessionIds = backend.SessionIds.Where(id => id != "hidden-3").ToArray();
    await bridge.Handle("""{"type":"connection-check"}""");
    await Until(() => js.Messages.Any(m => m.GetProperty("type").GetString() == "mobile-session-catalog"));
    var refresh = js.Messages.Last(m => m.GetProperty("type").GetString() == "mobile-session-catalog");
    Require(refresh.GetProperty("sessionsAuthoritative").GetBoolean() &&
        store.LoadSessions().Single(s => s.Id == "desktop").Title == "Renamed on desktop" &&
        store.LoadSessions().Last().Id == "second" && store.LoadSessions().Last().Hidden,
        "connection checks synchronize desktop title, visibility and order without reopening the dialog");
    Require(!refresh.GetProperty("liveIds").EnumerateArray().Any(id => id.GetString() == "hidden-3") &&
        ReferenceEquals(attachedHidden, ssh.TryGet("hidden-1")) && backend.AttachCount == attachesBeforePoll && backend.KillCount == 0,
        "catalog refresh reflects live exits while retaining healthy phone transports and desktop processes");
    // Simulate the UI attaching each live view from the received catalog.
    foreach (var id in refresh.GetProperty("liveIds").EnumerateArray().Select(value => value.GetString()!))
    {
        await bridge.Handle(JsonSerializer.Serialize(new { type = "attach", id }));
        await bridge.Handle(JsonSerializer.Serialize(new { type = "write", id, data = "catalog attach " + id }));
    }
    await Task.Delay(20);
    var refreshCount = js.Messages.Count(m => m.GetProperty("type").GetString() == "mobile-session-catalog");
    var statesBeforePoll = js.Messages.Count(IsConnectionState);
    await bridge.Handle("""{"type":"connection-check"}""");
    await Until(() => js.Messages.Count(IsConnectionState) >= statesBeforePoll + 2);
    Require(js.Messages.Count(m => m.GetProperty("type").GetString() == "mobile-session-catalog") == refreshCount,
        "an unchanged desktop catalog does not replay or rebuild existing phone terminals");

    var healthyDesktop = ssh.TryGet("desktop");
    foreach (var failureEvent in new[] { "RaiseClosed", "RaiseError" })
    {
        var lostHidden = ssh.TryGet("hidden-1")!;
        RaiseTransportEvent(lostHidden, failureEvent, failureEvent == "RaiseError" ? "lost mirror" : null);
        Require(lostHidden.State == SshSessionState.Connected,
            $"{failureEvent} recovery test reproduces a failed observer before the transport state catches up");
        await Task.Delay(20);
        await bridge.Handle("""{"type":"connection-check"}""");
        await Until(() => js.Messages.Count(m => m.GetProperty("type").GetString() == "mobile-session-catalog") > refreshCount);
        var attachesBeforeRecovery = backend.AttachCount;
        await bridge.Handle("""{"type":"attach","id":"hidden-1"}""");
        var recoveryInput = "recovered same catalog " + failureEvent;
        await bridge.Handle(JsonSerializer.Serialize(new { type = "write", id = "hidden-1", data = recoveryInput }));
        await Until(() => backend.Writes.Contains(recoveryInput));
        Require(!ReferenceEquals(lostHidden, ssh.TryGet("hidden-1")) &&
            ReferenceEquals(healthyDesktop, ssh.TryGet("desktop")) && backend.AttachCount == attachesBeforeRecovery + 1 &&
            backend.CreateCount == 0 && backend.KillCount == 0,
            $"an unchanged catalog recovers only the lost mirror after {failureEvent}, even when its transport reports connected");
        await Task.Delay(20);
        refreshCount = js.Messages.Count(m => m.GetProperty("type").GetString() == "mobile-session-catalog");
        statesBeforePoll = js.Messages.Count(IsConnectionState);
        await bridge.Handle("""{"type":"connection-check"}""");
        await Until(() => js.Messages.Count(IsConnectionState) >= statesBeforePoll + 2);
        Require(js.Messages.Count(m => m.GetProperty("type").GetString() == "mobile-session-catalog") == refreshCount,
            $"a healthy mirror recovered after {failureEvent} does not trigger another catalog replay");
    }

    catalog = catalog.Append(new GatewaySessionInfo { Id = "ended", Title = "Saved empty tab", SortOrder = 70 }).ToArray();
    await bridge.SendInitAsync();
    init = js.Messages.Last(m => m.GetProperty("type").GetString() == "init");
    Require(store.LoadSessions().Count == 7 && store.LoadSessions().Single(s => s.Id == "ended").Buffer is null &&
        !init.GetProperty("liveIds").EnumerateArray().Any(v => v.GetString() == "ended"),
        "saved desktop tabs with empty buffers survive without being reported as live");
    Preferences.Default.Set("gatewayToken", "wrong");
    await bridge.SendInitAsync(reconnect: true);
    init = js.Messages.Last(m => m.GetProperty("type").GetString() == "init");
    Require(store.LoadSessions().Count == 7 && store.LoadSessions().Any(s => s.Id == "ended") && ConnectionState(js) == "disconnected",
        "failed catalog refresh preserves cached sessions including empty saved tabs");
    Require(!init.GetProperty("sessionsAuthoritative").GetBoolean(),
        "a failed connection never instructs the phone to discard its cached session views");
    Preferences.Default.Set("gatewayToken", "catalog-test");
    catalog = [];
    await bridge.SendInitAsync();
    init = js.Messages.Last(m => m.GetProperty("type").GetString() == "init");
    Require(store.LoadSessions().Count == 0 && init.GetProperty("liveIds").GetArrayLength() == 0 && backend.KillCount == 0,
        "an authoritative empty catalog clears phone ghosts despite remaining host processes");
}

static bool IsConnectionState(JsonElement message) => message.GetProperty("type").GetString() == "connection-state";
static string? ConnectionState(JsRuntime js) => js.Messages.Last(IsConnectionState).GetProperty("state").GetString();
static void RaiseTransportEvent(ISshSession session, string method, object? value) =>
    typeof(SshSessionBase).GetMethod(method, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
        .Invoke(session, [value]);

static void Require(bool condition, string message)
{
    if (!condition) throw new Exception(message);
    Console.WriteLine("PASS " + message);
}
static async Task Until(Func<bool> condition)
{
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
    while (!condition()) await Task.Delay(20, timeout.Token);
}
sealed class JsRuntime : IJSRuntime
{
    public ConcurrentQueue<JsonElement> Messages { get; } = new();
    public ValueTask<T> InvokeAsync<T>(string identifier, object?[]? args) => InvokeAsync<T>(identifier, default, args);
    public ValueTask<T> InvokeAsync<T>(string identifier, CancellationToken token, object?[]? args)
    {
        if (identifier != "__tvDispatch") throw new Exception(identifier);
        Messages.Enqueue(JsonSerializer.Deserialize<JsonElement>((string)args![0]!));
        return ValueTask.FromResult(default(T)!);
    }
}
sealed class Backend : IGatewaySessionBackend
{
    public int AttachCount;
    public int CreateCount;
    public int KillCount;
    public string[] SessionIds { get; set; } = ["desktop"];
    public ConcurrentBag<string> Writes { get; } = new();
    public event Action<string, string>? Data;
    public event Action<string, uint>? Exited;
    public event Action<string, string>? DirectoryChanged;
    public event Action<string, string>? Error;
    public string[] LiveIds() => SessionIds;
    public void Attach(string id) { Interlocked.Increment(ref AttachCount); Data?.Invoke(id, "snapshot"); }
    public void Emit(string text) => Data?.Invoke("desktop", text);
    public void Exit() => Exited?.Invoke("desktop", 0);
    public void Write(string id, string data) => Writes.Add(data);
    public void Resize(string id, int cols, int rows) { }
    public void Kill(string id) { Interlocked.Increment(ref KillCount); throw new Exception("must not kill desktop session"); }
    public void Create(string id, int cols, int rows, string? cwd, string? shell, string? startupCommand, string? wslDistribution) => CreateCount++;
}
