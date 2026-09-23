using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.RegularExpressions;
using SelfUpdateKit;
using TerminalV.Update;

if (args.Length != 3 || !ReleaseVersion.TryParse(args[1], out var version))
{
    Console.Error.WriteLine("Usage: TerminalV.Package.Tests <zip> <expected-version> <new-extraction-directory>");
    return 2;
}

var zip = Path.GetFullPath(args[0]);
var extracted = Path.GetFullPath(args[2]);
if (Path.Exists(extracted)) throw new IOException("Extraction directory must not already exist.");
var checksum = await File.ReadAllTextAsync(zip + ".sha256");
var actualHash = Hash(zip);
Require(ReleaseChecksum.Matches(ReleaseChecksum.Parse(checksum), actualHash), "Package checksum mismatch");
Console.WriteLine($"SHA256 {actualHash}");
ZipFile.ExtractToDirectory(zip, extracted);

// Never use the installed application, its database, the network or the production host pipe.
var root = Directory.CreateTempSubdirectory("terminalv-package-test-").FullName;
var passed = 0;
var failed = 0;
async Task Check(string name, Func<Task> test)
{
    try { await test(); Console.WriteLine($"PASS {name}"); passed++; }
    catch (Exception error) { Console.Error.WriteLine($"FAIL {name}: {error.Message}"); failed++; }
}
string NewInstall()
{
    var dir = Directory.CreateDirectory(Path.Combine(root, Guid.NewGuid().ToString("N"))).FullName;
    File.Copy(Path.Combine(extracted, "TerminalV.exe"), Path.Combine(dir, "TerminalV.exe"));
    File.WriteAllText(Path.Combine(dir, "TerminalV.com"), "old shim sentinel");
    Directory.CreateDirectory(Path.Combine(dir, "wwwroot"));
    File.WriteAllText(Path.Combine(dir, "wwwroot", "index.html"), "old UI sentinel");
    return Path.Combine(dir, "TerminalV.exe");
}
static ReleaseSourceOptions PackageOptions() => TerminalVUpdate.Options(TerminalVUpdate.Variant.Full);
SelfUpdateService Service(string exe, LocalReleaseSource source, IExecutableReplacer? replacer = null) =>
    new(exe, new ReleaseVersion(0, 0, 0), source, PackageOptions(), replacer: replacer);
void Unchanged(string exe)
{
    Require(Hash(exe) == Hash(Path.Combine(extracted, "TerminalV.exe")), "Installed EXE changed");
    var dir = Path.GetDirectoryName(exe)!;
    Require(File.ReadAllText(Path.Combine(dir, "wwwroot", "index.html")) == "old UI sentinel", "Installed UI changed");
    Require(!File.Exists(Path.Combine(dir, TerminalVUpdate.PendingMarker)), "Unexpected pending marker");
    Require(!Directory.EnumerateDirectories(dir, TerminalVUpdate.StagingPrefix + "*").Any(), "Failed staging was not cleaned");
}
string Variant(string name, Action<ZipArchive> edit)
{
    var path = Path.Combine(root, name + ".zip");
    File.Copy(zip, path);
    using var archive = ZipFile.Open(path, ZipArchiveMode.Update);
    edit(archive);
    return path;
}

try
{
    await Check("package layout, PE binaries, UI assets and no development files", () =>
    {
        foreach (var binary in new[] { "TerminalV.exe", "TerminalV.com" })
        {
            using var stream = File.OpenRead(Path.Combine(extracted, binary));
            Require(stream.ReadByte() == 'M' && stream.ReadByte() == 'Z', $"{binary} is not a PE binary");
        }
        var ui = Path.Combine(extracted, "wwwroot");
        var html = File.ReadAllText(Path.Combine(ui, "index.html"));
        var assets = Regex.Matches(html, "(?:src|href)=\"(\\.?/?assets/[^\"]+)\"")
            .Select(match => match.Groups[1].Value.TrimStart('.', '/')).ToArray();
        Require(assets.Any(path => path.EndsWith(".js")) && assets.Any(path => path.EndsWith(".css")), "Missing JS/CSS references");
        foreach (var asset in assets) Require(File.Exists(Path.Combine(ui, asset)), $"Missing {asset}");
        var forbidden = Directory.EnumerateFiles(extracted, "*", SearchOption.AllDirectories)
            .Where(path => new[] { ".pdb", ".xml", ".db", ".log", ".map" }.Contains(Path.GetExtension(path)));
        Require(!forbidden.Any(), "Development/user files found in package");
        Require(AppVersion.CanSelfUpdate(Path.Combine(extracted, "TerminalV.exe")), "Package not recognized as portable installation");
        return Task.CompletedTask;
    });
    foreach (var binary in new[] { "TerminalV.exe", "TerminalV.com" })
    {
        await Check($"{binary} --version", async () =>
        {
            var result = await Run(Path.Combine(extracted, binary), "--version");
            Require(result.Code == 0 && result.Out.Trim() == args[1] && result.Error.Length == 0, $"Unexpected version: {result}");
        });
        await Check($"{binary} --help", async () =>
        {
            var result = await Run(Path.Combine(extracted, binary), "--help");
            Require(result.Code == 0 && result.Out.Contains("TerminalV " + args[1]) && result.Out.Contains("Usage:"), $"Bad help: {result}");
        });
        await Check($"{binary} update --help (no network/update)", async () =>
        {
            var result = await Run(Path.Combine(extracted, binary), "update", "--help");
            Require(result.Code == 0 && result.Out.Contains("--check"), $"Bad update help: {result}");
        });
        await Check($"{binary} forwards unknown-command error and exit code", async () =>
        {
            var result = await Run(Path.Combine(extracted, binary), "--package-test-unknown");
            Require(result.Code == 1 && result.Error.Contains("Unknown command"), $"Bad error forwarding: {result}");
        });
    }
    await Check("update check and already-current do not download", async () =>
    {
        var exe = NewInstall();
        var source = new LocalReleaseSource(zip, version, checksum);
        Require((await Service(exe, source).CheckAsync(default)).Status == SelfUpdateStatus.UpdateAvailable, "No update offered");
        var current = new SelfUpdateService(exe, version, source);
        Require((await current.UpdateAsync(new SelfUpdateRequest(), default)).Status == SelfUpdateStatus.AlreadyCurrent, "Current version reinstalled");
        Require(source.Downloads == 0, "Unexpected download");
        Unchanged(exe);
    });
    await Check("wrong checksum refuses replacement and removes temporary files", async () =>
    {
        var exe = NewInstall();
        await Refused(() => Service(exe, new(zip, version, new string('0', 64))).UpdateAsync(new SelfUpdateRequest(), default));
        Unchanged(exe);
    });
    foreach (var missing in new[] { "TerminalV.exe", "wwwroot/index.html", "TerminalV.com" })
    {
        await Check($"incomplete package without {missing} refuses replacement", async () =>
        {
            var variant = Variant(Guid.NewGuid().ToString("N"), archive =>
            {
                var entry = archive.Entries.Single(item => item.FullName.Replace('\\', '/') == missing);
                entry.Delete();
            });
            var exe = NewInstall();
            await Refused(() => Service(exe, new(variant, version, Hash(variant))).UpdateAsync(new SelfUpdateRequest(), default));
            Unchanged(exe);
        });
    }
    await Check("post-replacement failure restores previous executable and UI", async () =>
    {
        var exe = NewInstall();
        var replacer = new BrokenReplacement();
        var rejected = false;
        try { await Service(exe, new(zip, version, checksum), replacer).UpdateAsync(new SelfUpdateRequest(), default); }
        catch (Exception) when (replacer.Restored) { rejected = true; }
        Require(rejected && replacer.Restored, "Failed executable was not rolled back");
        Unchanged(exe);
    });
    await Check("marker write failure rolls back the installed executable", async () =>
    {
        var exe = NewInstall();
        var replacer = new BrokenReplacement(blockMarker: true);
        var rejected = false;
        try { await Service(exe, new(zip, version, checksum), replacer).UpdateAsync(new SelfUpdateRequest(), default); }
        catch (IOException) when (replacer.Restored) { rejected = true; }
        Require(rejected && replacer.Restored, "Marker failure did not roll back");
        Unchanged(exe);
    });
    await Check("local update replaces EXE, defers UI, then applies UI and shim", async () =>
    {
        var exe = NewInstall();
        var dir = Path.GetDirectoryName(exe)!;
        var result = await Service(exe, new(zip, version, checksum)).UpdateAsync(new SelfUpdateRequest(), default);
        Require(result.Status == SelfUpdateStatus.Updated, "Update not installed");
        Require(File.ReadAllText(Path.Combine(dir, "wwwroot", "index.html")) == "old UI sentinel", "UI replaced before restart");
        Require(File.Exists(Path.Combine(dir, TerminalVUpdate.PendingMarker)), "No pending update");
        PendingUpdateApplier.Apply(exe, PackageOptions());
        Require(Hash(exe) == Hash(Path.Combine(extracted, "TerminalV.exe")), "EXE mismatch");
        Require(Hash(Path.Combine(dir, "TerminalV.com")) == Hash(Path.Combine(extracted, "TerminalV.com")), "Console shim was not updated");
        foreach (var file in Directory.EnumerateFiles(Path.Combine(extracted, "wwwroot"), "*", SearchOption.AllDirectories))
            Require(Hash(Path.Combine(dir, Path.GetRelativePath(extracted, file))) == Hash(file), "Installed UI differs from package");
        Require(!File.Exists(Path.Combine(dir, TerminalVUpdate.PendingMarker)), "Marker not removed");
        Require(!Directory.EnumerateDirectories(dir, TerminalVUpdate.StagingPrefix + "*").Any(), "Staging not removed");
        PendingUpdateApplier.Apply(exe, PackageOptions()); // Restarting twice must be harmless.
    });
    await Check("locked staged shim preserves old UI and allows retry", async () =>
    {
        var exe = NewInstall();
        var dir = Path.GetDirectoryName(exe)!;
        await Service(exe, new(zip, version, checksum)).UpdateAsync(new SelfUpdateRequest(), default);
        var marker = Path.Combine(dir, TerminalVUpdate.PendingMarker);
        var payload = File.ReadAllText(marker);
        using (File.Open(Path.Combine(payload, "TerminalV.com"), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            PendingUpdateApplier.Apply(exe, PackageOptions());
            Require(File.Exists(marker), "Retry marker lost");
            Require(File.Exists(Path.Combine(payload, "wwwroot", "index.html")), "Staged UI not restored");
            Require(File.ReadAllText(Path.Combine(dir, "wwwroot", "index.html")) == "old UI sentinel", "Old UI not restored");
            Require(File.ReadAllText(Path.Combine(dir, "TerminalV.com")) == "old shim sentinel", "Old shim not restored");
        }
        PendingUpdateApplier.Apply(exe, PackageOptions());
        Require(!File.Exists(marker), "Retry did not complete");
        Require(Hash(Path.Combine(dir, "TerminalV.com")) == Hash(Path.Combine(extracted, "TerminalV.com")), "Retried shim mismatch");
    });
    await Check("second update preserves a pending payload", async () =>
    {
        var exe = NewInstall();
        var source = new LocalReleaseSource(zip, version, checksum);
        await Service(exe, source).UpdateAsync(new SelfUpdateRequest(), default);
        var marker = Path.Combine(Path.GetDirectoryName(exe)!, TerminalVUpdate.PendingMarker);
        var payload = File.ReadAllText(marker);
        var refused = false;
        try { await Service(exe, source).UpdateAsync(new SelfUpdateRequest(), default); }
        catch (InvalidOperationException) { refused = true; }
        Require(refused && source.Downloads == 1, "Downloaded a second update while one was pending");
        Require(File.ReadAllText(marker) == payload && Directory.Exists(payload), "Previous pending update damaged");
    });
    await Check("invalid pending marker cannot move/delete a sibling directory", () =>
    {
        var exe = NewInstall();
        // Both directories are disposable children of this test's unique temporary root.
        var sibling = Directory.CreateDirectory(Path.Combine(root, "unrelated")).FullName;
        var payload = Directory.CreateDirectory(Path.Combine(sibling, "payload")).FullName;
        Directory.CreateDirectory(Path.Combine(payload, "wwwroot"));
        File.WriteAllText(Path.Combine(payload, "wwwroot", "index.html"), "unrelated UI sentinel");
        File.WriteAllText(Path.Combine(sibling, "keep.txt"), "keep");
        File.WriteAllText(Path.Combine(Path.GetDirectoryName(exe)!, TerminalVUpdate.PendingMarker), payload);
        PendingUpdateApplier.Apply(exe, PackageOptions());
        Require(File.Exists(Path.Combine(sibling, "keep.txt")), "Pending marker deleted unrelated data");
        Require(File.Exists(Path.Combine(payload, "wwwroot", "index.html")), "Pending marker moved unrelated UI");
        Require(File.ReadAllText(Path.Combine(Path.GetDirectoryName(exe)!, "wwwroot", "index.html")) == "old UI sentinel", "Invalid marker changed installed UI");
        return Task.CompletedTask;
    });
    await Check("malformed marker and unrelated staging-like names are harmless", () =>
    {
        var exe = NewInstall();
        var dir = Path.GetDirectoryName(exe)!;
        var unrelated = Directory.CreateDirectory(Path.Combine(dir, TerminalVUpdate.StagingPrefix + "notes")).FullName;
        File.WriteAllText(Path.Combine(unrelated, "keep.txt"), "keep");
        var backup = Directory.CreateDirectory(Path.Combine(dir, "wwwroot.old-personal-backup")).FullName;
        File.WriteAllText(Path.Combine(backup, "keep.txt"), "keep");
        foreach (var value in new[] { "", "relative/payload", dir, unrelated, "bad\0path" })
        {
            File.WriteAllText(Path.Combine(dir, TerminalVUpdate.PendingMarker), value);
            PendingUpdateApplier.Apply(exe, PackageOptions());
            Require(File.Exists(exe), "Malformed marker removed installed files");
            Require(!File.Exists(Path.Combine(dir, TerminalVUpdate.PendingMarker)), "Invalid marker not discarded");
        }
        PendingUpdateApplier.Apply(exe, PackageOptions());
        Require(File.Exists(Path.Combine(unrelated, "keep.txt")) && File.Exists(Path.Combine(backup, "keep.txt")), "Cleanup removed unrelated files");
        return Task.CompletedTask;
    });
}
finally
{
    // This exact directory was created above, never inferred from application state or a marker.
    Directory.Delete(root, recursive: true);
}
Console.WriteLine($"Package checks: {passed} passed, {failed} failed. Extracted payload retained: {extracted}");
return failed == 0 ? 0 : 1;

static void Require(bool condition, string message)
{
    if (!condition) throw new InvalidOperationException(message);
}
static string Hash(string path)
{
    using var stream = File.OpenRead(path);
    return Convert.ToHexStringLower(SHA256.HashData(stream));
}
static async Task Refused(Func<Task<SelfUpdateReport>> operation)
{
    try { await operation(); }
    catch (InvalidDataException) { return; }
    throw new InvalidOperationException("Unsafe package was accepted");
}
static async Task<(int Code, string Out, string Error)> Run(string executable, params string[] arguments)
{
    var start = new ProcessStartInfo(executable)
    {
        UseShellExecute = false, CreateNoWindow = true,
        RedirectStandardOutput = true, RedirectStandardError = true,
        WorkingDirectory = Path.GetDirectoryName(executable)!
    };
    foreach (var argument in arguments) start.ArgumentList.Add(argument);
    using var process = Process.Start(start) ?? throw new InvalidOperationException("Process did not start");
    var stdout = process.StandardOutput.ReadToEndAsync();
    var stderr = process.StandardError.ReadToEndAsync();
    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
    try { await process.WaitForExitAsync(timeout.Token); }
    catch (OperationCanceledException)
    {
        process.Kill(entireProcessTree: true);
        await process.WaitForExitAsync();
        throw new TimeoutException("CLI probe timed out");
    }
    return (process.ExitCode, await stdout, await stderr);
}

sealed class LocalReleaseSource(string zip, ReleaseVersion version, string checksum) : IReleaseSource
{
    public int Downloads { get; private set; }
    public Task<ReleaseDescriptor> ResolveAsync(string? tag, CancellationToken cancellationToken) =>
        Task.FromResult(new ReleaseDescriptor($"v{version}", version, new Uri(zip), new Uri(zip + ".sha256"), 0, null, null));
    public Task DownloadAsync(Uri address, string destinationPath, CancellationToken cancellationToken, Action<long, long?>? progress = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        File.Copy(zip, destinationPath);
        Downloads++;
        return Task.CompletedTask;
    }
    public Task<string> ReadTextAsync(Uri address, CancellationToken cancellationToken) => Task.FromResult(checksum);
}

sealed class BrokenReplacement(bool blockMarker = false) : IExecutableReplacer
{
    private readonly ExecutableReplacer inner = new();
    public bool Restored { get; private set; }
    public string Replace(string currentPath, string stagedPath)
    {
        var retired = inner.Replace(currentPath, stagedPath);
        if (blockMarker)
            Directory.CreateDirectory(Path.Combine(Path.GetDirectoryName(currentPath)!, TerminalVUpdate.PendingMarker));
        else
            File.WriteAllText(currentPath, "not an executable");
        return retired;
    }
    public void Restore(string retiredPath, string currentPath)
    {
        inner.Restore(retiredPath, currentPath);
        Restored = true;
    }
    public int RemoveRetiredCopies(string currentPath) => inner.RemoveRetiredCopies(currentPath);
}
