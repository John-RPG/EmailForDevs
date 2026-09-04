using System.Security.Cryptography;
using System.Text;
using Mail.Storage.Database;
using Microsoft.Data.Sqlite;

namespace Mail.Tests;

public sealed class EncryptedDatabaseTests : IDisposable
{
    readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));

    public EncryptedDatabaseTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    string DbPath(string name) => Path.Combine(_dir, name);

    [Fact]
    public void Data_roundtrips_and_wrong_key_is_rejected()
    {
        var path = DbPath("roundtrip.db");
        var key = RandomNumberGenerator.GetBytes(32);

        using (var conn = EncryptedSqlite.Open(path, key))
        {
            Exec(conn, "CREATE TABLE t(x TEXT); INSERT INTO t VALUES('needle-7f3a');");
        }
        using (var conn = EncryptedSqlite.Open(path, key))
        {
            Assert.Equal("needle-7f3a", Scalar(conn, "SELECT x FROM t;"));
        }

        var wrongKey = RandomNumberGenerator.GetBytes(32);
        Assert.Throws<WrongDatabaseKeyException>(() => EncryptedSqlite.Open(path, wrongKey));
    }

    [Fact]
    public void Database_file_is_actually_encrypted_on_disk()
    {
        var path = DbPath("opaque.db");
        var key = RandomNumberGenerator.GetBytes(32);
        using (var conn = EncryptedSqlite.Open(path, key))
        {
            Exec(conn, "CREATE TABLE t(x TEXT); INSERT INTO t VALUES('needle-7f3a');");
            // Fold WAL content back into the main file before inspecting it.
            Exec(conn, "PRAGMA wal_checkpoint(TRUNCATE);");
        }

        var bytes = File.ReadAllBytes(path);
        var plaintextHeader = Encoding.ASCII.GetBytes("SQLite format 3");
        Assert.False(bytes.AsSpan(0, plaintextHeader.Length).SequenceEqual(plaintextHeader));
        Assert.True(bytes.AsSpan().IndexOf(Encoding.UTF8.GetBytes("needle-7f3a")) < 0,
            "plaintext row content leaked into the database file");
    }

    [Fact]
    public void App_schema_applies_and_reopens()
    {
        var path = DbPath("app.db");
        var key = RandomNumberGenerator.GetBytes(32);
        using (var conn = AppDatabase.Open(path, key))
        {
            Exec(conn, """
                INSERT INTO identities(name) VALUES('John-work');
                INSERT INTO accounts(identity_id, kind, display_name, upn)
                    VALUES(1, 'graph', 'Work', 'john@work.example');
                INSERT INTO mailboxes(account_id, upn, kind, db_path, dek)
                    VALUES(1, 'shared@work.example', 'shared', 'mailboxes/shared@work.example.db', x'00112233445566778899aabbccddeeff00112233445566778899aabbccddeeff');
                """);
        }
        using var reopened = AppDatabase.Open(path, key);
        Assert.Equal(1L, Scalar(reopened, "SELECT count(*) FROM mailboxes WHERE kind='shared';"));
        // Assert migrations ran to the current head rather than a fixed number,
        // so adding one does not fail this test for the wrong reason.
        Assert.Equal((long)AppDatabase.SchemaVersion, Scalar(reopened, "PRAGMA user_version;"));
        // V2: accounts carry the discovery opt-in.
        Assert.Equal(0L, Scalar(reopened, "SELECT discovery_enabled FROM accounts WHERE upn='john@work.example';"));
    }

    [Fact]
    public void Mailbox_schema_applies_and_fts_search_works()
    {
        var path = DbPath("mailbox.db");
        var key = RandomNumberGenerator.GetBytes(32);
        using var conn = MailboxDatabase.Open(path, key);
        Exec(conn, """
            INSERT INTO folders(name, special_use) VALUES('Inbox', 'inbox');
            INSERT INTO messages(folder_id, subject) VALUES(1, 'Quarterly deployment report');
            INSERT INTO messages_fts(rowid, subject, body_text, participants)
                VALUES(1, 'Quarterly deployment report', 'the rollout finished with zero downtime', 'alice@example.com bob@example.com');
            """);
        Assert.Equal(1L, Scalar(conn,
            "SELECT rowid FROM messages_fts WHERE messages_fts MATCH 'rollout AND downtime';"));
        Assert.Equal(1L, Scalar(conn,
            "SELECT rowid FROM messages_fts WHERE messages_fts MATCH 'participants: alice';"));
    }

    [Fact]
    public void Blob_store_dedups_and_gc_removes_orphans()
    {
        var path = DbPath("blobs.db");
        var key = RandomNumberGenerator.GetBytes(32);
        using var conn = MailboxDatabase.Open(path, key);
        Exec(conn, """
            INSERT INTO blobs(sha256, compression, content, size) VALUES(x'01', 'none', x'aa', 1);
            INSERT INTO blobs(sha256, compression, content, size) VALUES(x'02', 'none', x'bb', 1);
            INSERT INTO bodies(raw_size) VALUES(2);
            INSERT INTO bodies(raw_size) VALUES(1);
            -- Both bodies share blob 1 (the "repeated signature image" case).
            INSERT INTO body_segments(body_id, seq, blob_id) VALUES(1, 0, 1), (1, 1, 2), (2, 0, 1);
            """);
        // Same sha256 cannot be inserted twice.
        Assert.Throws<SqliteException>(() =>
            Exec(conn, "INSERT INTO blobs(sha256, compression, content, size) VALUES(x'01', 'none', x'aa', 1);"));

        // Deleting body 1 orphans blob 2 (blob 1 is still used by body 2).
        Exec(conn, "DELETE FROM bodies WHERE id = 1;");
        Assert.Equal(1, MailboxDatabase.CollectGarbageBlobs(conn));
        Assert.Equal(1L, Scalar(conn, "SELECT count(*) FROM blobs;"));
        Assert.Equal(1L, Scalar(conn, "SELECT id FROM blobs;"));
    }

    static void Exec(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    static object? Scalar(SqliteConnection conn, string sql)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = sql;
        return cmd.ExecuteScalar();
    }
}
