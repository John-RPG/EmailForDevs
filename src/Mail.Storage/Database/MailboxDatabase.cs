using Microsoft.Data.Sqlite;

namespace Mail.Storage.Database;

/// <summary>
/// One encrypted database per final mailbox (primary or shared), keyed by that
/// mailbox's DEK from the app.db registry.
///
/// Storage model: a content-addressed blob store (`blobs`, keyed by SHA-256).
/// A message's raw RFC 5322 source is an ordered list of segments
/// (`bodies` → `body_segments` → `blobs`), so repeated MIME parts — signature
/// images, logos, re-forwarded attachments — are stored once mailbox-wide and
/// the original message is still reconstructable byte-for-byte by concatenating
/// its segments. Decoded attachment content shares the same store.
/// </summary>
public static class MailboxDatabase
{
    public static SqliteConnection Open(string path, ReadOnlySpan<byte> dek)
    {
        var conn = EncryptedSqlite.Open(path, dek);
        MigrationRunner.Apply(conn, Migrations);
        return conn;
    }

    /// <summary>Deletes blobs no longer referenced by any segment or attachment.</summary>
    public static int CollectGarbageBlobs(SqliteConnection conn)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            DELETE FROM blobs WHERE id NOT IN (
                SELECT blob_id FROM body_segments
                UNION
                SELECT blob_id FROM attachments WHERE blob_id IS NOT NULL
            );
            """;
        return cmd.ExecuteNonQuery();
    }

    static readonly IReadOnlyList<string> Migrations = [V1, V2];

    const string V2 = """
        ALTER TABLE attachments ADD COLUMN content_id TEXT;
        CREATE INDEX ix_attachments_content_id ON attachments(message_id, content_id);
        """;

    const string V1 = """
        CREATE TABLE folders(
            id           INTEGER PRIMARY KEY,
            parent_id    INTEGER REFERENCES folders(id),
            server_id    TEXT,                      -- Graph folder id / IMAP mailbox name
            name         TEXT NOT NULL,
            special_use  TEXT,                      -- inbox|sent|drafts|trash|junk|archive
            total_count  INTEGER NOT NULL DEFAULT 0,
            unread_count INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE addresses(
            id           INTEGER PRIMARY KEY,
            email        TEXT NOT NULL COLLATE NOCASE,
            display_name TEXT,
            UNIQUE(email, display_name)
        );

        CREATE TABLE blobs(
            id          INTEGER PRIMARY KEY,
            sha256      BLOB NOT NULL UNIQUE,
            compression TEXT NOT NULL,              -- 'none' | 'brotli'
            content     BLOB NOT NULL,
            size        INTEGER NOT NULL            -- uncompressed bytes
        );

        CREATE TABLE bodies(
            id       INTEGER PRIMARY KEY,
            raw_size INTEGER NOT NULL               -- full reconstructed size
        );

        CREATE TABLE body_segments(
            body_id INTEGER NOT NULL REFERENCES bodies(id) ON DELETE CASCADE,
            seq     INTEGER NOT NULL,
            blob_id INTEGER NOT NULL REFERENCES blobs(id),
            PRIMARY KEY(body_id, seq)
        );

        CREATE TABLE messages(
            id                  INTEGER PRIMARY KEY,
            folder_id           INTEGER NOT NULL REFERENCES folders(id),
            server_id           TEXT,               -- Graph message id / IMAP UID
            internet_message_id TEXT,
            conversation_key    TEXT,
            subject             TEXT,
            sent_at             INTEGER,            -- unix seconds, UTC
            received_at         INTEGER,
            is_read             INTEGER NOT NULL DEFAULT 0,
            is_flagged          INTEGER NOT NULL DEFAULT 0,
            is_draft            INTEGER NOT NULL DEFAULT 0,
            has_attachments     INTEGER NOT NULL DEFAULT 0,
            size                INTEGER,
            body_id             INTEGER REFERENCES bodies(id),
            preview             TEXT
        );
        CREATE INDEX ix_messages_folder_date ON messages(folder_id, received_at DESC);
        CREATE INDEX ix_messages_imid ON messages(internet_message_id);
        CREATE INDEX ix_messages_conversation ON messages(conversation_key);

        CREATE TABLE message_references(
            message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
            reference  TEXT NOT NULL,                -- referenced internet_message_id
            PRIMARY KEY(message_id, reference)
        );
        CREATE INDEX ix_message_references_ref ON message_references(reference);

        CREATE TABLE message_addresses(
            message_id INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
            address_id INTEGER NOT NULL REFERENCES addresses(id),
            kind       INTEGER NOT NULL,            -- 0 from, 1 to, 2 cc, 3 bcc, 4 reply-to, 5 sender
            position   INTEGER NOT NULL,
            PRIMARY KEY(message_id, kind, position)
        );
        CREATE INDEX ix_message_addresses_address ON message_addresses(address_id);

        CREATE TABLE attachments(
            id           INTEGER PRIMARY KEY,
            message_id   INTEGER NOT NULL REFERENCES messages(id) ON DELETE CASCADE,
            blob_id      INTEGER REFERENCES blobs(id),  -- decoded content; NULL until fetched
            file_name    TEXT,
            content_type TEXT,
            is_inline    INTEGER NOT NULL DEFAULT 0,
            size         INTEGER
        );
        CREATE INDEX ix_attachments_message ON attachments(message_id);

        CREATE TABLE sync_state(
            folder_id      INTEGER PRIMARY KEY REFERENCES folders(id),
            provider       TEXT NOT NULL,           -- 'graph' | 'imap' | 'pop'
            delta_token    TEXT,
            uidvalidity    INTEGER,
            highest_modseq INTEGER,
            last_sync_at   INTEGER
        );

        CREATE VIRTUAL TABLE messages_fts USING fts5(
            subject,
            body_text,
            participants
        );
        """;
}
