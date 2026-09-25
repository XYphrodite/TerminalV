using System.IO;
using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;
using TerminalV.Extensibility;

namespace TerminalV.Extensions;

internal sealed class ExtensionStorage : IExtensionStorage
{
    private readonly string _connectionString;
    private readonly string _scope;
    private readonly bool _secret;

    public ExtensionStorage(string databasePath, string scope, bool secret = false)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(databasePath))!);
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
        _scope = scope;
        _secret = secret;
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE IF NOT EXISTS extension_values (
                scope TEXT NOT NULL, secret INTEGER NOT NULL, key TEXT NOT NULL, value TEXT NOT NULL,
                PRIMARY KEY(scope, secret, key))
            """;
        command.ExecuteNonQuery();
    }

    public string? Get(string key)
    {
        ValidateKey(key);
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM extension_values WHERE scope=$scope AND secret=$secret AND key=$key";
        Bind(command, key);
        var value = command.ExecuteScalar() as string;
        if (value is null || !_secret) return value;
        return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(value),
            Encoding.UTF8.GetBytes(_scope), DataProtectionScope.CurrentUser));
    }

    public void Set(string key, string? value)
    {
        ValidateKey(key);
        if (value?.Length > 1024 * 1024) throw new ArgumentException("Storage values cannot exceed 1 MiB of characters.");
        if (_secret && value is not null)
            value = Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(value),
                Encoding.UTF8.GetBytes(_scope), DataProtectionScope.CurrentUser));
        using var connection = Open();
        using var command = connection.CreateCommand();
        command.CommandText = value is null
            ? "DELETE FROM extension_values WHERE scope=$scope AND secret=$secret AND key=$key"
            : """
                INSERT INTO extension_values(scope,secret,key,value) VALUES($scope,$secret,$key,$value)
                ON CONFLICT(scope,secret,key) DO UPDATE SET value=excluded.value
                """;
        Bind(command, key);
        if (value is not null) command.Parameters.AddWithValue("$value", value);
        command.ExecuteNonQuery();
    }

    private SqliteConnection Open()
    {
        var connection = new SqliteConnection(_connectionString);
        connection.Open();
        return connection;
    }

    private void Bind(SqliteCommand command, string key)
    {
        command.Parameters.AddWithValue("$scope", _scope);
        command.Parameters.AddWithValue("$secret", _secret ? 1 : 0);
        command.Parameters.AddWithValue("$key", key);
    }

    private static void ValidateKey(string key)
    {
        if (string.IsNullOrWhiteSpace(key) || key.Length > 200)
            throw new ArgumentException("Storage keys must contain 1–200 characters.");
    }
}
