using System.Runtime.InteropServices;
using TerminalV.Shell;

// Real Windows .lnk files, but only in this test's private directory. No GUI,
// process launches, real known-folder lookup, Shell notifications or user links.
var root = Directory.CreateTempSubdirectory("terminalv-shortcuts-test-").FullName;
var passed = 0;
var failed = 0;
async Task Check(string name, Func<Task> run)
{
    try { await run(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception error) { Console.Error.WriteLine($"FAIL {name}: {error}"); failed++; }
}
(string Exe, string Menu, string Desktop) Paths()
{
    var dir = Path.Combine(root, Guid.NewGuid().ToString("N"), "Папка ZIP & user's [files]");
    Directory.CreateDirectory(dir);
    var exe = Path.Combine(dir, "TerminalV.exe");
    File.WriteAllText(exe, "Test sentinel, never executed");
    return (exe, Path.Combine(dir, "redirected", "Пуск"), Path.Combine(dir, "OneDrive", "Рабочий стол"));
}
ShortcutService Service((string Exe, string Menu, string Desktop) p, Action<string, bool>? notify = null) =>
    new(p.Exe, folder => folder == Environment.SpecialFolder.Programs ? p.Menu :
        folder == Environment.SpecialFolder.DesktopDirectory ? p.Desktop : throw new Exception("Unexpected folder"),
        notify ?? ((_, _) => { }));
string LinkPath(string folder) => Path.Combine(folder, "TerminalV.lnk");
try
{
    foreach (var choice in new[] { (true, false), (false, true), (true, true) })
        await Check($"only selected destinations: Start={choice.Item1}, Desktop={choice.Item2}", async () =>
        {
            var p = Paths();
            var notices = new List<(string Path, bool Created)>();
            var results = await Service(p, (path, created) => notices.Add((path, created))).CreateAsync(choice.Item1, choice.Item2);
            Require(results.All(r => r.Error is null), string.Join("; ", results.Select(r => r.Error)));
            Require(File.Exists(LinkPath(p.Menu)) == choice.Item1 && File.Exists(LinkPath(p.Desktop)) == choice.Item2, "Wrong destinations");
            Require(notices.Count == results.Count && notices.All(n => n.Created), "Wrong create notifications");
            foreach (var result in results) WithLink(result.Path!, link =>
            {
                Require(link.TargetPath == p.Exe, "Target must be current ZIP EXE, with Unicode/spaces intact");
                Require(link.WorkingDirectory == Path.GetDirectoryName(p.Exe), "Wrong working directory");
                Require(link.IconLocation == p.Exe + ",0", "Icon must come from EXE");
                Require(link.Arguments == "", "Shortcut must not inject arguments or shell commands");
            });
        });
    await Check("no destinations and invalid executable cannot write shortcuts", async () =>
    {
        var p = Paths();
        await Refused(() => Service(p).CreateAsync(false, false));
        foreach (var exe in new[] { (string?)null, "TerminalV.exe", Path.Combine(root, "TerminalV.exe"), Environment.ProcessPath })
        {
            var service = new ShortcutService(exe, _ => throw new Exception("Must not resolve folders"), (_, _) => { });
            Require(!service.Supported, "Invalid executable accepted");
            await Refused(() => service.CreateAsync(true, true));
        }
        Require(!Directory.Exists(p.Menu) && !Directory.Exists(p.Desktop), "Unexpected write");
    });
    await Check("repeat refreshes icon, keeps customizations and creates no duplicate", async () =>
    {
        var p = Paths();
        await Service(p).CreateAsync(true, false);
        WithLink(LinkPath(p.Menu), link => { link.Description = "My description"; link.WorkingDirectory = root; link.IconLocation = p.Exe + ",1"; link.Save(); });
        var notices = new List<bool>();
        var result = await Service(p, (_, created) => notices.Add(created)).CreateAsync(true, false);
        Require(result.Single().Error is null && notices.SequenceEqual(new[] { false }), "Refresh failed");
        WithLink(LinkPath(p.Menu), link =>
        {
            Require(link.Description == "My description" && link.WorkingDirectory == root, "Customizations lost");
            Require(link.IconLocation == p.Exe + ",0", "Icon not refreshed");
        });
        Require(Directory.GetFiles(p.Menu).Length == 1 && !Directory.Exists(p.Desktop), "Duplicate or unselected shortcut created");
    });
    foreach (var customArguments in new[] { false, true })
        await Check($"conflicting shortcut preserved; independent destination still succeeds (arguments={customArguments})", async () =>
        {
            var p = Paths();
            await Service(p).CreateAsync(true, false);
            WithLink(LinkPath(p.Menu), link =>
            {
                if (customArguments) link.Arguments = "--help";
                else link.TargetPath = Path.Combine(root, "another.exe");
                link.Save();
            });
            var before = File.ReadAllBytes(LinkPath(p.Menu));
            var results = await Service(p).CreateAsync(true, true);
            Require(results[0].Error is not null && results[1].Error is null, "Partial result lost");
            Require(before.SequenceEqual(File.ReadAllBytes(LinkPath(p.Menu))), "Existing link changed");
            Require(Directory.GetFiles(p.Menu).Length == 1, "Temporary file leaked");
        });
    await Check("unavailable destination and directory collision are reported without removing anything", async () =>
    {
        var p = Paths();
        var occupied = Directory.CreateDirectory(LinkPath(p.Desktop)).FullName;
        File.WriteAllText(Path.Combine(occupied, "keep.txt"), "Keep");
        var service = new ShortcutService(p.Exe, folder => folder == Environment.SpecialFolder.Programs ? "" : p.Desktop, (_, _) => { });
        var results = await service.CreateAsync(true, true);
        Require(results.Count == 2 && results.All(r => r.Error is not null), "Missing destination errors");
        Require(File.ReadAllText(Path.Combine(occupied, "keep.txt")) == "Keep", "Collision was removed");
    });
    await Check("Shell notification failure does not misreport a saved shortcut", async () =>
    {
        var p = Paths();
        var results = await Service(p, (_, _) => throw new IOException("Test notification failure")).CreateAsync(true, false);
        Require(results.Single().Error is null && File.Exists(LinkPath(p.Menu)), "Saved link reported as failure");
    });
}
finally { Directory.Delete(root, recursive: true); } // Exact private directory created above.
Console.WriteLine($"Shortcut checks: {passed} passed, {failed} failed.");
return failed == 0 ? 0 : 1;

static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
static async Task Refused(Func<Task<IReadOnlyList<ShortcutResult>>> run)
{
    try { await run(); }
    catch (Exception error) when (error is ArgumentException or InvalidOperationException) { return; }
    throw new Exception("Request was not refused");
}
static void WithLink(string path, Action<dynamic> action)
{
    Exception? failure = null;
    var thread = new Thread(() =>
    {
        object? shell = null, link = null;
        try
        {
            shell = Activator.CreateInstance(Type.GetTypeFromProgID("WScript.Shell", true)!)!;
            link = ((dynamic)shell).CreateShortcut(path);
            action(link);
        }
        catch (Exception error) { failure = error; }
        finally
        {
            if (link is not null) Marshal.FinalReleaseComObject(link);
            if (shell is not null) Marshal.FinalReleaseComObject(shell);
        }
    });
    thread.SetApartmentState(ApartmentState.STA);
    thread.Start();
    thread.Join();
    if (failure is not null) throw failure;
}
