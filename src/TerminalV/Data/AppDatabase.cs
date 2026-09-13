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
            return new AppSettings();
        }

        try
        {
            return JsonSerializer.Deserialize<AppSettings>(value, Json) ?? new AppSettings();
        }
        catch (JsonException)
        {
            return new AppSettings();
        }
    }

    public void SaveSettings(AppSettings settings)
    {
        var json = JsonSerializer.Serialize(settings, Json);
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings(key, value) VALUES('app', $value)
            ON CONFLICT(key) DO UPDATE SET value = excluded.value
            """;
        cmd.Parameters.AddWithValue("$value", json);
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
                   group_name, color, is_pinned, is_hidden, is_muted, shell, startup_command
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
                StartupCommand = reader.IsDBNull(13) ? null : reader.GetString(13)
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
                                     group_name, color, is_pinned, is_hidden, is_muted, shell, startup_command, created_at, updated_at)
                VALUES ($id, $title, $custom, $sort, $active, $buffer, $cwd,
                        $group, $color, $pinned, $hidden, $muted, $shell, $command, $now, $now)
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
            ("is_muted", "INTEGER NOT NULL DEFAULT 0"), ("shell", "TEXT"), ("startup_command", "TEXT") })
        {
            if (existing.Contains(name)) continue;
            using var add = _connection.CreateCommand();
            add.CommandText = $"ALTER TABLE sessions ADD COLUMN {name} {definition}";
            add.ExecuteNonQuery();
        }
    }

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
