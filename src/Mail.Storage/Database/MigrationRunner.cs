using Microsoft.Data.Sqlite;

namespace Mail.Storage.Database;

/// <summary>
/// Versioned migrations tracked via PRAGMA user_version: script N in the list
/// takes the database from version N to N+1, each inside a transaction.
/// </summary>
public static class MigrationRunner
{
    public static void Apply(SqliteConnection conn, IReadOnlyList<string> migrations)
    {
        int current;
        using (var read = conn.CreateCommand())
        {
            read.CommandText = "PRAGMA user_version;";
            current = Convert.ToInt32(read.ExecuteScalar());
        }
        if (current > migrations.Count)
            throw new InvalidOperationException(
                $"Database is at schema version {current}, newer than this build understands ({migrations.Count}).");
        for (var v = current; v < migrations.Count; v++)
        {
            using var tx = conn.BeginTransaction();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = migrations[v] + $"\nPRAGMA user_version = {v + 1};";
            cmd.ExecuteNonQuery();
            tx.Commit();
        }
    }
}
