using System.Security.Cryptography;
using System.Text;
using Mail.Storage.Database;
using Microsoft.Data.Sqlite;

namespace Mail.Tests;

public sealed class DraftStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));
    readonly SqliteConnection _appDb;
    readonly DraftStore _drafts;

    public DraftStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _appDb = AppDatabase.Open(
            Path.Combine(_dir, "app.db"), RandomNumberGenerator.GetBytes(32));
        _drafts = new DraftStore(_appDb);
    }

    public void Dispose()
    {
        _appDb.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    static DraftStore.DraftRecord Sample(long id = 0, string subject = "Hello") => new(
        id, "john@example.com", "someone@example.com", "", "", subject,
        "Body text", null, IsRich: false, null, null, [], DateTimeOffset.UtcNow);

    [Fact]
    public void Saving_returns_an_id_and_the_draft_reads_back()
    {
        var id = _drafts.Save(Sample());
        Assert.True(id > 0);

        var loaded = _drafts.Get(id);
        Assert.NotNull(loaded);
        Assert.Equal("Hello", loaded!.Subject);
        Assert.Equal("someone@example.com", loaded.To);
        Assert.Equal("Body text", loaded.Body);
    }

    [Fact]
    public void Saving_with_an_id_updates_rather_than_duplicating()
    {
        // Autosave runs repeatedly while typing; each pass must replace the row
        // rather than leave a trail of near-identical drafts behind.
        var id = _drafts.Save(Sample());
        for (var i = 0; i < 5; i++)
            _drafts.Save(Sample(id, $"Revision {i}"));

        Assert.Equal(1, _drafts.Count());
        Assert.Equal("Revision 4", _drafts.Get(id)!.Subject);
    }

    [Fact]
    public void Attachments_survive_a_round_trip()
    {
        var content = Encoding.UTF8.GetBytes("attachment bytes");
        var id = _drafts.Save(Sample() with
        {
            Attachments = [new DraftStore.DraftFile("notes.txt", "text/plain", content)],
        });

        var loaded = _drafts.Get(id)!;
        var file = Assert.Single(loaded.Attachments);
        Assert.Equal("notes.txt", file.FileName);
        Assert.Equal(content, file.Content);
    }

    [Fact]
    public void Threading_headers_are_kept_so_a_resumed_reply_still_threads()
    {
        var id = _drafts.Save(Sample() with
        {
            InReplyTo = "<parent@example.com>",
            References = "<root@example.com> <parent@example.com>",
        });

        var loaded = _drafts.Get(id)!;
        Assert.Equal("<parent@example.com>", loaded.InReplyTo);
        Assert.Contains("<root@example.com>", loaded.References);
    }

    [Fact]
    public void Html_and_rich_flag_round_trip()
    {
        var id = _drafts.Save(Sample() with
        {
            HtmlBody = "<p>Hello</p>",
            IsRich = true,
        });

        var loaded = _drafts.Get(id)!;
        Assert.True(loaded.IsRich);
        Assert.Equal("<p>Hello</p>", loaded.HtmlBody);
    }

    [Fact]
    public void Deleting_removes_only_that_draft()
    {
        var first = _drafts.Save(Sample(subject: "First"));
        var second = _drafts.Save(Sample(subject: "Second"));

        _drafts.Delete(first);

        Assert.Null(_drafts.Get(first));
        Assert.NotNull(_drafts.Get(second));
        Assert.Equal(1, _drafts.Count());
    }

    [Fact]
    public void Listing_is_newest_first()
    {
        var older = _drafts.Save(Sample(subject: "Older"));
        // updated_at has one-second resolution, so force a distinct timestamp
        // rather than relying on wall-clock separation within a test.
        using (var cmd = _appDb.CreateCommand())
        {
            cmd.CommandText = "UPDATE drafts SET updated_at = updated_at - 60 WHERE id = @i;";
            cmd.Parameters.AddWithValue("@i", older);
            cmd.ExecuteNonQuery();
        }
        _drafts.Save(Sample(subject: "Newer"));

        var listed = _drafts.List();
        Assert.Equal("Newer", listed[0].Subject);
        Assert.Equal("Older", listed[1].Subject);
    }

    [Fact]
    public void Display_is_readable_when_the_subject_is_empty()
    {
        var draft = Sample(subject: "");
        Assert.Contains("(no subject)", draft.Display);
        Assert.Contains("someone@example.com", draft.Display);
    }

    [Fact]
    public void Unreadable_attachments_do_not_cost_the_message_text()
    {
        // Losing the body because the attachment JSON is corrupt would defeat
        // the point of saving the draft at all.
        var id = _drafts.Save(Sample());
        using (var cmd = _appDb.CreateCommand())
        {
            cmd.CommandText = "UPDATE drafts SET attachments = 'not json' WHERE id = @i;";
            cmd.Parameters.AddWithValue("@i", id);
            cmd.ExecuteNonQuery();
        }

        var loaded = _drafts.Get(id);
        Assert.NotNull(loaded);
        Assert.Equal("Body text", loaded!.Body);
        Assert.Empty(loaded.Attachments);
    }
}
