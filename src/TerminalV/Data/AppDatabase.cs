using System.IO;
using System.Text.Json;
using System.Linq;
using Microsoft.Data.Sqlite;

namespace TerminalV.Data;

internal sealed class AppDatabase : IDisposable
{
    private static readonly JsonSerializerOptions Json = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true
    };

    private readonly SqliteConnection _connection;

    public AppDatabase(string? databasePath = null)
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath ?? AppPaths.Database,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString());
        _connection.Open();
        using var pragma = _connection.CreateCommand();
        pragma.CommandText = "PRAGMA journal_mode=WAL;";
        pragma.ExecuteNonQuery();
        EnsureSchema();
    }

    public AppSettings LoadSettings()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = 'app'";
        var value = cmd.ExecuteScalar() as string;
        if (string.IsNullOrWhiteSpace(value))
        {
            // First launch or after reinstall without DB: detect OS language, then persist via SaveSettings on next UI persist.
            var fresh = new AppSettings { Language = DetectSystemLanguage() };
            return fresh;
        }

        try
        {
            var settings = JsonSerializer.Deserialize<AppSettings>(value, Json) ?? new AppSettings { Language = DetectSystemLanguage() };
            // Old DB without Language field -> migrate to system language instead of hardcoded ru.
            settings.Language = string.IsNullOrWhiteSpace(settings.Language) ? DetectSystemLanguage() : NormalizeLanguage(settings.Language);
            return settings;
        }
        catch (JsonException)
        {
            return new AppSettings { Language = DetectSystemLanguage() };
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        settings.Language = NormalizeLanguage(settings.Language);
        var json = JsonSerializer.Serialize(settings, Json);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings(key, value) VALUES('app', $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$value", json);
        cmd.ExecuteNonQuery();
    }

    public bool LoadWindowMaximized()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = 'window-maximized'";
        return bool.TryParse(cmd.ExecuteScalar() as string, out var maximized) && maximized;
    }

    public void SaveWindowMaximized(bool maximized)
    {
        // Native window state must survive replacement of the UI's app settings.
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings(key, value) VALUES('window-maximized', $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$value", maximized ? "true" : "false");
        cmd.ExecuteNonQuery();
    }

    public List<LaunchProfile> LoadProfiles()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = 'profiles'";
        var value = cmd.ExecuteScalar() as string;
        return value is null ? [] : JsonSerializer.Deserialize<List<LaunchProfile>>(value, Json) ?? [];
    }

    public void SaveProfiles(IReadOnlyList<LaunchProfile> profiles)
    {
        if (profiles.Count > 100 || profiles.Select(p => p.Id).Distinct().Count() != profiles.Count)
            throw new ArgumentException("Допустимо до 100 профилей с уникальными идентификаторами.");
        foreach (var profile in profiles) profile.Validate();
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings(key, value) VALUES('profiles', $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$value", JsonSerializer.Serialize(profiles, Json));
        cmd.ExecuteNonQuery();
    }

    public List<SessionRecord> LoadSessions()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, custom_title, sort_order, is_active, buffer, cwd,
                   group_name, color, is_pinned, is_hidden, is_muted, shell, startup_command, wsl_distribution
            FROM sessions
            ORDER BY sort_order, updated_at
            """;
        using var reader = cmd.ExecuteReader();
        var list = new List<SessionRecord>();
        while (reader.Read())
        {
            list.Add(new SessionRecord
            {
                Id = reader.GetString(0),
                Title = reader.GetString(1),
                CustomTitle = reader.IsDBNull(2) ? null : reader.GetString(2),
                SortOrder = reader.GetInt32(3),
                Active = reader.GetInt32(4) != 0,
                Buffer = reader.FieldCount > 5 && !reader.IsDBNull(5) ? reader.GetString(5) : null,
                Cwd = reader.IsDBNull(6) ? null : reader.GetString(6),
                Group = reader.IsDBNull(7) ? null : reader.GetString(7),
                Color = reader.IsDBNull(8) ? null : reader.GetString(8),
                Pinned = reader.GetInt32(9) != 0,
                Hidden = reader.GetInt32(10) != 0,
                Muted = reader.GetInt32(11) != 0,
                Shell = reader.IsDBNull(12) ? null : reader.GetString(12),
                StartupCommand = reader.IsDBNull(13) ? null : reader.GetString(13),
                WslDistribution = reader.IsDBNull(14) ? null : reader.GetString(14)
            });
        }

        return list;
    }

    public List<PaneLayout> LoadLayouts()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = "SELECT value FROM settings WHERE key = 'layouts'";
        var value = cmd.ExecuteScalar() as string;
        try { return value is null ? [] : JsonSerializer.Deserialize<List<PaneLayout>>(value, Json) ?? []; }
        catch (JsonException) { return []; } // Session records are still restored as independent panes.
    }

    public void SaveSessions(IReadOnlyList<SessionRecord> sessions, IReadOnlyList<PaneLayout>? layouts = null)
    {
        // Never wipe the entire inventory on an empty flush: upgrade/reload
        // may post an empty list before init has restored tabs (readyForPersist
        // race, WebView2 not ready, or JSON failure). An intentional "close all"
        // must be explicit, not an accidental empty commit over a populated DB.
        if (sessions.Count == 0)
        {
            using var count = _connection.CreateCommand();
            count.CommandText = "SELECT COUNT(*) FROM sessions";
            var existing = Convert.ToInt64(count.ExecuteScalar());
            if (existing > 0) return;
        }
        // Preserve every session regardless of Hidden – visible sessions must also
        // survive restart with buffer/cwd, not only hidden ones.
        var normalized = PaneLayout.Normalize(layouts ?? LoadLayouts(), sessions);
        using var tx = _connection.BeginTransaction();
        using (var clear = _connection.CreateCommand())
        {
            clear.Transaction = tx;
            clear.CommandText = "DELETE FROM sessions";
            clear.ExecuteNonQuery();
        }

        var now = DateTimeOffset.UtcNow.ToString("O");
        foreach (var session in sessions)
        {
            using var insert = _connection.CreateCommand();
            insert.Transaction = tx;
            insert.CommandText = """
                INSERT INTO sessions(id, title, custom_title, sort_order, is_active, buffer, cwd,
                                     group_name, color, is_pinned, is_hidden, is_muted, shell, startup_command, wsl_distribution, created_at, updated_at)
                VALUES ($id, $title, $custom, $sort, $active, $buffer, $cwd,
                        $group, $color, $pinned, $hidden, $muted, $shell, $command, $distribution, $now, $now)
                """;
            insert.Parameters.AddWithValue("$id", session.Id);
            insert.Parameters.AddWithValue("$title", session.Title);
            insert.Parameters.AddWithValue("$custom", (object?)session.CustomTitle ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sort", session.SortOrder);
            insert.Parameters.AddWithValue("$active", session.Active && !session.Hidden ? 1 : 0);
            insert.Parameters.AddWithValue("$buffer", (object?)session.Buffer ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cwd", (object?)session.Cwd ?? DBNull.Value);
            insert.Parameters.AddWithValue("$group", (object?)session.Group ?? DBNull.Value);
            insert.Parameters.AddWithValue("$color", (object?)session.Color ?? DBNull.Value);
            insert.Parameters.AddWithValue("$pinned", session.Pinned ? 1 : 0);
            insert.Parameters.AddWithValue("$hidden", session.Hidden ? 1 : 0);
            insert.Parameters.AddWithValue("$muted", session.Muted ? 1 : 0);
            insert.Parameters.AddWithValue("$shell", (object?)session.Shell ?? DBNull.Value);
            insert.Parameters.AddWithValue("$command", (object?)session.StartupCommand ?? DBNull.Value);
            insert.Parameters.AddWithValue("$distribution", (object?)session.WslDistribution ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
        }

        using (var saveLayout = _connection.CreateCommand())
        {
            saveLayout.Transaction = tx;
            saveLayout.CommandText = """
                INSERT INTO settings(key, value) VALUES('layouts', $value)
                ON CONFLICT(key) DO UPDATE SET value = excluded.value
                """;
            saveLayout.Parameters.AddWithValue("$value", JsonSerializer.Serialize(normalized, Json));
            saveLayout.ExecuteNonQuery();
        }
        tx.Commit();
    }

    public string ImportBackground(string sourcePath)
    {
        var ext = Path.GetExtension(sourcePath);
        if (string.IsNullOrWhiteSpace(ext) || ext.Length > 8)
        {
            ext = ".png";
        }

        var name = Guid.NewGuid().ToString("N") + ext.ToLowerInvariant();
        var dest = Path.Combine(AppPaths.Backgrounds, name);
        File.Copy(sourcePath, dest, overwrite: true);
        return "https://tvdata.local/backgrounds/" + name;
    }

    public void Dispose() => _connection.Dispose();

    private void EnsureSchema()
    {
        using (var settings = _connection.CreateCommand())
        {
            settings.CommandText = """
                CREATE TABLE IF NOT EXISTS settings (
                    key TEXT PRIMARY KEY,
                    value TEXT NOT NULL
                );
                """;
            settings.ExecuteNonQuery();
        }

        using var sessions = _connection.CreateCommand();
        sessions.CommandText = """
            CREATE TABLE IF NOT EXISTS sessions (
                id TEXT PRIMARY KEY,
                title TEXT NOT NULL,
                custom_title TEXT,
                sort_order INTEGER NOT NULL DEFAULT 0,
                is_active INTEGER NOT NULL DEFAULT 0,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            """;
        sessions.ExecuteNonQuery();
        TryAddColumn("ALTER TABLE sessions ADD COLUMN buffer TEXT;");
        TryAddColumn("ALTER TABLE sessions ADD COLUMN cwd TEXT;");
        // Check columns explicitly: do not silently treat a failed migration as success.
        using var columns = _connection.CreateCommand();
        columns.CommandText = "PRAGMA table_info(sessions)";
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using (var reader = columns.ExecuteReader())
        {
            while (reader.Read()) existing.Add(reader.GetString(1));
        }
        foreach (var (name, definition) in new[] {
            ("group_name", "TEXT"), ("color", "TEXT"),
            ("is_pinned", "INTEGER NOT NULL DEFAULT 0"), ("is_hidden", "INTEGER NOT NULL DEFAULT 0"),
            ("is_muted", "INTEGER NOT NULL DEFAULT 0"), ("shell", "TEXT"), ("startup_command", "TEXT"), ("wsl_distribution", "TEXT") })
        {
            if (existing.Contains(name)) continue;
            using var add = _connection.CreateCommand();
            add.CommandText = $"ALTER TABLE sessions ADD COLUMN {name} {definition}";
            add.ExecuteNonQuery();
        }
    }

    private static string DetectSystemLanguage()
    {
        try
        {
            var name = System.Globalization.CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return name == "en" ? "en" : "ru";
        }
        catch { return "ru"; }
    }

    private static string NormalizeLanguage(string? language) =>
        language == "en" ? "en" : "ru";

    private void TryAddColumn(string sql)
    {
        try
        {
            using var cmd = _connection.CreateCommand();
            cmd.CommandText = sql;
            cmd.ExecuteNonQuery();
        }
        catch (SqliteException)
        {
        }
    }
}
