using System.Security.Cryptography;
using Mail.Core.Search;
using Mail.Storage.Database;
using Microsoft.Data.Sqlite;
using static Mail.Core.Search.QueryNode;

namespace Mail.Tests;

public sealed class SearchQueryTests : IDisposable
{
    readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));
    readonly SqliteConnection _conn;

    static long Utc(string iso) => DateTimeOffset.Parse(iso).ToUnixTimeSeconds();

    public SearchQueryTests()
    {
        Directory.CreateDirectory(_dir);
        _conn = MailboxDatabase.Open(Path.Combine(_dir, "mailbox.db"), RandomNumberGenerator.GetBytes(32));
        Exec($"""
            INSERT INTO folders(id, name, special_use) VALUES(1, 'Inbox', 'inbox'), (2, 'Archive', 'archive');
            INSERT INTO addresses(id, email, display_name) VALUES
                (1, 'alice@example.com', 'Alice Adams'),
                (2, 'bob@example.com', 'Bob'),
                (3, 'carol@big.example', 'Carol');
            INSERT INTO messages(id, folder_id, subject, received_at, size, is_read, has_attachments) VALUES
                (1, 1, 'Invoice March',  {Utc("2026-03-05T12:00:00Z")}, 5000,    1, 1),
                (2, 1, 'Lunch?',         {Utc("2026-05-10T09:00:00Z")}, 800,     0, 0),
                (3, 2, 'Invoice April',  {Utc("2026-04-02T15:00:00Z")}, 4000000, 0, 1);
            INSERT INTO message_addresses(message_id, address_id, kind, position) VALUES
                (1, 1, 0, 0), (1, 2, 1, 0),          -- 1: from alice, to bob
                (2, 2, 0, 0), (2, 1, 1, 0),          -- 2: from bob, to alice
                (3, 3, 0, 0), (3, 1, 1, 0), (3, 2, 2, 0); -- 3: from carol, to alice, cc bob
            INSERT INTO attachments(message_id, blob_id, file_name, content_type, size) VALUES
                (3, NULL, 'quarterly-report.pdf', 'application/pdf', 123456);
            INSERT INTO messages_fts(rowid, subject, body_text, participants) VALUES
                (1, 'Invoice March', 'please find attached the invoice for march', 'alice@example.com bob@example.com'),
                (2, 'Lunch?', 'fancy lunch tomorrow at noon', 'bob@example.com alice@example.com'),
                (3, 'Invoice April', 'quarterly figures attached as discussed', 'carol@big.example alice@example.com bob@example.com');
            """);
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

    List<long> Run(QueryNode query)
    {
        var compiled = SqliteQueryCompiler.Compile(query);
        using var cmd = _conn.CreateCommand();
        cmd.CommandText = $"SELECT m.id FROM messages m WHERE {compiled.WhereSql} ORDER BY m.id;";
        foreach (var (name, value) in compiled.Parameters)
            cmd.Parameters.AddWithValue(name, value);
        var ids = new List<long>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) ids.Add(reader.GetInt64(0));
        return ids;
    }

    [Fact]
    public void From_equals_matches_only_that_sender()
    {
        Assert.Equal([1L], Run(Where(MessageProperty.From, ConditionOperator.Equals, "ALICE@example.com")));
    }

    [Fact]
    public void Address_contains_matches_display_name_too()
    {
        Assert.Equal([1L], Run(Where(MessageProperty.From, ConditionOperator.Contains, "Adams")));
        Assert.Equal([2L, 3L], Run(Where(MessageProperty.AnyRecipient, ConditionOperator.Contains, "Adams")));
    }

    [Fact]
    public void Nested_groups_with_date_band_and_size()
    {
        // (subject contains 'invoice') AND received in March–April AND size < 1MB
        var query = And(
            Where(MessageProperty.Subject, ConditionOperator.Contains, "invoice"),
            Where(MessageProperty.Received, ConditionOperator.Between,
                DateTimeOffset.Parse("2026-03-01T00:00:00Z"), DateTimeOffset.Parse("2026-04-30T23:59:59Z")),
            Where(MessageProperty.Size, ConditionOperator.LessThan, 1_000_000));
        Assert.Equal([1L], Run(query));
    }

    [Fact]
    public void Or_and_not_compose()
    {
        var query = Or(
            Where(MessageProperty.Folder, ConditionOperator.Equals, "Archive"),
            Not(Where(MessageProperty.Subject, ConditionOperator.Contains, "invoice")));
        Assert.Equal([2L, 3L], Run(query));
    }

    [Fact]
    public void Fulltext_body_and_bare_text()
    {
        Assert.Equal([1L, 3L], Run(Where(MessageProperty.Body, ConditionOperator.Contains, "attached")));
        Assert.Equal([2L], Run(Text("lunch noon")));
        Assert.Equal([2L], Run(Where(MessageProperty.Body, ConditionOperator.NotContains, "attached")));
    }

    [Fact]
    public void Attachment_name_and_flags()
    {
        var query = And(
            Where(MessageProperty.AttachmentName, ConditionOperator.EndsWith, ".pdf"),
            Where(MessageProperty.IsRead, ConditionOperator.Equals, false),
            Where(MessageProperty.HasAttachments, ConditionOperator.Equals, true));
        Assert.Equal([3L], Run(query));
    }

    [Fact]
    public void Like_wildcards_in_user_input_are_literal()
    {
        Assert.Empty(Run(Where(MessageProperty.Subject, ConditionOperator.Contains, "%")));
        Assert.Empty(Run(Where(MessageProperty.Subject, ConditionOperator.Contains, "In_oice")));
    }

    [Fact]
    public void Invalid_operator_for_property_throws()
    {
        Assert.Throws<QueryCompilationException>(() =>
            SqliteQueryCompiler.Compile(Where(MessageProperty.Received, ConditionOperator.Contains, "x")));
        Assert.Throws<QueryCompilationException>(() =>
            SqliteQueryCompiler.Compile(Where(MessageProperty.Size, ConditionOperator.Between, 1)));
    }

    [Fact]
    public void Json_roundtrip_preserves_query()
    {
        var query = And(
            Or(Where(MessageProperty.From, ConditionOperator.Contains, "alice"),
               Where(MessageProperty.Size, ConditionOperator.Between, 100L, 5000L)),
            Not(Where(MessageProperty.IsRead, ConditionOperator.Equals, true)),
            Text("invoice"));
        var json = QueryJson.Serialize(query);
        var roundTripped = QueryJson.Serialize(QueryJson.Deserialize(json));
        Assert.Equal(json, roundTripped);

        // And the deserialized tree still compiles and runs identically.
        Assert.Equal(Run(query), Run(QueryJson.Deserialize(json)));
    }
}
