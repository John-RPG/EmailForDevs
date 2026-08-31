using System.Security.Cryptography;
using Mail.Core.Ingest;
using Mail.Storage;
using Mail.Storage.Database;
using Microsoft.Data.Sqlite;
using MimeKit;

namespace Mail.Tests;

public sealed class MailboxStoreSyncTests : IDisposable
{
    readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));
    readonly SqliteConnection _conn;

    public MailboxStoreSyncTests()
    {
        Directory.CreateDirectory(_dir);
        _conn = MailboxDatabase.Open(Path.Combine(_dir, "mailbox.db"), RandomNumberGenerator.GetBytes(32));
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    long Scalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    long IngestSample(long folderId, string serverId)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        message.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        message.Subject = "hello";
        message.Body = new TextPart("plain") { Text = "sample body text" };
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        var raw = stream.ToArray();
        return MailboxStore.IngestMessage(
            _conn, folderId, raw, MimeMessageParser.Parse(raw),
            DateTimeOffset.UtcNow, serverId);
    }

    [Fact]
    public void UpsertFolder_inserts_updates_and_links_parents()
    {
        var inbox = MailboxStore.UpsertFolder(_conn, "srv-inbox", null, "Inbox", "inbox", 10, 2);
        var child = MailboxStore.UpsertFolder(_conn, "srv-child", "srv-inbox", "Receipts", null, 3, 0);
        Assert.Equal(inbox, MailboxStore.FindFolderByServerId(_conn, "srv-inbox"));

        // Second upsert updates in place (rename + counts), same local id.
        var again = MailboxStore.UpsertFolder(_conn, "srv-child", "srv-inbox", "Receipts2", null, 4, 1);
        Assert.Equal(child, again);
        Assert.Equal(1L, Scalar("SELECT count(*) FROM folders WHERE server_id='srv-child';"));
        Assert.Equal(inbox, Scalar("SELECT parent_id FROM folders WHERE server_id='srv-child';"));
        Assert.Equal(4L, Scalar("SELECT total_count FROM folders WHERE server_id='srv-child';"));
    }

    [Fact]
    public void UpdateMessageState_moves_and_flags()
    {
        var inbox = MailboxStore.UpsertFolder(_conn, "srv-inbox", null, "Inbox", "inbox", 0, 0);
        var archive = MailboxStore.UpsertFolder(_conn, "srv-archive", null, "Archive", "archive", 0, 0);
        var id = IngestSample(inbox, "msg-1");

        MailboxStore.UpdateMessageState(_conn, id, archive, isRead: true, isFlagged: true, isDraft: false);
        Assert.Equal(archive, Scalar("SELECT folder_id FROM messages WHERE id=" + id));
        Assert.Equal(1L, Scalar("SELECT is_read FROM messages WHERE id=" + id));
        Assert.Equal(1L, Scalar("SELECT is_flagged FROM messages WHERE id=" + id));
    }

    [Fact]
    public void DeleteMessageByServerId_cleans_fts_and_body_and_is_move_safe()
    {
        var inbox = MailboxStore.UpsertFolder(_conn, "srv-inbox", null, "Inbox", "inbox", 0, 0);
        var other = MailboxStore.UpsertFolder(_conn, "srv-other", null, "Other", null, 0, 0);
        var id = IngestSample(inbox, "msg-1");

        // Reported removed from a folder it is no longer in: no-op (move safety).
        Assert.False(MailboxStore.DeleteMessageByServerId(_conn, "msg-1", other));
        Assert.Equal(1L, Scalar("SELECT count(*) FROM messages;"));

        Assert.True(MailboxStore.DeleteMessageByServerId(_conn, "msg-1", inbox));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM messages;"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM messages_fts WHERE messages_fts MATCH 'sample';"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM bodies;"));
        Assert.Equal(0L, Scalar("SELECT count(*) FROM body_segments;"));
        // Blobs are swept separately.
        MailboxDatabase.CollectGarbageBlobs(_conn);
        Assert.Equal(0L, Scalar("SELECT count(*) FROM blobs;"));
    }

    [Fact]
    public void Delta_token_round_trips_and_overwrites()
    {
        var inbox = MailboxStore.UpsertFolder(_conn, "srv-inbox", null, "Inbox", "inbox", 0, 0);
        Assert.Null(MailboxStore.GetDeltaToken(_conn, inbox));
        MailboxStore.SaveDeltaToken(_conn, inbox, "graph", "https://graph/delta?token=1");
        Assert.Equal("https://graph/delta?token=1", MailboxStore.GetDeltaToken(_conn, inbox));
        MailboxStore.SaveDeltaToken(_conn, inbox, "graph", "https://graph/delta?token=2");
        Assert.Equal("https://graph/delta?token=2", MailboxStore.GetDeltaToken(_conn, inbox));
    }
}
