using Mail.Core.Search;

namespace Mail.Tests;

public sealed class ConversationGroupingTests
{
    static readonly DateTimeOffset Base = new(2026, 9, 1, 9, 0, 0, TimeSpan.Zero);

    static ConversationGrouping.Item<string> Msg(
        string key, string id, string label, int minutes, params string[] references) =>
        new(key, id, references, Base.AddMinutes(minutes), label);

    static List<string> Labels(IEnumerable<ConversationGrouping.Placed<string>> rows) =>
        [.. rows.Select(r => r.Payload)];

    [Fact]
    public void A_reply_sits_under_what_it_answers()
    {
        // The point of the feature. Date order alone would put these side by
        // side with no indication that one answers the other.
        var rows = ConversationGrouping.Group([
            Msg("c1", "m1", "original", 0),
            Msg("c1", "m2", "reply", 10, "m1"),
        ]);

        Assert.Equal(["original", "reply"], Labels(rows));
        Assert.Equal(0, rows[0].Depth);
        Assert.Equal(1, rows[1].Depth);
    }

    [Fact]
    public void Nested_replies_nest()
    {
        var rows = ConversationGrouping.Group([
            Msg("c1", "m1", "original", 0),
            Msg("c1", "m2", "reply", 10, "m1"),
            Msg("c1", "m3", "reply to reply", 20, "m1", "m2"),
        ]);

        Assert.Equal([0, 1, 2], [.. rows.Select(r => r.Depth)]);
    }

    [Fact]
    public void A_reply_attaches_to_its_nearest_known_ancestor()
    {
        // References runs oldest to newest. With the direct parent missing, the
        // reply should attach to the closest message we do hold rather than to
        // the start of the thread.
        var rows = ConversationGrouping.Group([
            Msg("c1", "m1", "original", 0),
            Msg("c1", "m2", "reply", 10, "m1"),
            // answers m3, which never arrived; m2 is the nearest we have
            Msg("c1", "m4", "later", 30, "m1", "m2", "m3"),
        ]);

        var later = rows.Single(r => r.Payload == "later");
        Assert.Equal(2, later.Depth);
    }

    [Fact]
    public void Threads_are_ordered_by_their_newest_message()
    {
        // A conversation replied to today belongs above one that ended last
        // week, even though it started earlier.
        var rows = ConversationGrouping.Group([
            Msg("old", "a1", "old thread", 0),
            Msg("old", "a2", "old reply", 5, "a1"),
            Msg("new", "b1", "new thread", 100),
        ]);

        Assert.Equal("new thread", rows[0].Payload);
    }

    [Fact]
    public void Within_a_thread_the_oldest_comes_first()
    {
        // Even when the list is newest-first overall, a thread has to read top
        // to bottom or it is not a thread.
        var rows = ConversationGrouping.Group([
            Msg("c1", "m2", "reply", 10, "m1"),
            Msg("c1", "m1", "original", 0),
        ], newestFirst: true);

        Assert.Equal(["original", "reply"], Labels(rows));
    }

    [Fact]
    public void Out_of_order_arrival_still_threads()
    {
        // Sync does not guarantee the parent arrives first.
        var rows = ConversationGrouping.Group([
            Msg("c1", "m3", "third", 20, "m1", "m2"),
            Msg("c1", "m1", "first", 0),
            Msg("c1", "m2", "second", 10, "m1"),
        ]);

        Assert.Equal(["first", "second", "third"], Labels(rows));
        Assert.Equal([0, 1, 2], [.. rows.Select(r => r.Depth)]);
    }

    [Fact]
    public void An_orphan_reply_does_not_start_a_second_thread()
    {
        // Holding a reply whose parent was never received is routine — it must
        // appear, but the conversation still has exactly one root.
        var rows = ConversationGrouping.Group([
            Msg("c1", "m2", "orphan reply", 10, "missing"),
            Msg("c1", "m3", "another orphan", 20, "missing"),
        ]);

        Assert.Equal(2, rows.Count);
        Assert.Single(rows, r => r.IsThreadRoot);
    }

    [Fact]
    public void Messages_with_no_key_stay_separate()
    {
        // Lumping every unkeyed message into one thread would be worse than not
        // grouping at all.
        var rows = ConversationGrouping.Group<string>([
            new(null, "x1", [], Base, "loose one"),
            new(null, "x2", [], Base.AddMinutes(5), "loose two"),
        ]);

        Assert.Equal(2, rows.Count);
        Assert.All(rows, r => Assert.True(r.IsThreadRoot));
        Assert.All(rows, r => Assert.Equal(0, r.Depth));
        Assert.All(rows, r => Assert.Null(r.Key));
    }

    [Fact]
    public void Counts_describe_what_collapsing_hides()
    {
        var rows = ConversationGrouping.Group([
            Msg("c1", "m1", "root", 0),
            Msg("c1", "m2", "child a", 10, "m1"),
            Msg("c1", "m3", "grandchild", 20, "m1", "m2"),
            Msg("c1", "m4", "child b", 30, "m1"),
        ]);

        var root = rows[0];
        Assert.Equal(2, root.ChildCount);        // a and b
        Assert.Equal(3, root.DescendantCount);   // a, grandchild, b
    }

    [Fact]
    public void A_reference_cycle_terminates()
    {
        // References come from whoever sent the mail, so a loop is possible and
        // would otherwise mean a row that is its own ancestor.
        var rows = ConversationGrouping.Group([
            Msg("c1", "m1", "one", 0, "m2"),
            Msg("c1", "m2", "two", 10, "m1"),
        ]);

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void A_message_referencing_itself_is_not_its_own_parent()
    {
        var rows = ConversationGrouping.Group([
            Msg("c1", "m1", "self", 0, "m1"),
        ]);

        Assert.Single(rows);
        Assert.Equal(0, rows[0].Depth);
    }

    [Fact]
    public void Collapsing_hides_the_whole_subtree()
    {
        var rows = ConversationGrouping.Group([
            Msg("c1", "m1", "root", 0),
            Msg("c1", "m2", "child", 10, "m1"),
            Msg("c1", "m3", "grandchild", 20, "m1", "m2"),
            Msg("other", "z1", "unrelated", 30),
        ]);

        var visible = ConversationGrouping.ApplyCollapse(rows, key => key == "c1");

        Assert.Contains("root", Labels(visible));
        Assert.DoesNotContain("child", Labels(visible));
        Assert.DoesNotContain("grandchild", Labels(visible));
        // Collapsing one conversation must not touch another.
        Assert.Contains("unrelated", Labels(visible));
    }

    [Fact]
    public void Expanding_everything_shows_everything()
    {
        var rows = ConversationGrouping.Group([
            Msg("c1", "m1", "root", 0),
            Msg("c1", "m2", "child", 10, "m1"),
        ]);

        var visible = ConversationGrouping.ApplyCollapse(rows, _ => false);

        Assert.Equal(rows.Count, visible.Count);
    }

    [Fact]
    public void Grouping_never_loses_a_message()
    {
        // The invariant that matters most: a mail client that hides mail while
        // grouping it is worse than one that does not group.
        ConversationGrouping.Item<string>[] items = [
            Msg("c1", "m1", "a", 0),
            Msg("c1", "m2", "b", 10, "m1"),
            Msg("c2", "m3", "c", 20),
            new(null, "m4", [], Base.AddMinutes(30), "d"),
            Msg("c1", "m5", "e", 40, "nope"),
        ];

        var rows = ConversationGrouping.Group(items);

        Assert.Equal(items.Length, rows.Count);
        Assert.Equal(
            [.. items.Select(i => i.Payload).Order()],
            [.. rows.Select(r => r.Payload).Order()]);
    }
}
