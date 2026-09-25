namespace Mail.Core.Search;

/// <summary>
/// Arranges messages into conversations for a flat list.
///
/// The grid is a DataGrid, which has no notion of a tree, so grouping is done
/// by flattening: a conversation becomes a run of consecutive rows, each
/// carrying a depth and an expanded flag, and collapsing one simply drops its
/// descendants from the list. That keeps sorting, column layout and selection
/// working exactly as they do ungrouped, which a real tree control would not.
///
/// The parent/child shape comes from the reference chain that the store already
/// records, so a reply sits under the message it answers rather than merely
/// beside it in date order.
/// </summary>
public static class ConversationGrouping
{
    /// <summary>
    /// One message, as grouping needs to see it. Deliberately minimal so the
    /// app's row type does not have to be visible here.
    /// </summary>
    /// <param name="Key">Identifies the conversation; messages sharing it thread together.</param>
    /// <param name="MessageId">Its own identity, referenced by replies.</param>
    /// <param name="References">Message ids this one replies to, oldest first.</param>
    public readonly record struct Item<T>(
        string? Key,
        string? MessageId,
        IReadOnlyList<string> References,
        DateTimeOffset? Received,
        T Payload);

    /// <summary>
    /// A message placed in a thread.
    /// </summary>
    /// <param name="Depth">0 for the root; each reply one deeper than its parent.</param>
    /// <param name="ChildCount">Direct replies, so a collapsed row can say what it hides.</param>
    /// <param name="DescendantCount">Everything beneath it, which is what collapsing removes.</param>
    /// <param name="IsThreadRoot">True for the first row of a conversation.</param>
    public sealed record Placed<T>(
        T Payload,
        int Depth,
        int ChildCount,
        int DescendantCount,
        bool IsThreadRoot,
        string? Key);

    /// <summary>
    /// Groups messages into threads and returns them in display order:
    /// conversations ordered by their newest message, and within a conversation
    /// each reply directly after the message it answers.
    /// </summary>
    /// <param name="items">The messages to arrange.</param>
    /// <param name="newestFirst">
    /// Whether conversations are ordered newest first. Only the order of
    /// conversations changes; a thread always reads parent before reply,
    /// because a reply above the message it answers is not a thread.
    /// </param>
    public static List<Placed<T>> Group<T>(
        IReadOnlyList<Item<T>> items,
        bool newestFirst = true)
    {
        // A message with no key threads with nothing; give it one of its own so
        // it still appears rather than being dropped or lumped together with
        // every other keyless message.
        var keyed = items
            .Select((item, index) => (Item: item, Index: index,
                Key: string.IsNullOrEmpty(item.Key) ? $"\u0000solo-{index}" : item.Key))
            .ToList();

        var threads = keyed
            .GroupBy(x => x.Key, StringComparer.Ordinal)
            .Select(group => new
            {
                Key = group.Key,
                Members = group.ToList(),
                // A conversation sorts by its newest message: a thread that was
                // replied to today belongs at the top even if it began last year.
                Newest = group.Max(x => x.Item.Received ?? DateTimeOffset.MinValue),
            })
            .ToList();

        threads = newestFirst
            ? [.. threads.OrderByDescending(t => t.Newest)]
            : [.. threads.OrderBy(t => t.Newest)];

        var placed = new List<Placed<T>>(items.Count);
        foreach (var thread in threads)
            AppendThread(placed, thread.Members.Select(m => m.Item).ToList(), thread.Key);
        return placed;
    }

    static void AppendThread<T>(List<Placed<T>> into, List<Item<T>> members, string key)
    {
        var displayKey = key.StartsWith('\u0000') ? null : key;

        if (members.Count == 1)
        {
            into.Add(new Placed<T>(members[0].Payload, 0, 0, 0, true, displayKey));
            return;
        }

        // Index by message id so a reply can find what it answers. Duplicates
        // are possible when the same message appears in two folders; the first
        // wins, which keeps the shape stable rather than throwing.
        var byId = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        for (var i = 0; i < members.Count; i++)
        {
            var id = members[i].MessageId;
            if (!string.IsNullOrEmpty(id)) byId.TryAdd(id, i);
        }

        var parent = new int?[members.Count];
        for (var i = 0; i < members.Count; i++)
        {
            // The last reference that is present is the nearest ancestor we
            // hold: References runs oldest to newest, so walking backwards
            // finds the closest one rather than the thread's origin.
            var references = members[i].References;
            for (var r = references.Count - 1; r >= 0; r--)
            {
                if (!byId.TryGetValue(references[r], out var candidate)) continue;
                if (candidate == i) continue;
                parent[i] = candidate;
                break;
            }
        }

        BreakCycles(parent);

        var children = new List<int>[members.Count];
        var roots = new List<int>();
        for (var i = 0; i < members.Count; i++)
        {
            if (parent[i] is int p)
                (children[p] ??= []).Add(i);
            else
                roots.Add(i);
        }

        // Oldest first within the thread: a conversation reads top to bottom.
        static int ByDate(List<Item<T>> m, int a, int b) =>
            Nullable.Compare(m[a].Received, m[b].Received);

        roots.Sort((a, b) => ByDate(members, a, b));
        foreach (var list in children)
            list?.Sort((a, b) => ByDate(members, a, b));

        var descendants = new int[members.Count];
        foreach (var root in roots) CountDescendants(root, children, descendants);

        // Only the conversation's first row is its root. Further roots mean we
        // hold replies whose parent never arrived, which is common — they are
        // shown at depth 0 but must not start a second thread in the list.
        var isFirstRow = true;
        foreach (var root in roots)
        {
            Walk(root, 0);
        }

        void Walk(int index, int depth)
        {
            into.Add(new Placed<T>(
                members[index].Payload,
                depth,
                children[index]?.Count ?? 0,
                descendants[index],
                isFirstRow,
                displayKey));
            isFirstRow = false;
            if (children[index] is not { } kids) return;
            foreach (var child in kids) Walk(child, depth + 1);
        }
    }

    /// <summary>
    /// Counts everything beneath each node. Iterative, because a malformed
    /// thread could otherwise recurse as deep as it is long.
    /// </summary>
    static void CountDescendants(int root, List<int>[] children, int[] into)
    {
        var order = new List<int>();
        var stack = new Stack<int>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var next = stack.Pop();
            order.Add(next);
            if (children[next] is not { } kids) continue;
            foreach (var kid in kids) stack.Push(kid);
        }
        // Deepest first, so a node's children are counted before it is.
        for (var i = order.Count - 1; i >= 0; i--)
        {
            var node = order[i];
            var total = 0;
            if (children[node] is { } kids)
                foreach (var kid in kids) total += 1 + into[kid];
            into[node] = total;
        }
    }

    /// <summary>
    /// Detaches any node whose parent chain loops back to it.
    ///
    /// References are supplied by whoever sent the mail, so a cycle is not
    /// impossible — and a cycle here would mean a row that is its own ancestor,
    /// which walks forever.
    /// </summary>
    static void BreakCycles(int?[] parent)
    {
        for (var i = 0; i < parent.Length; i++)
        {
            var slow = i;
            var fast = i;
            while (true)
            {
                if (parent[fast] is not int f1) break;
                if (parent[f1] is not int f2) break;
                fast = f2;
                slow = parent[slow]!.Value;
                if (slow != fast) continue;
                // In a loop: cut this node free and let it start its own branch.
                parent[i] = null;
                break;
            }
        }
    }

    /// <summary>
    /// Hides the descendants of collapsed threads.
    /// </summary>
    /// <param name="rows">Rows as <see cref="Group"/> produced them.</param>
    /// <param name="isCollapsed">Whether the conversation with this key is collapsed.</param>
    public static List<Placed<T>> ApplyCollapse<T>(
        IReadOnlyList<Placed<T>> rows,
        Func<string?, bool> isCollapsed)
    {
        var visible = new List<Placed<T>>(rows.Count);
        var hidingBelow = int.MaxValue;
        foreach (var row in rows)
        {
            // Everything deeper than a collapsed row is hidden, until a row at
            // that depth or shallower ends the run.
            if (row.Depth > hidingBelow) continue;
            hidingBelow = int.MaxValue;
            visible.Add(row);
            if (row.ChildCount > 0 && isCollapsed(row.Key)) hidingBelow = row.Depth;
        }
        return visible;
    }
}
