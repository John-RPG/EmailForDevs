using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace Mail.Storage.Database;

/// <summary>
/// Unsent messages, kept locally so closing the window — or losing the process —
/// does not throw away what was typed.
///
/// Drafts live in app.db rather than a mailbox database because a draft is not
/// yet mail: it has no server id, may change which account it is sent from, and
/// must survive a mailbox being removed. Attachments are stored inline; a draft
/// is short-lived and rarely large, and putting them in the content-addressed
/// blob store would tie an unsent message's lifetime to deduplicated content it
/// does not own.
/// </summary>
public sealed class DraftStore(SqliteConnection appDb)
{
    /// <param name="Id">Stable across saves, so autosave updates rather than piles up.</param>
    /// <param name="IsRich">Whether the body is HTML; a plain draft keeps Body only.</param>
    public sealed record DraftRecord(
        long Id, string From, string To, string Cc, string Bcc, string Subject,
        string Body, string? HtmlBody, bool IsRich,
        string? InReplyTo, string? References,
        IReadOnlyList<DraftFile> Attachments, DateTimeOffset UpdatedAt)
    {
        /// <summary>What the drafts list shows, since an empty subject is common.</summary>
        public string Display =>
            (Subject.Length > 0 ? Subject : "(no subject)") +
            (To.Length > 0 ? $" — to {To}" : "");
    }

    public sealed record DraftFile(string FileName, string ContentType, byte[] Content);

    /// <summary>
    /// Inserts or updates a draft, returning its id. Pass 0 to create one; pass
    /// the returned id to keep updating the same row as the user types.
    /// </summary>
    public long Save(DraftRecord draft)
    {
        using var cmd = appDb.CreateCommand();
        if (draft.Id == 0)
        {
            cmd.CommandText = """
                INSERT INTO drafts(from_address, to_addresses, cc_addresses, bcc_addresses,
                                   subject, body, html_body, is_rich, in_reply_to,
                                   references_header, attachments, updated_at)
                VALUES(@f, @to, @cc, @bcc, @s, @b, @h, @r, @irt, @refs, @att, unixepoch());
                SELECT last_insert_rowid();
                """;
        }
        else
        {
            cmd.CommandText = """
                UPDATE drafts SET
                    from_address = @f, to_addresses = @to, cc_addresses = @cc,
                    bcc_addresses = @bcc, subject = @s, body = @b, html_body = @h,
                    is_rich = @r, in_reply_to = @irt, references_header = @refs,
                    attachments = @att, updated_at = unixepoch()
                WHERE id = @id;
                SELECT @id;
                """;
            cmd.Parameters.AddWithValue("@id", draft.Id);
        }

        cmd.Parameters.AddWithValue("@f", draft.From);
        cmd.Parameters.AddWithValue("@to", draft.To);
        cmd.Parameters.AddWithValue("@cc", draft.Cc);
        cmd.Parameters.AddWithValue("@bcc", draft.Bcc);
        cmd.Parameters.AddWithValue("@s", draft.Subject);
        cmd.Parameters.AddWithValue("@b", draft.Body);
        cmd.Parameters.AddWithValue("@h", (object?)draft.HtmlBody ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@r", draft.IsRich ? 1 : 0);
        cmd.Parameters.AddWithValue("@irt", (object?)draft.InReplyTo ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@refs", (object?)draft.References ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@att", SerialiseAttachments(draft.Attachments));
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public void Delete(long id)
    {
        using var cmd = appDb.CreateCommand();
        cmd.CommandText = "DELETE FROM drafts WHERE id = @id;";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Newest first, which is the order they are offered for resuming.</summary>
    public List<DraftRecord> List()
    {
        using var cmd = appDb.CreateCommand();
        cmd.CommandText = """
            SELECT id, from_address, to_addresses, cc_addresses, bcc_addresses,
                   subject, body, html_body, is_rich, in_reply_to,
                   references_header, attachments, updated_at
            FROM drafts ORDER BY updated_at DESC;
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<DraftRecord>();
        while (reader.Read()) result.Add(Read(reader));
        return result;
    }

    public DraftRecord? Get(long id)
    {
        using var cmd = appDb.CreateCommand();
        cmd.CommandText = """
            SELECT id, from_address, to_addresses, cc_addresses, bcc_addresses,
                   subject, body, html_body, is_rich, in_reply_to,
                   references_header, attachments, updated_at
            FROM drafts WHERE id = @id;
            """;
        cmd.Parameters.AddWithValue("@id", id);
        using var reader = cmd.ExecuteReader();
        return reader.Read() ? Read(reader) : null;
    }

    public int Count()
    {
        using var cmd = appDb.CreateCommand();
        cmd.CommandText = "SELECT count(*) FROM drafts;";
        return Convert.ToInt32(cmd.ExecuteScalar());
    }

    static DraftRecord Read(SqliteDataReader reader) => new(
        reader.GetInt64(0), reader.GetString(1), reader.GetString(2),
        reader.GetString(3), reader.GetString(4), reader.GetString(5),
        reader.GetString(6),
        reader.IsDBNull(7) ? null : reader.GetString(7),
        reader.GetInt64(8) != 0,
        reader.IsDBNull(9) ? null : reader.GetString(9),
        reader.IsDBNull(10) ? null : reader.GetString(10),
        DeserialiseAttachments(reader.IsDBNull(11) ? null : reader.GetString(11)),
        DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(12)));

    static string SerialiseAttachments(IReadOnlyList<DraftFile> files) =>
        files.Count == 0 ? "[]" : JsonSerializer.Serialize(files);

    static IReadOnlyList<DraftFile> DeserialiseAttachments(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return [];
        try
        {
            return JsonSerializer.Deserialize<List<DraftFile>>(json) ?? [];
        }
        catch (JsonException)
        {
            // A draft with unreadable attachments is still worth recovering:
            // losing the text as well would defeat the point of saving it.
            return [];
        }
    }
}
