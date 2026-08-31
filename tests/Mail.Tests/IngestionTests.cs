using System.Security.Cryptography;
using Mail.Core.Ingest;
using Mail.Core.Search;
using Mail.Storage;
using Mail.Storage.Database;
using Microsoft.Data.Sqlite;
using MimeKit;
using MimeKit.Utils;
using static Mail.Core.Search.QueryNode;

namespace Mail.Tests;

public sealed class IngestionTests : IDisposable
{
    readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));
    readonly SqliteConnection _conn;

    public IngestionTests()
    {
        Directory.CreateDirectory(_dir);
        _conn = MailboxDatabase.Open(Path.Combine(_dir, "mailbox.db"), RandomNumberGenerator.GetBytes(32));
        Exec("INSERT INTO folders(id, name, special_use) VALUES(1, 'Inbox', 'inbox');");
    }

    public void Dispose()
    {
        _conn.Dispose();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    void Exec(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        cmd.ExecuteNonQuery();
    }

    long Scalar(string sql)
    {
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = sql;
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    static byte[] SampleAttachment { get; } =
        [.. Enumerable.Range(0, 20_000).Select(i => (byte)(i % 251))];

    static byte[] BuildMessage(
        string fromName, string fromEmail, string subject, string htmlBody,
        (string Name, byte[] Content)? attachment = null,
        string? messageId = null, string[]? references = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, fromEmail));
        message.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        message.Subject = subject;
        message.MessageId = messageId ?? MimeUtils.GenerateMessageId();
        message.Date = DateTimeOffset.Parse("2026-06-01T10:00:00Z");
        foreach (var reference in references ?? [])
            message.References.Add(reference);
        var builder = new BodyBuilder { HtmlBody = htmlBody };
        if (attachment is { } a)
            builder.Attachments.Add(a.Name, a.Content);
        message.Body = builder.ToMessageBody();
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        return stream.ToArray();
    }

    long Ingest(byte[] raw) =>
        MailboxStore.IngestMessage(_conn, 1, raw, MimeMessageParser.Parse(raw));

    [Fact]
    public void Raw_message_roundtrips_byte_exact()
    {
        var raw = BuildMessage("Alice Adams", "alice@example.com", "Report attached",
            "<p>see attached</p>", ("report.bin", SampleAttachment));
        var id = Ingest(raw);
        Assert.Equal(raw, MailboxStore.GetRawMessage(_conn, id));
        // The attachment was big enough to become its own dedup segment.
        Assert.True(Scalar("SELECT count(*) FROM body_segments") >= 2);
    }

    [Fact]
    public void Repeated_attachment_is_stored_once()
    {
        var raw1 = BuildMessage("Alice Adams", "alice@example.com", "First send",
            "<p>first</p>", ("sig.png", SampleAttachment));
        var raw2 = BuildMessage("Alice Adams", "alice@example.com", "Second send, same sig",
            "<p>second, different text</p>", ("sig.png", SampleAttachment));
        var id1 = Ingest(raw1);
        var id2 = Ingest(raw2);

        // At least one raw segment blob is shared between the two bodies…
        Assert.True(Scalar("""
            SELECT count(*) FROM
                (SELECT blob_id FROM body_segments GROUP BY blob_id HAVING count(*) > 1);
            """) >= 1);
        // …and the decoded attachment content is a single blob referenced by both rows.
        Assert.Equal(1, Scalar("SELECT count(DISTINCT blob_id) FROM attachments;"));
        Assert.Equal(2, Scalar("SELECT count(*) FROM attachments;"));

        // Dedup never compromises reconstruction.
        Assert.Equal(raw1, MailboxStore.GetRawMessage(_conn, id1));
        Assert.Equal(raw2, MailboxStore.GetRawMessage(_conn, id2));
    }

    [Fact]
    public void Threading_links_replies_even_out_of_order()
    {
        const string rootId = "root@test.local";
        var reply1 = BuildMessage("Bob", "bob@example.com", "RE: kickoff", "<p>+1</p>",
            references: [rootId]);
        var root = BuildMessage("Alice Adams", "alice@example.com", "kickoff", "<p>let's go</p>",
            messageId: rootId);
        var reply2 = BuildMessage("Carol", "carol@big.example", "RE: kickoff", "<p>agreed</p>",
            references: [rootId]);
        var unrelated = BuildMessage("Dave", "dave@example.com", "lunch", "<p>?</p>");

        Ingest(reply1);   // arrives before its parent
        Ingest(root);
        Ingest(reply2);
        Ingest(unrelated);

        Assert.Equal(2, Scalar("SELECT count(DISTINCT conversation_key) FROM messages;"));
        Assert.Equal(3, Scalar("""
            SELECT count(*) FROM messages WHERE conversation_key =
                (SELECT conversation_key FROM messages WHERE internet_message_id = 'root@test.local');
            """));
    }

    [Fact]
    public void Ingested_mail_is_searchable_end_to_end()
    {
        Ingest(BuildMessage("Alice Adams", "alice@example.com", "Zebra migration",
            "<p>the <b>zebra</b> cluster &amp; failover plan</p>"));
        Ingest(BuildMessage("Bob", "bob@example.com", "Lunch", "<p>burgers?</p>"));

        var compiled = SqliteQueryCompiler.Compile(And(
            Where(MessageProperty.Body, ConditionOperator.Contains, "zebra failover"),
            Where(MessageProperty.From, ConditionOperator.Contains, "Adams")));
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT m.subject FROM messages m WHERE {compiled.WhereSql};";
        foreach (var (name, value) in compiled.Parameters)
            cmd.Parameters.AddWithValue(name, value);
        Assert.Equal("Zebra migration", (string?)cmd.ExecuteScalar());
    }

    [Fact]
    public void Inline_image_content_id_is_stored_and_retrievable()
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Alice", "alice@example.com"));
        message.To.Add(new MailboxAddress("Bob", "bob@example.com"));
        message.Subject = "with inline image";
        var builder = new BodyBuilder { HtmlBody = "<p>logo: <img src=\"cid:logo123\"></p>" };
        var image = builder.LinkedResources.Add("logo.png", SampleAttachment);
        image.ContentId = "logo123";
        message.Body = builder.ToMessageBody();
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        var raw = stream.ToArray();

        var id = MailboxStore.IngestMessage(_conn, 1, raw, MimeMessageParser.Parse(raw));
        var inline = MailboxStore.TryGetInlineAttachment(_conn, id, "logo123");
        Assert.NotNull(inline);
        Assert.Equal(SampleAttachment, inline.Value.Content);
        Assert.Contains("image", inline.Value.ContentType);
        Assert.Null(MailboxStore.TryGetInlineAttachment(_conn, id, "nope"));
    }

    [Fact]
    public void Html_to_text_strips_tags_scripts_and_decodes_entities()
    {
        var text = HtmlText.ToPlainText(
            "<div>Hello <b>world</b><script>evil()</script><style>p{}</style> &amp; friends</div>");
        Assert.Equal("Hello world & friends", text);
    }
}
