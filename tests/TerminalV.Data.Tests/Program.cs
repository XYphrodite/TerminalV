using Microsoft.Data.Sqlite;
using TerminalV.Data;

var root = Directory.CreateTempSubdirectory("terminalv-data-test-").FullName;
var passed = 0;
void Check(string name, Action test)
{
    test();
    Console.WriteLine($"PASS {name}");
    passed++;
}
void Equal<T>(T actual, T expected)
{
    if (!EqualityComparer<T>.Default.Equals(actual, expected))
        throw new Exception($"Expected {expected}, got {actual}");
}

try
{
    Check("only one desktop can own a data directory", () =>
    {
        var dataDirectory = Directory.CreateDirectory(Path.Combine(root, "desktop-exclusive")).FullName;
        using var first = DesktopInstanceLease.TryAcquire(dataDirectory);
        Equal(first is not null, true);
        using var second = DesktopInstanceLease.TryAcquire(dataDirectory);
        Equal(second is null, true);
    });

    Check("closing a desktop releases ownership even when its lock file remains", () =>
    {
        var dataDirectory = Directory.CreateDirectory(Path.Combine(root, "desktop-reopen")).FullName;
        using (var first = DesktopInstanceLease.TryAcquire(dataDirectory))
        {
            Equal(first is not null, true);
        }
        Equal(File.Exists(Path.Combine(dataDirectory, "desktop.lock")), true);
        using var reopened = DesktopInstanceLease.TryAcquire(dataDirectory);
        Equal(reopened is not null, true);
    });

    Check("independent data directories can have separate desktops", () =>
    {
        var firstDirectory = Directory.CreateDirectory(Path.Combine(root, "desktop-first")).FullName;
        var secondDirectory = Directory.CreateDirectory(Path.Combine(root, "desktop-second")).FullName;
        using var first = DesktopInstanceLease.TryAcquire(firstDirectory);
        using var second = DesktopInstanceLease.TryAcquire(secondDirectory);
        Equal(first is not null, true);
        Equal(second is not null, true);
    });

    Check("desktop startup I/O failures are not reported as another open window", () =>
    {
        try
        {
            using var lease = DesktopInstanceLease.TryAcquire(Path.Combine(root, "missing-directory"));
            throw new Exception("Expected a missing data directory error");
        }
        catch (DirectoryNotFoundException)
        {
        }
    });

    var path = Path.Combine(root, "legacy.db");
    using (var legacy = new SqliteConnection($"Data Source={path}"))
    {
        legacy.Open();
        using var create = legacy.CreateCommand();
        create.CommandText = """
            CREATE TABLE sessions (
                id TEXT PRIMARY KEY, title TEXT NOT NULL, custom_title TEXT,
                sort_order INTEGER NOT NULL DEFAULT 0, is_active INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL, updated_at TEXT NOT NULL, buffer TEXT, cwd TEXT);
            INSERT INTO sessions VALUES ('old', 'PowerShell', 'Мой проект', 0, 1, 'now', 'now', 'Сохранённый вывод', 'C:\Проект');
            """;
        create.ExecuteNonQuery();
    }

    Check("existing database migration preserves titles, screen, cwd and active session", () =>
    {
        using var db = new AppDatabase(path);
        var old = db.LoadSessions().Single();
        Equal(old.CustomTitle, "Мой проект");
        Equal(old.Buffer, "Сохранённый вывод");
        Equal(old.Cwd, @"C:\Проект");
        Equal(old.Active, true);
        Equal(old.Hidden, false);
        Equal(old.Pinned, false);
        Equal(old.Group, null);
        Equal(old.Color, null);
        Equal(old.Muted, false);
        Equal(old.Shell, null);
        Equal(old.StartupCommand, null);
        Equal(old.WslDistribution, null);
    });

    Check("hidden sessions and metadata survive disposal and reopen without becoming active", () =>
    {
        using (var db = new AppDatabase(path))
        {
            db.SaveSessions([
                new() { Id = "live", Title = "Muse", SortOrder = 1, Active = true, Cwd = @"C:\Проект" },
                new() { Id = "hidden", Title = "Фоновая сессия", CustomTitle = "<img> & 'имя'", SortOrder = 0,
                    Active = true, Hidden = true, Pinned = true, Muted = true, Color = "violet", Group = "Проект'; DROP TABLE sessions; --",
                    Buffer = "\u001b[?1049hСохранённый TUI", Cwd = @"D:\Моя папка" }
            ]);
        }
        using var reopened = new AppDatabase(path);
        var records = reopened.LoadSessions();
        Equal(records.Count, 2);
        var hidden = records[0];
        Equal(hidden.Id, "hidden");
        Equal(hidden.Hidden, true);
        Equal(hidden.Pinned, true);
        Equal(hidden.Muted, true);
        Equal(hidden.Active, false);
        Equal(hidden.Color, "violet");
        Equal(hidden.Group, "Проект'; DROP TABLE sessions; --");
        Equal(hidden.Buffer, "\u001b[?1049hСохранённый TUI");
        Equal(hidden.Cwd, @"D:\Моя папка");
        Equal(hidden.CustomTitle, "<img> & 'имя'");
        Equal(records[1].Active, true);
    });

    Check("failed save rolls back instead of losing the existing session inventory", () =>
    {
        using var db = new AppDatabase(path);
        try
        {
            db.SaveSessions([new() { Id = "duplicate" }, new() { Id = "duplicate" }]);
            throw new Exception("Duplicate primary key was accepted");
        }
        catch (SqliteException) { }
        Equal(db.LoadSessions().Count, 2);
        Equal(db.LoadSessions()[0].Id, "hidden");
    });

    Check("unhide and remove update only the explicitly persisted inventory", () =>
    {
        using var db = new AppDatabase(path);
        var records = db.LoadSessions();
        records[0].Hidden = false;
        records[0].Active = true;
        records[1].Active = false;
        db.SaveSessions(records);
        Equal(db.LoadSessions()[0].Hidden, false);
        Equal(db.LoadSessions()[0].Pinned, true);
        Equal(db.LoadSessions()[0].Muted, true);
        db.SaveSessions([records[0]]);
        Equal(db.LoadSessions().Single().Id, "hidden");
    });

    Check("profiles survive reopening independently of appearance and session launch snapshots", () =>
    {
        using (var db = new AppDatabase(path))
        {
            Equal(db.LoadProfiles().Count, 0);
            db.SaveSettings(new() { ThemeId = "profile-test" });
            db.SaveProfiles([new() { Id = "project", Title = "Проект <&>", Shell = "powershell", Color = "green",
                Cwd = @"C:\Папка O'Brien", StartupCommand = "Write-Output 'first'\r\nWrite-Output 'вторая'" }]);
            db.SaveSessions([new() { Id = "snapshot", Shell = "powershell", StartupCommand = "original" }]);
        }
        using var reopened = new AppDatabase(path);
        var profile = reopened.LoadProfiles().Single();
        Equal(profile.Title, "Проект <&>");
        Equal(profile.Cwd, @"C:\Папка O'Brien");
        Equal(profile.StartupCommand, "Write-Output 'first'\r\nWrite-Output 'вторая'");
        Equal(profile.Color, "green");
        Equal(reopened.LoadSettings().ThemeId, "profile-test");
        profile.Shell = "cmd";
        profile.StartupCommand = "echo changed";
        reopened.SaveProfiles([profile]);
        Equal(reopened.LoadSessions().Single().StartupCommand, "original");
        reopened.SaveProfiles([]);
        Equal(reopened.LoadProfiles().Count, 0);
        Equal(reopened.LoadSessions().Single().Shell, "powershell");
    });

    Check("invalid profile saves preserve all previously saved data", () =>
    {
        using var db = new AppDatabase(path);
        db.SaveProfiles([new() { Id = "saved", Title = "Saved" }]);
        foreach (var invalid in new List<LaunchProfile>[] {
            [new() { Id = "same", Title = "A" }, new() { Id = "same", Title = "B" }],
            [new() { Id = "a", Title = "" }], [new() { Id = "a", Title = "A", Shell = "unknown" }],
            [new() { Id = "a", Title = "A", Shell = "cmd", StartupCommand = "a\nb" }],
            [new() { Id = "a", Title = "A", StartupCommand = new string('x', 4097) }]
        })
        {
            try { db.SaveProfiles(invalid); throw new Exception("Invalid profile was accepted"); }
            catch (ArgumentException) { }
            Equal(db.LoadProfiles().Single().Id, "saved");
        }
    });

    Check("nested layouts and ratios survive reopening with the same session identities", () =>
    {
        var splitPath = Path.Combine(root, "split.db");
        using (var db = new AppDatabase(splitPath))
        {
            Equal(db.LoadLayouts().Count, 0);
            db.SaveSessions([new() { Id = "a", Active = true }, new() { Id = "b", Shell = "powershell", StartupCommand = "original" },
                new() { Id = "c", Hidden = true }], [new() { Axis = "columns", Ratio = .65,
                    First = new() { SessionId = "a" }, Second = new() { SessionId = "b" } }]);
        }
        using var reopened = new AppDatabase(splitPath);
        Equal(reopened.LoadLayouts().Single().Ratio, .65);
        Equal(reopened.LoadLayouts().Single().Second!.SessionId, "b");
        Equal(reopened.LoadSessions()[0].Active, true);
        Equal(reopened.LoadSessions()[1].StartupCommand, "original");
        // A metadata-only writer must preserve existing layouts; hiding prunes only that leaf.
        var records = reopened.LoadSessions();
        reopened.SaveSessions(records);
        Equal(reopened.LoadLayouts().Single().Ratio, .65);
        records[0].Hidden = true;
        reopened.SaveSessions(records);
        Equal(reopened.LoadLayouts().Single().SessionId, "b");
    });

    Check("failed inventory writes roll back both layouts and sessions", () =>
    {
        using var db = new AppDatabase(Path.Combine(root, "atomic.db"));
        db.SaveSessions([new() { Id = "kept" }], [new() { SessionId = "kept" }]);
        try
        {
            db.SaveSessions([new() { Id = "duplicate" }, new() { Id = "duplicate" }], [new() { SessionId = "duplicate" }]);
            throw new Exception("Duplicate accepted");
        }
        catch (SqliteException) { }
        Equal(db.LoadSessions().Single().Id, "kept");
        Equal(db.LoadLayouts().Single().SessionId, "kept");
    });

    Check("layout normalization recovers malformed references without dropping sessions", () =>
    {
        var sessions = Enumerable.Range(0, 20).Select(i => new SessionRecord { Id = i.ToString() }).ToList();
        var tree = new PaneLayout { SessionId = "0" };
        for (var i = 1; i < 20; i++) tree = new() { Axis = "rows", Ratio = 5, First = tree, Second = new() { SessionId = i.ToString() } };
        IEnumerable<string> Leaves(PaneLayout node) => node.SessionId is { } id ? [id]
            : Leaves(node.First!).Concat(Leaves(node.Second!));
        var cleaned = PaneLayout.Normalize([tree, new() { Axis = "bad" }], sessions);
        Equal(cleaned.SelectMany(Leaves).Distinct().Count(), 20);
        Equal(cleaned.All(node => Leaves(node).Count() <= 8), true);
        var pair = PaneLayout.Normalize([new() { Axis = "columns", Ratio = 10,
            First = new() { SessionId = "0" }, Second = new() { SessionId = "1" } }], sessions);
        Equal(pair[0].Ratio, .9);
    });

    Check("WSL distribution survives reopening without changing profiles or other session metadata", () =>
    {
        var wslPath = Path.Combine(root, "wsl.db");
        using (var db = new AppDatabase(wslPath))
        {
            db.SaveProfiles([new() { Id = "saved", Title = "Windows profile", Shell = "powershell" }]);
            db.SaveSessions([new() { Id = "linux", Title = "Ubuntu", Shell = "wsl", WslDistribution = "Ubuntu Dev's", Buffer = "Linux screen", Hidden = true }]);
        }
        using var reopened = new AppDatabase(wslPath);
        var session = reopened.LoadSessions().Single();
        Equal(session.Shell, "wsl");
        Equal(session.WslDistribution, "Ubuntu Dev's");
        Equal(session.Buffer, "Linux screen");
        Equal(session.Cwd, null);
        Equal(session.Hidden, true);
        Equal(reopened.LoadProfiles().Single().Shell, "powershell");
        reopened.SaveSessions([session]);
        Equal(reopened.LoadSessions().Single().WslDistribution, "Ubuntu Dev's");
    });

    Check("fresh database has the same schema and reopening migration is idempotent", () =>
    {
        var freshPath = Path.Combine(root, "fresh.db");
        using (var db = new AppDatabase(freshPath))
        {
            db.SaveSettings(new() { ThemeId = "test", FontSize = 18 });
            db.SaveSessions([new() { Id = "new", Group = "Группа", Hidden = true }]);
        }
        using var reopened = new AppDatabase(freshPath);
        Equal(reopened.LoadSessions().Single().Group, "Группа");
        Equal(reopened.LoadSettings().FontSize, 18);
    });

    Check("visible sessions preserve buffer and metadata without requiring hidden", () =>
    {
        var visiblePath = Path.Combine(root, "visible.db");
        using (var db = new AppDatabase(visiblePath))
        {
            db.SaveSessions([
                new() { Id = "visible", Title = "Чат", Buffer = "привет чат история\nвторая строка \u001b[34m", Cwd = @"C:\Проект", SortOrder = 0, Active = true, Hidden = false, Group = "Проект", Color = "green", Pinned = true },
                new() { Id = "hidden", Title = "HiddenChat", Buffer = "hidden buffer \u001b[?1049h", Cwd = @"D:\Папка", SortOrder = 1, Active = false, Hidden = true }
            ]);
        }
        using var reopened = new AppDatabase(visiblePath);
        var records = reopened.LoadSessions();
        Equal(records.Count, 2);
        var visible = records.First(r => r.Id == "visible");
        Equal(visible.Buffer, "привет чат история\nвторая строка \u001b[34m");
        Equal(visible.Cwd, @"C:\Проект");
        Equal(visible.Active, true);
        Equal(visible.Hidden, false);
        Equal(visible.Group, "Проект");
        Equal(visible.Color, "green");
        Equal(visible.Pinned, true);
        var hidden = records.First(r => r.Id == "hidden");
        Equal(hidden.Buffer, "hidden buffer \u001b[?1049h");
        Equal(hidden.Hidden, true);
        // Layout must keep visible session, hidden must not appear in layout
        using (var db = new AppDatabase(visiblePath))
        {
            db.SaveSessions(records, [new() { SessionId = "visible" }, new() { SessionId = "hidden" }]);
        }
        using var relayout = new AppDatabase(visiblePath);
        var layouts = relayout.LoadLayouts();
        Equal(layouts.Count, 1);
        Equal(layouts[0].SessionId, "visible");
        Equal(relayout.LoadSessions().First(r => r.Id == "visible").Buffer, "привет чат история\nвторая строка \u001b[34m");
    });
}
finally
{
    SqliteConnection.ClearAllPools();
    // Only this test run's temporary databases, never the user's TerminalV data.
    Directory.Delete(root, recursive: true);
}
Console.WriteLine($"{passed} checks passed.");
