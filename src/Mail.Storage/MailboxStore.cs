using System.Security.Cryptography;
using Mail.Core.Ingest;
using Mail.Storage.Database;
using Microsoft.Data.Sqlite;

namespace Mail.Storage;

/// <summary>
/// Writes parsed messages into a mailbox database: content-addressed segments,
/// deduplicated addresses, attachments, FTS row, and conversation threading
/// (adopt referenced messages' key; merge when bridging; late parents find
/// children through message_references).
/// </summary>
public static class MailboxStore
{
    public static long IngestMessage(
        SqliteConnection conn, long folderId, byte[] raw, ParsedMessage parsed,
        DateTimeOffset? receivedAt = null, string? serverId = null)
    {
        using var tx = conn.BeginTransaction();

        var bodyId = Insert(conn, tx,
            "INSERT INTO bodies(raw_size) VALUES(@size);", ("@size", raw.Length));
        var seq = 0;
        foreach (var segment in parsed.Segments)
        {
            var blobId = UpsertBlob(conn, tx, raw.AsSpan(segment.Offset, segment.Length));
            Exec(conn, tx,
                "INSERT INTO body_segments(body_id, seq, blob_id) VALUES(@b, @s, @l);",
                ("@b", bodyId), ("@s", seq++), ("@l", blobId));
        }

        var conversationKey = ResolveConversationKey(conn, tx, parsed);

        var messageId = Insert(conn, tx, """
            INSERT INTO messages(folder_id, server_id, internet_message_id, conversation_key,
                                 subject, sent_at, received_at, has_attachments, size, body_id, preview)
            VALUES(@folder, @server, @imid, @ck, @subject, @sent, @recv, @att, @size, @body, @preview);
            """,
            ("@folder", folderId),
            ("@server", serverId),
            ("@imid", parsed.MessageId),
            ("@ck", conversationKey),
            ("@subject", parsed.Subject),
            ("@sent", parsed.SentAt?.ToUnixTimeSeconds()),
            ("@recv", (receivedAt ?? parsed.SentAt)?.ToUnixTimeSeconds()),
            ("@att", parsed.HasAttachments ? 1 : 0),
            ("@size", raw.Length),
            ("@body", bodyId),
            ("@preview", parsed.Preview));

        foreach (var reference in parsed.References)
            Exec(conn, tx,
                "INSERT OR IGNORE INTO message_references(message_id, reference) VALUES(@m, @r);",
                ("@m", messageId), ("@r", reference));

        var positionPerKind = new Dictionary<AddressKind, int>();
        foreach (var address in parsed.Addresses)
        {
            var addressId = UpsertAddress(conn, tx, address);
            var position = positionPerKind.GetValueOrDefault(address.Kind);
            positionPerKind[address.Kind] = position + 1;
            Exec(conn, tx, """
                INSERT INTO message_addresses(message_id, address_id, kind, position)
                VALUES(@m, @a, @k, @p);
                """,
                ("@m", messageId), ("@a", addressId), ("@k", (int)address.Kind), ("@p", position));
        }

        foreach (var attachment in parsed.Attachments)
        {
            var blobId = UpsertBlob(conn, tx, attachment.Content);
            Exec(conn, tx, """
                INSERT INTO attachments(message_id, blob_id, file_name, content_type, is_inline, size)
                VALUES(@m, @b, @f, @c, @i, @s);
                """,
                ("@m", messageId), ("@b", blobId), ("@f", attachment.FileName),
                ("@c", attachment.ContentType), ("@i", attachment.IsInline ? 1 : 0),
                ("@s", attachment.Content.Length));
        }

        var participants = string.Join(' ', parsed.Addresses.Select(a =>
            a.DisplayName is null ? a.Email : $"{a.Email} {a.DisplayName}"));
        Exec(conn, tx, """
            INSERT INTO messages_fts(rowid, subject, body_text, participants)
            VALUES(@id, @subject, @body, @participants);
            """,
            ("@id", messageId), ("@subject", parsed.Subject ?? ""),
            ("@body", parsed.BodyText ?? ""), ("@participants", participants));

        tx.Commit();
        return messageId;
    }

    /// <summary>Reconstructs the original raw message byte-for-byte from its segments.</summary>
    public static byte[] GetRawMessage(SqliteConnection conn, long messageId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = """
            SELECT b.compression, b.content, b.size
            FROM messages m
            JOIN body_segments s ON s.body_id = m.body_id
            JOIN blobs b ON b.id = s.blob_id
            WHERE m.id = @id
            ORDER BY s.seq;
            """;
        cmd.Parameters.AddWithValue("@id", messageId);
        using var reader = cmd.ExecuteReader();
        using var result = new MemoryStream();
        while (reader.Read())
        {
            var decoded = BlobCodec.Decode(
                reader.GetString(0), (byte[])reader.GetValue(1), reader.GetInt32(2));
            result.Write(decoded);
        }
        if (result.Length == 0)
            throw new InvalidOperationException($"Message {messageId} has no stored body.");
        return result.ToArray();
    }

    // ---- sync support --------------------------------------------------------

    /// <summary>Insert or update a folder by its server id; resolves the parent by server id too.</summary>
    public static long UpsertFolder(
        SqliteConnection conn, string serverId, string? parentServerId, string name,
        string? specialUse, int totalCount, int unreadCount)
    {
        var parentId = parentServerId is null ? null : FindFolderByServerId(conn, parentServerId);
        var existing = FindFolderByServerId(conn, serverId);
        if (existing is long id)
        {
            Exec(conn, null, """
                UPDATE folders SET parent_id=@p, name=@n, special_use=@s,
                                   total_count=@t, unread_count=@u WHERE id=@id;
                """,
                ("@p", parentId), ("@n", name), ("@s", specialUse),
                ("@t", totalCount), ("@u", unreadCount), ("@id", id));
            return id;
        }
        return Insert(conn, null, """
            INSERT INTO folders(server_id, parent_id, name, special_use, total_count, unread_count)
            VALUES(@sid, @p, @n, @s, @t, @u);
            """,
            ("@sid", serverId), ("@p", parentId), ("@n", name), ("@s", specialUse),
            ("@t", totalCount), ("@u", unreadCount));
    }

    public static long? FindFolderByServerId(SqliteConnection conn, string serverId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM folders WHERE server_id = @s;";
        cmd.Parameters.AddWithValue("@s", serverId);
        return cmd.ExecuteScalar() as long?;
    }

    public static long? TryGetMessageIdByServerId(SqliteConnection conn, string serverId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT id FROM messages WHERE server_id = @s;";
        cmd.Parameters.AddWithValue("@s", serverId);
        return cmd.ExecuteScalar() as long?;
    }

    /// <summary>Applies server-side state (folder move, read/flag/draft) to an existing message.</summary>
    public static void UpdateMessageState(
        SqliteConnection conn, long messageId, long folderId,
        bool isRead, bool isFlagged, bool isDraft)
    {
        Exec(conn, null, """
            UPDATE messages SET folder_id=@f, is_read=@r, is_flagged=@fl, is_draft=@d WHERE id=@id;
            """,
            ("@f", folderId), ("@r", isRead ? 1 : 0), ("@fl", isFlagged ? 1 : 0),
            ("@d", isDraft ? 1 : 0), ("@id", messageId));
    }

    /// <summary>
    /// Removes a message reported deleted in this folder (skips if it was moved and
    /// already re-homed elsewhere). Cleans the FTS row and the body; orphaned blobs
    /// are left for <see cref="MailboxDatabase.CollectGarbageBlobs"/>.
    /// </summary>
    public static bool DeleteMessageByServerId(SqliteConnection conn, string serverId, long folderId)
    {
        using var tx = conn.BeginTransaction();
        long messageId;
        long? bodyId;
        using (var find = Command(conn, tx,
            "SELECT id, body_id FROM messages WHERE server_id = @s AND folder_id = @f;",
            [("@s", serverId), ("@f", folderId)]))
        using (var reader = find.ExecuteReader())
        {
            if (!reader.Read())
                return false;
            messageId = reader.GetInt64(0);
            bodyId = reader.IsDBNull(1) ? null : reader.GetInt64(1);
        }
        Exec(conn, tx, "DELETE FROM messages WHERE id = @id;", ("@id", messageId));
        Exec(conn, tx, "DELETE FROM messages_fts WHERE rowid = @id;", ("@id", messageId));
        if (bodyId is long b)
            Exec(conn, tx, "DELETE FROM bodies WHERE id = @b;", ("@b", b));
        tx.Commit();
        return true;
    }

    public static string? GetDeltaToken(SqliteConnection conn, long folderId)
    {
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT delta_token FROM sync_state WHERE folder_id = @f;";
        cmd.Parameters.AddWithValue("@f", folderId);
        return cmd.ExecuteScalar() as string;
    }

    public static void SaveDeltaToken(SqliteConnection conn, long folderId, string provider, string? deltaToken)
    {
        Exec(conn, null, """
            INSERT INTO sync_state(folder_id, provider, delta_token, last_sync_at)
            VALUES(@f, @p, @t, unixepoch())
            ON CONFLICT(folder_id) DO UPDATE SET
                provider=@p, delta_token=@t, last_sync_at=unixepoch();
            """,
            ("@f", folderId), ("@p", provider), ("@t", deltaToken));
    }

    static string ResolveConversationKey(SqliteConnection conn, SqliteTransaction tx, ParsedMessage parsed)
    {
        var keys = new List<string>();

        if (parsed.References.Count > 0)
        {
            var names = parsed.References.Select((_, i) => "@r" + i).ToArray();
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText =
                "SELECT DISTINCT conversation_key FROM messages " +
                $"WHERE internet_message_id IN ({string.Join(", ", names)}) AND conversation_key IS NOT NULL;";
            for (var i = 0; i < parsed.References.Count; i++)
                cmd.Parameters.AddWithValue(names[i], parsed.References[i]);
            using var reader = cmd.ExecuteReader();
            while (reader.Read()) keys.Add(reader.GetString(0));
        }

        if (parsed.MessageId is not null)
        {
            // Out-of-order arrival: children that referenced this message are already here.
            using var cmd = conn.CreateCommand();
            cmd.Transaction = tx;
            cmd.CommandText = """
                SELECT DISTINCT m.conversation_key FROM messages m
                JOIN message_references r ON r.message_id = m.id
                WHERE r.reference = @id AND m.conversation_key IS NOT NULL;
                """;
            cmd.Parameters.AddWithValue("@id", parsed.MessageId);
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                if (!keys.Contains(reader.GetString(0)))
                    keys.Add(reader.GetString(0));
        }

        var key = keys.FirstOrDefault()
                  ?? parsed.MessageId
                  ?? Guid.NewGuid().ToString("N");
        foreach (var stale in keys.Skip(1))
            Exec(conn, tx,
                "UPDATE messages SET conversation_key = @new WHERE conversation_key = @old;",
                ("@new", key), ("@old", stale));
        return key;
    }

    static long UpsertBlob(SqliteConnection conn, SqliteTransaction tx, ReadOnlySpan<byte> content)
    {
        var sha = SHA256.HashData(content);
        using (var find = conn.CreateCommand())
        {
            find.Transaction = tx;
            find.CommandText = "SELECT id FROM blobs WHERE sha256 = @sha;";
            find.Parameters.AddWithValue("@sha", sha);
            if (find.ExecuteScalar() is long existing)
                return existing;
        }
        var (compression, stored) = BlobCodec.Encode(content);
        return Insert(conn, tx,
            "INSERT INTO blobs(sha256, compression, content, size) VALUES(@sha, @c, @data, @size);",
            ("@sha", sha), ("@c", compression), ("@data", stored), ("@size", content.Length));
    }

    static long UpsertAddress(SqliteConnection conn, SqliteTransaction tx, MessageAddress address)
    {
        Exec(conn, tx,
            "INSERT OR IGNORE INTO addresses(email, display_name) VALUES(@e, @n);",
            ("@e", address.Email), ("@n", address.DisplayName));
        using var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = "SELECT id FROM addresses WHERE email = @e AND display_name IS @n;";
        cmd.Parameters.AddWithValue("@e", address.Email);
        cmd.Parameters.AddWithValue("@n", (object?)address.DisplayName ?? DBNull.Value);
        return (long)cmd.ExecuteScalar()!;
    }

    static long Insert(SqliteConnection conn, SqliteTransaction? tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(conn, tx, sql + " SELECT last_insert_rowid();", parameters);
        return (long)cmd.ExecuteScalar()!;
    }

    static void Exec(SqliteConnection conn, SqliteTransaction? tx, string sql,
        params (string Name, object? Value)[] parameters)
    {
        using var cmd = Command(conn, tx, sql, parameters);
        cmd.ExecuteNonQuery();
    }

    static SqliteCommand Command(SqliteConnection conn, SqliteTransaction? tx, string sql,
        (string Name, object? Value)[] parameters)
    {
        var cmd = conn.CreateCommand();
        cmd.Transaction = tx;
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
            cmd.Parameters.AddWithValue(name, value ?? DBNull.Value);
        return cmd;
    }
}
