using System.Security.Cryptography;
using Mail.Core.Search;
using Mail.Storage.Database;
using Microsoft.Data.Sqlite;

namespace Mail.Tests;

public sealed class QueryParserTests : IDisposable
{
    readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));
    readonly SqliteConnection _conn;

    static long Utc(string iso) => DateTimeOffset.Parse(iso).ToUnixTimeSeconds();

    public QueryParserTests()
    {
        Directory.CreateDirectory(_dir);
        _conn = MailboxDatabase.Open(Path.Combine(_dir, "mailbox.db"), RandomNumberGenerator.GetBytes(32));
        Exec($"""
            INSERT INTO folders(id, name, special_use) VALUES(1, 'Inbox', 'inbox'), (2, 'Archive', 'archive');
            INSERT INTO addresses(id, email, display_name) VALUES
                (1, 'alice@example.com', 'Alice Adams'),
                (2, 'bob@example.com', 'Bob Brown'),
                (3, 'carol@big.example', 'Carol Clark');
            INSERT INTO messages(id, folder_id, subject, received_at, size, is_read, is_flagged, has_attachments) VALUES
                (1, 1, 'Invoice March',   {Utc("2026-03-05T12:00:00Z")}, 5000,    1, 0, 1),
                (2, 1, 'Lunch?',          {Utc("2026-05-10T09:00:00Z")}, 800,     0, 1, 0),
                (3, 2, 'Invoice April',   {Utc("2026-04-02T15:00:00Z")}, 4000000, 0, 0, 1);
            INSERT INTO message_addresses(message_id, address_id, kind, position) VALUES
                (1, 1, 0, 0), (1, 2, 1, 0),
                (2, 2, 0, 0), (2, 1, 1, 0),
                (3, 3, 0, 0), (3, 1, 1, 0);
            INSERT INTO attachments(message_id, blob_id, file_name, content_type, size) VALUES
                (3, NULL, 'quarterly-report.pdf', 'application/pdf', 123456);
            INSERT INTO messages_fts(rowid, subject, body_text, participants) VALUES
                (1, 'Invoice March', 'please find the invoice attached', 'alice@example.com bob@example.com'),
                (2, 'Lunch?', 'fancy lunch tomorrow at noon', 'bob@example.com alice@example.com'),
                (3, 'Invoice April', 'quarterly figures as discussed', 'carol@big.example alice@example.com');
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

    List<long> Run(string query)
    {
        var compiled = SqliteQueryCompiler.Compile(QueryParser.Parse(query));
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
    public void Field_terms_and_implicit_and()
    {
        Assert.Equal([1L], Run("from:alice"));
        Assert.Equal([1L, 3L], Run("has:attachment"));
        Assert.Equal([3L], Run("has:attachment is:unread"));
        Assert.Equal([2L], Run("is:flagged"));
    }

    [Fact]
    public void Negation_with_minus_and_not()
    {
        Assert.Equal([2L, 3L], Run("-from:alice"));
        Assert.Equal([2L, 3L], Run("NOT from:alice"));
        Assert.Equal([2L], Run("-has:attachment"));
    }

    [Fact]
    public void Or_groups_and_parentheses()
    {
        Assert.Equal([1L, 2L], Run("from:alice OR from:bob"));
        Assert.Equal([1L], Run("(from:alice OR from:carol) has:attachment is:read"));
    }

    [Fact]
    public void Quoted_values_keep_spaces()
    {
        Assert.Equal([1L], Run("subject:\"Invoice March\""));
        Assert.Empty(Run("subject:\"Invoice Marchx\""));
    }

    [Fact]
    public void Dates_absolute_and_relative()
    {
        Assert.Equal([2L, 3L], Run("after:2026-04-01"));
        Assert.Equal([1L], Run("before:2026-04-01"));
        Assert.Equal([3L], Run("on:2026-04-02"));
        Assert.Equal([1L, 2L, 3L], Run("after:2026"));
        // Relative dates resolve against today; all fixtures are in the past.
        Assert.Empty(Run("after:1d"));
    }

    [Fact]
    public void Sizes_with_units()
    {
        Assert.Equal([3L], Run("larger:1mb"));
        Assert.Equal([1L, 2L], Run("smaller:1mb"));
        Assert.Equal([1L, 3L], Run("larger:1kb"));
    }

    [Fact]
    public void Bare_words_are_full_text()
    {
        Assert.Equal([2L], Run("lunch"));
        Assert.Equal([1L], Run("invoice attached"));
        Assert.Equal([1L, 3L], Run("subject:invoice"));
    }

    [Fact]
    public void Folder_and_attachment_name_terms()
    {
        Assert.Equal([3L], Run("in:Archive"));
        Assert.Equal([3L], Run("filename:quarterly"));
    }

    [Fact]
    public void Wildcards_in_field_terms()
    {
        // Anchored glob: domain and suffix matching.
        Assert.Equal([3L], Run("from:*@big.example"));
        Assert.Equal([1L, 2L], Run("from:*@example.com"));
        Assert.Equal([3L], Run("filename:*.pdf"));
        Assert.Equal([1L, 3L], Run("subject:Invoice*"));
        // ? matches exactly one character.
        Assert.Equal([1L, 2L], Run("from:?ob@example.com OR from:alic?@example.com"));
        // Without wildcards, substring behaviour is unchanged.
        Assert.Equal([1L], Run("from:alice"));
    }

    [Fact]
    public void Escaped_wildcards_are_literal()
    {
        Exec("""
            INSERT INTO messages(id, folder_id, subject, received_at, size, is_read) VALUES
                (4, 1, 'Sale 5*3 offer', 1800000000, 900, 1),
                (5, 1, 'Sale 5x3 offer', 1800000000, 900, 1);
            """);
        // Wildcard patterns are anchored, so a leading * is needed to match mid-subject.
        Assert.Equal([4L, 5L], Run("subject:*5*3*"));
        // Escaped: the asterisk between 5 and 3 is literal, so only row 4 matches.
        Assert.Equal([4L], Run(@"subject:*5\*3*"));
        // Escaping also works in the plain (non-wildcard, substring) path.
        Assert.Equal([4L], Run(@"subject:5\*3"));

        // A doubled backslash is a literal backslash.
        Exec("""
            INSERT INTO messages(id, folder_id, subject, received_at, size, is_read) VALUES
                (6, 1, 'Path C:\temp\notes', 1800000000, 900, 1);
            """);
        Assert.Equal([6L], Run(@"subject:C:\\temp"));
        Assert.Equal([6L], Run(@"subject:*C:\\temp*"));
        // LIKE metacharacters in user input stay literal too.
        Assert.Empty(Run("subject:%"));
    }

    [Fact]
    public void Fts_prefix_matching()
    {
        Assert.Equal([1L, 3L], Run("invoic*"));
        Assert.Equal([2L], Run("lunc*"));
    }

    [Fact]
    public void Unknown_fields_and_bad_values_are_errors()
    {
        Assert.Throws<QueryParseException>(() => QueryParser.Parse("banana:split"));
        Assert.Throws<QueryParseException>(() => QueryParser.Parse("is:sideways"));
        Assert.Throws<QueryParseException>(() => QueryParser.Parse("after:nonsense"));
        Assert.Throws<QueryParseException>(() => QueryParser.Parse("from:"));
        Assert.Throws<QueryParseException>(() => QueryParser.Parse("(from:alice"));
        Assert.Throws<QueryParseException>(() => QueryParser.Parse("\"unterminated"));
    }

    [Fact]
    public void ParseOrText_falls_back_to_plain_search()
    {
        // A colon in ordinary text must not fail the search box.
        var node = QueryParser.ParseOrText("re: banana:split");
        Assert.IsType<TextNode>(node);
    }
}
