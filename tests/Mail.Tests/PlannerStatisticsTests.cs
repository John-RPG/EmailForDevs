using System.Security.Cryptography;
using Mail.Storage.Database;
using Microsoft.Data.Sqlite;

namespace Mail.Tests;

/// <summary>
/// FTS5 deletes merged segments by rowid range. If the planner believes its
/// data table is tiny it scans the table instead, and on a large mailbox every
/// merge then costs seconds - which is what pinned a core during sync. These
/// pin the plan, not the timing, so they hold on any machine.
/// </summary>
public sealed class PlannerStatisticsTests : IDisposable
{
    readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));

    public PlannerStatisticsTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    const string SegmentDelete = "DELETE FROM messages_fts_data WHERE id>=1 AND id<=5;";

    static string Plan(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "EXPLAIN QUERY PLAN " + sql;
        using var reader = cmd.ExecuteReader();
        var steps = new List<string>();
        while (reader.Read()) steps.Add(reader.GetString(3));
        return string.Join(" | ", steps);
    }

    static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    [Fact]
    public void A_new_mailbox_seeks_full_text_segments_by_rowid()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var db = MailboxDatabase.Open(Path.Combine(_dir, "new.db"), key);

        Assert.Contains("USING INTEGER PRIMARY KEY", Plan(db, SegmentDelete));
    }

    [Fact]
    public void Statistics_recorded_on_an_empty_mailbox_are_removed_and_not_used()
    {
        // Recreate what V4's ANALYZE left on mailboxes created after it was
        // added: the FTS data table recorded as holding 2 rows.
        var path = Path.Combine(_dir, "poisoned.db");
        var key = RandomNumberGenerator.GetBytes(32);
        using (var db = MailboxDatabase.Open(path, key))
        {
            Exec(db, "ANALYZE sqlite_schema;");
            Exec(db, "DELETE FROM sqlite_stat1;");
            Exec(db, "INSERT INTO sqlite_stat1(tbl, idx, stat) VALUES('messages_fts_data', NULL, '2');");
            Exec(db, "PRAGMA user_version = 5;");
        }
        using (var stale = EncryptedSqlite.Open(path, key))
            Assert.Contains("SCAN", Plan(stale, SegmentDelete)); // the bug, reproduced

        // The connection Open hands back is the one the app uses, so it must
        // already plan correctly - not only connections opened after it.
        using var migrated = MailboxDatabase.Open(path, key);

        Assert.Contains("USING INTEGER PRIMARY KEY", Plan(migrated, SegmentDelete));
        using var count = migrated.CreateCommand();
        count.CommandText = "SELECT count(*) FROM sqlite_stat1 WHERE tbl LIKE 'messages_fts%';";
        Assert.Equal(0L, Convert.ToInt64(count.ExecuteScalar()));
    }

    [Fact]
    public void Delta_sync_looks_messages_up_by_server_id_through_an_index()
    {
        var key = RandomNumberGenerator.GetBytes(32);
        using var db = MailboxDatabase.Open(Path.Combine(_dir, "lookup.db"), key);

        Assert.Contains("ix_messages_server_id",
            Plan(db, "SELECT id FROM messages WHERE server_id = 'x';"));
    }
}
