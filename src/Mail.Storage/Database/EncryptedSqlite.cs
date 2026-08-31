using Microsoft.Data.Sqlite;

namespace Mail.Storage.Database;

public sealed class WrongDatabaseKeyException(string path, Exception inner)
    : Exception($"'{path}' could not be opened with the supplied key.", inner);

/// <summary>
/// Opens SQLCipher-encrypted databases keyed with a raw 32-byte key
/// (PRAGMA key = "x'..'"), skipping SQLCipher's per-open PBKDF2 entirely.
/// </summary>
public static class EncryptedSqlite
{
    static EncryptedSqlite() => SQLitePCL.Batteries_V2.Init();

    public static SqliteConnection Open(string path, ReadOnlySpan<byte> key)
    {
        if (key.Length != 32)
            throw new ArgumentException("Expected a raw 32-byte key.", nameof(key));
        var conn = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = path,
            Mode = SqliteOpenMode.ReadWriteCreate,
            // Pooled handles would be reused without re-running PRAGMA key.
            Pooling = false,
        }.ConnectionString);
        conn.Open();
        try
        {
            Execute(conn, $"PRAGMA key = \"x'{Convert.ToHexString(key)}'\";");
            try
            {
                Execute(conn, "SELECT count(*) FROM sqlite_master;");
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 26 /* SQLITE_NOTADB */)
            {
                throw new WrongDatabaseKeyException(path, ex);
            }
            Execute(conn, "PRAGMA journal_mode=WAL; PRAGMA foreign_keys=ON;");
            return conn;
        }
        catch
        {
            conn.Dispose();
            throw;
        }
    }

    static void Execute(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }
}
