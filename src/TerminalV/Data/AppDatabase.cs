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

    public AppDatabase()
    {
        _connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = AppPaths.Database,
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

    public List<SessionRecord> LoadSessions()
    {
        using var cmd = _connection.CreateCommand();
        cmd.CommandText = """
            SELECT id, title, custom_title, sort_order, is_active, buffer, cwd
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
                Cwd = reader.FieldCount > 6 && !reader.IsDBNull(6) ? reader.GetString(6) : null
            });
        }

        return list;
    }

    public void SaveSessions(IReadOnlyList<SessionRecord> sessions)
    {
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
                INSERT INTO sessions(id, title, custom_title, sort_order, is_active, buffer, cwd, created_at, updated_at)
                VALUES ($id, $title, $custom, $sort, $active, $buffer, $cwd, $now, $now)
                """;
            insert.Parameters.AddWithValue("$id", session.Id);
            insert.Parameters.AddWithValue("$title", session.Title);
            insert.Parameters.AddWithValue("$custom", (object?)session.CustomTitle ?? DBNull.Value);
            insert.Parameters.AddWithValue("$sort", session.SortOrder);
            insert.Parameters.AddWithValue("$active", session.Active ? 1 : 0);
            insert.Parameters.AddWithValue("$buffer", (object?)session.Buffer ?? DBNull.Value);
            insert.Parameters.AddWithValue("$cwd", (object?)session.Cwd ?? DBNull.Value);
            insert.Parameters.AddWithValue("$now", now);
            insert.ExecuteNonQuery();
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
