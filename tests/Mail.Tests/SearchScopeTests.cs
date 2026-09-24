using Mail.Core.Search;

namespace Mail.Tests;

public sealed class SearchScopeTests
{
    // A small tree spanning two accounts, one of which has a shared mailbox:
    //   work   (account A)  Inbox 1 -> Projects 2 -> Archive 3,  Sent 4
    //   shared (account A)  Inbox 10
    //   home   (account B)  Inbox 20
    static readonly ScopeResolver.Folder[] Tree =
    [
        new("work",   "A",  1, null),
        new("work",   "A",  2, 1),
        new("work",   "A",  3, 2),
        new("work",   "A",  4, null),
        new("shared", "A", 10, null),
        new("home",   "B", 20, null),
    ];

    [Fact]
    public void Folder_scope_is_just_the_open_folder()
    {
        var scope = ScopeResolver.Resolve(SearchScopeKind.Folder, Tree, "work", 1);

        Assert.Equal(1, scope.FolderCount);
        Assert.Equal([1L], scope.Folders["work"]);
    }

    [Fact]
    public void Folder_and_children_reaches_grandchildren()
    {
        // The reason this is a separate scope from "this folder": mail filed
        // two levels down is still in the folder as far as the user is
        // concerned, and a one-level walk would quietly miss it.
        var scope = ScopeResolver.Resolve(SearchScopeKind.FolderAndChildren, Tree, "work", 1);

        Assert.Equal([1L, 2L, 3L], [.. scope.Folders["work"].Order()]);
        Assert.DoesNotContain(4L, scope.Folders["work"]);   // Sent is a sibling
    }

    [Fact]
    public void Mailbox_scope_does_not_enumerate_folders()
    {
        // An empty list means "everything here". Listing every id would make a
        // query with thousands of parameters for no benefit.
        var scope = ScopeResolver.Resolve(SearchScopeKind.Mailbox, Tree, "work", 1);

        Assert.Empty(scope.Folders["work"]);
        Assert.Null(scope.FolderCount);
        Assert.Single(scope.Folders);
    }

    [Fact]
    public void Account_scope_includes_shared_mailboxes()
    {
        // A shared mailbox is reached through the account that holds rights on
        // it, so "this account" that skipped it would surprise.
        var scope = ScopeResolver.Resolve(SearchScopeKind.Account, Tree, "work", 1);

        Assert.Contains("work", scope.Mailboxes);
        Assert.Contains("shared", scope.Mailboxes);
        Assert.DoesNotContain("home", scope.Mailboxes);
    }

    [Fact]
    public void Everything_spans_every_account()
    {
        var scope = ScopeResolver.Resolve(SearchScopeKind.Everything, Tree, "work", 1);

        Assert.Equal(3, scope.Mailboxes.Count());
        Assert.Contains("home", scope.Mailboxes);
    }

    [Fact]
    public void Selected_folders_can_span_mailboxes()
    {
        var scope = ScopeResolver.Resolve(
            SearchScopeKind.Selected, Tree, "work", 1,
            selected: [("work", 4), ("home", 20)]);

        Assert.Equal([4L], scope.Folders["work"]);
        Assert.Equal([20L], scope.Folders["home"]);
        Assert.Equal(2, scope.FolderCount);
    }

    [Fact]
    public void A_parent_cycle_terminates()
    {
        // Should not happen, but a corrupt tree must produce a usable answer
        // rather than a stack overflow that takes the app with it.
        ScopeResolver.Folder[] cyclic =
        [
            new("m", "A", 1, 2),
            new("m", "A", 2, 1),
        ];

        var scope = ScopeResolver.Resolve(SearchScopeKind.FolderAndChildren, cyclic, "m", 1);

        Assert.Equal([1L, 2L], [.. scope.Folders["m"].Order()]);
    }

    [Fact]
    public void Folder_filter_binds_ids_as_parameters()
    {
        // Concatenating ids would work today, since they are our own integers,
        // and would be the line a later edit reuses for something that is not.
        var scope = ScopeResolver.Resolve(SearchScopeKind.FolderAndChildren, Tree, "work", 1);
        var (sql, parameters) = scope.FolderFilter("work");

        Assert.Contains("m.folder_id IN", sql);
        Assert.Equal(3, parameters.Count);
        Assert.DoesNotContain("1, 2, 3", sql);       // ids are not inlined
        Assert.All(parameters.Values, v => Assert.IsType<long>(v));
    }

    [Fact]
    public void Whole_mailbox_filter_is_empty()
    {
        // No restriction is the correct SQL for "the whole mailbox"; a filter
        // of "1=1" would work but reads as a leftover.
        var scope = ScopeResolver.Resolve(SearchScopeKind.Mailbox, Tree, "work", 1);
        var (sql, parameters) = scope.FolderFilter("work");

        Assert.Equal("", sql);
        Assert.Empty(parameters);
    }

    [Fact]
    public void Missing_context_yields_nothing_rather_than_everything()
    {
        // Failing open would search every mailbox when the user asked for one
        // folder — slow, and not what was asked for.
        var scope = ScopeResolver.Resolve(SearchScopeKind.Folder, Tree, null, null);

        Assert.Empty(scope.Mailboxes);
    }

    [Theory]
    [InlineData(SearchScopeKind.Folder)]
    [InlineData(SearchScopeKind.FolderAndChildren)]
    [InlineData(SearchScopeKind.Mailbox)]
    [InlineData(SearchScopeKind.Account)]
    [InlineData(SearchScopeKind.Everything)]
    [InlineData(SearchScopeKind.Selected)]
    public void Every_scope_has_a_label(SearchScopeKind kind)
    {
        // The dropdown is generated from the enum, so a new scope without a
        // label would show its C# name to the user.
        var label = ScopeResolver.Describe(kind);

        Assert.False(string.IsNullOrWhiteSpace(label));
        Assert.NotEqual(kind.ToString(), label);
    }
}
