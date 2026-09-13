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
    });

    Check("hidden sessions and metadata survive disposal and reopen without becoming active", () =>
    {
        using (var db = new AppDatabase(path))
        {
            db.SaveSessions([
                new() { Id = "live", Title = "Muse", SortOrder = 1, Active = true, Cwd = @"C:\Проект" },
                new() { Id = "hidden", Title = "Фоновая сессия", CustomTitle = "<img> & 'имя'", SortOrder = 0,
                    Active = true, Hidden = true, Pinned = true, Color = "violet", Group = "Проект'; DROP TABLE sessions; --",
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
        db.SaveSessions([records[0]]);
        Equal(db.LoadSessions().Single().Id, "hidden");
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
}
finally
{
    SqliteConnection.ClearAllPools();
    // Only this test run's temporary databases, never the user's TerminalV data.
    Directory.Delete(root, recursive: true);
}
Console.WriteLine($"{passed} checks passed.");
