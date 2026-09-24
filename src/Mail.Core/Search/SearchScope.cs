namespace Mail.Core.Search;

/// <summary>
/// Where a search looks.
///
/// Separate from the query itself because they answer different questions: the
/// query is what to match, the scope is where to look. Keeping them apart means
/// the same query can be re-run wider without retyping it, which is the usual
/// way a search goes — nothing in this folder, so try the mailbox.
/// </summary>
public enum SearchScopeKind
{
    /// <summary>The open folder alone.</summary>
    Folder,

    /// <summary>The open folder and everything filed underneath it.</summary>
    FolderAndChildren,

    /// <summary>Every folder in the mailbox the open folder belongs to.</summary>
    Mailbox,

    /// <summary>
    /// Every mailbox belonging to the same account, including shared mailboxes
    /// reached through it.
    /// </summary>
    Account,

    /// <summary>Every mailbox in every account.</summary>
    Everything,

    /// <summary>A set of folders picked by hand, possibly across mailboxes.</summary>
    Selected,
}

/// <summary>
/// A resolved scope: the concrete folders to search, per mailbox.
///
/// Resolution happens once, before the query runs, so a search cannot drift
/// as the folder tree changes underneath it — and so the UI can say exactly
/// how many folders it is about to search rather than "some".
/// </summary>
/// <param name="Kind">What the user asked for, kept for display and for re-resolving.</param>
/// <param name="Folders">
/// Folder ids per mailbox key. An empty list for a mailbox means every folder
/// in it, which avoids enumerating thousands of ids for a whole-mailbox search.
/// </param>
public sealed record ResolvedScope(
    SearchScopeKind Kind,
    IReadOnlyDictionary<string, IReadOnlyList<long>> Folders)
{
    /// <summary>Mailboxes this search touches.</summary>
    public IEnumerable<string> Mailboxes => Folders.Keys;

    /// <summary>
    /// How many folders will be searched, or null when the answer is "all of
    /// them" for at least one mailbox and counting would need the tree.
    /// </summary>
    public int? FolderCount =>
        Folders.Values.Any(f => f.Count == 0)
            ? null
            : Folders.Values.Sum(f => f.Count);

    /// <summary>
    /// The SQL fragment restricting a query to this mailbox's folders, and the
    /// parameters it needs. Empty when the whole mailbox is in scope.
    ///
    /// Ids are bound as parameters rather than interpolated. They are integers
    /// from our own database rather than user input, so this is not the usual
    /// injection worry — but a query built by concatenation invites the next
    /// edit to concatenate something that did come from outside.
    /// </summary>
    public (string Sql, IReadOnlyDictionary<string, object> Parameters) FolderFilter(
        string mailboxKey, string prefix = "@sf")
    {
        if (!Folders.TryGetValue(mailboxKey, out var ids) || ids.Count == 0)
            return ("", new Dictionary<string, object>());

        var parameters = new Dictionary<string, object>();
        var names = new List<string>(ids.Count);
        for (var i = 0; i < ids.Count; i++)
        {
            var name = $"{prefix}{i}";
            names.Add(name);
            parameters[name] = ids[i];
        }
        return ($"m.folder_id IN ({string.Join(", ", names)})", parameters);
    }

    /// <summary>A scope covering one mailbox entirely.</summary>
    public static ResolvedScope WholeMailbox(string mailboxKey, SearchScopeKind kind) =>
        new(kind, new Dictionary<string, IReadOnlyList<long>>
        {
            [mailboxKey] = Array.Empty<long>(),
        });
}

/// <summary>
/// Turns a scope choice into the folders it covers.
///
/// The caller supplies the tree, because the folder hierarchy lives in the app
/// rather than here — this stays testable without a database or a UI.
/// </summary>
public static class ScopeResolver
{
    /// <summary>
    /// One folder as the resolver sees it: enough to walk the tree and nothing
    /// more, so the app's own view model does not have to be visible here.
    /// </summary>
    /// <param name="MailboxKey">Identifies the mailbox the folder belongs to.</param>
    /// <param name="AccountKey">Groups mailboxes that share an account.</param>
    public readonly record struct Folder(
        string MailboxKey,
        string AccountKey,
        long Id,
        long? ParentId);

    /// <summary>
    /// Resolves a scope against the known folders.
    /// </summary>
    /// <param name="kind">What the user chose.</param>
    /// <param name="all">Every folder in every open mailbox.</param>
    /// <param name="currentMailbox">The mailbox holding the open folder.</param>
    /// <param name="currentFolder">The open folder, if one is open.</param>
    /// <param name="selected">Hand-picked folders, for <see cref="SearchScopeKind.Selected"/>.</param>
    public static ResolvedScope Resolve(
        SearchScopeKind kind,
        IReadOnlyList<Folder> all,
        string? currentMailbox,
        long? currentFolder,
        IReadOnlyCollection<(string MailboxKey, long FolderId)>? selected = null)
    {
        switch (kind)
        {
            case SearchScopeKind.Folder:
                if (currentMailbox is null || currentFolder is null) return Empty(kind);
                return Single(kind, currentMailbox, [currentFolder.Value]);

            case SearchScopeKind.FolderAndChildren:
            {
                if (currentMailbox is null || currentFolder is null) return Empty(kind);
                var ids = Descendants(all, currentMailbox, currentFolder.Value);
                return Single(kind, currentMailbox, ids);
            }

            case SearchScopeKind.Mailbox:
                if (currentMailbox is null) return Empty(kind);
                return ResolvedScope.WholeMailbox(currentMailbox, kind);

            case SearchScopeKind.Account:
            {
                if (currentMailbox is null) return Empty(kind);
                var account = all.FirstOrDefault(f => f.MailboxKey == currentMailbox).AccountKey;
                // Shared mailboxes are reached through the account that holds
                // rights on them, so they belong to this scope too.
                var mailboxes = all
                    .Where(f => f.AccountKey == account)
                    .Select(f => f.MailboxKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase);
                return AllOf(kind, mailboxes);
            }

            case SearchScopeKind.Everything:
                return AllOf(kind, all
                    .Select(f => f.MailboxKey)
                    .Distinct(StringComparer.OrdinalIgnoreCase));

            case SearchScopeKind.Selected:
            {
                if (selected is null || selected.Count == 0) return Empty(kind);
                var byMailbox = selected
                    .GroupBy(s => s.MailboxKey, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(
                        g => g.Key,
                        g => (IReadOnlyList<long>)g.Select(s => s.FolderId).Distinct().ToList(),
                        StringComparer.OrdinalIgnoreCase);
                return new ResolvedScope(kind, byMailbox);
            }

            default:
                return Empty(kind);
        }
    }

    /// <summary>
    /// A folder and everything beneath it. Iterative rather than recursive: a
    /// corrupt tree with a parent cycle would otherwise overflow the stack
    /// instead of returning something usable.
    /// </summary>
    static List<long> Descendants(IReadOnlyList<Folder> all, string mailboxKey, long root)
    {
        var children = all
            .Where(f => string.Equals(f.MailboxKey, mailboxKey, StringComparison.OrdinalIgnoreCase))
            .Where(f => f.ParentId is not null)
            .GroupBy(f => f.ParentId!.Value)
            .ToDictionary(g => g.Key, g => g.Select(f => f.Id).ToList());

        var found = new List<long> { root };
        var seen = new HashSet<long> { root };
        var pending = new Queue<long>();
        pending.Enqueue(root);
        while (pending.Count > 0)
        {
            var next = pending.Dequeue();
            if (!children.TryGetValue(next, out var kids)) continue;
            foreach (var kid in kids)
                if (seen.Add(kid))
                {
                    found.Add(kid);
                    pending.Enqueue(kid);
                }
        }
        return found;
    }

    static ResolvedScope Single(SearchScopeKind kind, string mailboxKey, IReadOnlyList<long> ids) =>
        new(kind, new Dictionary<string, IReadOnlyList<long>>(StringComparer.OrdinalIgnoreCase)
        {
            [mailboxKey] = ids,
        });

    static ResolvedScope AllOf(SearchScopeKind kind, IEnumerable<string> mailboxes) =>
        new(kind, mailboxes.ToDictionary(
            m => m,
            _ => (IReadOnlyList<long>)Array.Empty<long>(),
            StringComparer.OrdinalIgnoreCase));

    static ResolvedScope Empty(SearchScopeKind kind) =>
        new(kind, new Dictionary<string, IReadOnlyList<long>>(StringComparer.OrdinalIgnoreCase));

    /// <summary>How a scope reads in the UI, so the label and the behaviour cannot drift.</summary>
    public static string Describe(SearchScopeKind kind) => kind switch
    {
        SearchScopeKind.Folder => "This folder",
        SearchScopeKind.FolderAndChildren => "This folder and subfolders",
        SearchScopeKind.Mailbox => "This mailbox",
        SearchScopeKind.Account => "This account",
        SearchScopeKind.Everything => "All mailboxes",
        SearchScopeKind.Selected => "Selected folders…",
        _ => kind.ToString(),
    };
}
