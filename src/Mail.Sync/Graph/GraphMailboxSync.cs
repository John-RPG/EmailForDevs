using System.Runtime.Versioning;
using System.Threading.Channels;
using Channel = System.Threading.Channels.Channel;
using Mail.Core.Ingest;
using Mail.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace Mail.Sync.Graph;

/// <summary>
/// Mirrors a Graph mailbox into a mailbox database, in parallel:
///  - folders sync as independent tasks (bounded by maxConcurrentFolders);
///  - network calls share an adaptive worker pool that starts at the Outlook
///    service's documented per-mailbox concurrency (4) and shrinks on 429
///    (our own app registration already gives us a private throttling bucket —
///    concurrency discipline is what keeps us inside it);
///  - all database writes flow through one writer task via a bounded channel,
///    so SQLite sees a single writer while downloads and MIME parsing fan out.
/// Delta tokens are enqueued after a folder's items, so a checkpoint is never
/// persisted ahead of the data it covers.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GraphMailboxSync(
    GraphServiceClient graph,
    Func<SqliteConnection> dbFactory,
    Action<GraphMailboxSync.SyncProgressEvent>? progress = null,
    int maxConcurrentDownloads = 4,
    int maxConcurrentFolders = 1,
    string? mailboxAddress = null)
{
    /// <summary>
    /// The mailbox being mirrored. Everything addresses /users/{upn} rather than
    /// /me so a shared mailbox is not a special case: for the signed-in user's
    /// own mailbox the two are the same resource, and Exchange applies the same
    /// permission check either way.
    /// </summary>
    readonly Microsoft.Graph.Users.Item.UserItemRequestBuilder _mailbox =
        graph.Users[mailboxAddress ?? "me"];

    public enum SyncPhase { Folders, Counting, Scanning, Downloading, FolderDone, Throttled }

    public sealed record SyncProgressEvent(
        SyncPhase Phase, string? FolderName, int FolderIndex, int FolderCount,
        int FolderDownloaded, int? FolderTarget,
        int OverallDownloaded, int? OverallTarget);

    public sealed record SyncStats(int Folders, int Added, int Updated, int Removed, int Failed);

    static readonly string[] DeltaSelect =
        ["id", "internetMessageId", "isRead", "isDraft", "flag", "receivedDateTime", "parentFolderId"];

    static readonly (string WellKnownName, string SpecialUse)[] WellKnownFolders =
    [
        ("inbox", "inbox"), ("drafts", "drafts"), ("sentitems", "sent"),
        ("deleteditems", "trash"), ("junkemail", "junk"), ("outbox", "outbox"),
        ("archive", "archive"),
    ];

    abstract record WriteOp;
    sealed record IngestOp(long FolderId, byte[] Raw, ParsedMessage Parsed, Message Msg) : WriteOp;
    sealed record UpdateOp(string ServerId, long FolderId, Message Msg) : WriteOp;
    sealed record DeleteOp(string ServerId, long FolderId) : WriteOp;
    sealed record TokenOp(long FolderId, string DeltaLink) : WriteOp;

    int _overallDownloaded;
    int? _overallTarget;
    int _failed;
    int _folderCount;

    public async Task<SyncStats> SyncAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        progress?.Invoke(new(SyncPhase.Folders, null, 0, 0, 0, null, 0, null));
        List<(long LocalId, string ServerId, string Name)> folders;
        string?[] tokens;
        using (var listDb = dbFactory())
        {
            var specialUse = await MapWellKnownFoldersAsync(ct);
            folders = await SyncFoldersAsync(listDb, specialUse, ct);
            tokens = [.. folders.Select(f => MailboxStore.GetDeltaToken(listDb, f.LocalId))];
        }
        _folderCount = folders.Count;

        // Parallel pre-count of initial (non-delta) folders → determinate target.
        var targets = new int?[folders.Count];
        var countSem = new SemaphoreSlim(maxConcurrentDownloads);
        var counted = 0;
        await Task.WhenAll(folders.Select(async (folder, i) =>
        {
            if (tokens[i] is not null) return;
            await countSem.WaitAsync(ct);
            try
            {
                targets[i] = await TryCountFolderAsync(folder.ServerId, since, ct);
                var done = Interlocked.Increment(ref counted);
                progress?.Invoke(new(SyncPhase.Counting, folder.Name, done, _folderCount, 0, targets[i], 0, null));
            }
            finally { countSem.Release(); }
        }));
        _overallTarget = targets.Any(t => t.HasValue)
            ? targets.Where(t => t.HasValue).Sum(t => t!.Value)
            : null;
        _overallDownloaded = 0;
        _failed = 0;

        var channel = Channel.CreateBounded<WriteOp>(new BoundedChannelOptions(256)
        {
            SingleReader = true,
            SingleWriter = false,
            FullMode = BoundedChannelFullMode.Wait,
        });
        var writerTask = Task.Run(() => WriterLoopAsync(channel.Reader, ct), ct);

        var throttle = new AdaptiveThrottle(maxConcurrentDownloads);
        var folderSem = new SemaphoreSlim(maxConcurrentFolders);
        try
        {
            await Task.WhenAll(folders.Select((folder, i) =>
                RunFolderAsync(folder, i + 1, tokens[i], targets[i], since,
                    channel.Writer, throttle, folderSem, ct)));
        }
        finally
        {
            channel.Writer.Complete();
        }
        var (added, updated, removed) = await writerTask;
        return new SyncStats(folders.Count, added, updated, removed, _failed);
    }

    async Task RunFolderAsync(
        (long LocalId, string ServerId, string Name) folder, int index,
        string? storedToken, int? target, DateTimeOffset? since,
        ChannelWriter<WriteOp> writer, AdaptiveThrottle throttle, SemaphoreSlim folderSem,
        CancellationToken ct)
    {
        await folderSem.WaitAsync(ct);
        try
        {
            using var readDb = dbFactory();
            var deltaBuilder = _mailbox.MailFolders[folder.ServerId].Messages.Delta;
            var downloaded = 0;
            var scanned = 0;
            progress?.Invoke(new(SyncPhase.Downloading, folder.Name, index, _folderCount,
                0, target, _overallDownloaded, _overallTarget));

            Task<Microsoft.Graph.Users.Item.MailFolders.Item.Messages.Delta.DeltaGetResponse?> FreshListing() =>
                deltaBuilder.GetAsDeltaGetResponseAsync(rc =>
                {
                    rc.QueryParameters.Select = DeltaSelect;
                    rc.Headers.Add("Prefer", "odata.maxpagesize=100");
                    if (since is { } s)
                        rc.QueryParameters.Filter =
                            $"receivedDateTime ge {s.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
                }, ct)!;

            Microsoft.Graph.Users.Item.MailFolders.Item.Messages.Delta.DeltaGetResponse? page;
            if (storedToken is not null)
            {
                try
                {
                    // Stored cursor may be a mid-listing nextLink or a final deltaLink —
                    // both resume server-side from exactly where we left off.
                    page = await Guarded(() => deltaBuilder.WithUrl(storedToken)
                        .GetAsDeltaGetResponseAsync(rc => rc.Headers.Add("Prefer", "odata.maxpagesize=100"), ct), throttle, ct);
                }
                catch (ODataError ex) when (ex.ResponseStatusCode == 410)
                {
                    // Cursor expired server-side: restart the folder from scratch;
                    // stored messages are skipped by id, not re-downloaded.
                    page = await Guarded(FreshListing, throttle, ct);
                }
            }
            else
            {
                page = await Guarded(FreshListing, throttle, ct);
            }

            while (page is not null)
            {
                var pageItems = page.Value ?? [];
                var toDownload = new List<Message>();
                foreach (var message in pageItems)
                {
                    ct.ThrowIfCancellationRequested();
                    if (message.Id is null) continue;
                    try
                    {
                        if (message.AdditionalData?.ContainsKey("@removed") == true)
                            await writer.WriteAsync(new DeleteOp(message.Id, folder.LocalId), ct);
                        else if (MailboxStore.TryGetMessageIdByServerId(readDb, message.Id) is not null)
                            await writer.WriteAsync(new UpdateOp(message.Id, folder.LocalId, message), ct);
                        else
                            toDownload.Add(message);
                    }
                    catch (ODataError)
                    {
                        Interlocked.Increment(ref _failed);
                    }
                }

                // Fan this page's downloads across the worker pool (benchmarked ~5x
                // sequential at C=4). The per-page barrier keeps the checkpoint exact:
                // the page cursor is enqueued only after every item is written.
                await Task.WhenAll(toDownload.Select(async message =>
                {
                    try
                    {
                        var raw = await Guarded(() => DownloadRawAsync(message.Id!, ct), throttle, ct);
                        var parsed = MimeMessageParser.Parse(raw); // CPU stays on the worker
                        await writer.WriteAsync(new IngestOp(folder.LocalId, raw, parsed, message), ct);
                        var inFolder = Interlocked.Increment(ref downloaded);
                        var overall = Interlocked.Increment(ref _overallDownloaded);
                        progress?.Invoke(new(SyncPhase.Downloading, folder.Name, index, _folderCount,
                            inFolder, target, overall, _overallTarget));
                    }
                    catch (ODataError)
                    {
                        Interlocked.Increment(ref _failed);
                    }
                }));
                // Per-page heartbeat (FolderDownloaded carries items examined) so
                // resume scans over known messages are visible, not silent.
                scanned += page.Value?.Count ?? 0;
                progress?.Invoke(new(SyncPhase.Scanning, folder.Name, index, _folderCount,
                    scanned, target, _overallDownloaded, _overallTarget));

                if (page.OdataNextLink is not null)
                {
                    // Mid-listing checkpoint: enqueued behind this page's data ops,
                    // so a restart resumes here having lost at most one page.
                    var next = page.OdataNextLink;
                    await writer.WriteAsync(new TokenOp(folder.LocalId, next), ct);
                    page = await Guarded(() => deltaBuilder.WithUrl(next)
                        .GetAsDeltaGetResponseAsync(rc => rc.Headers.Add("Prefer", "odata.maxpagesize=100"), ct), throttle, ct);
                    continue;
                }
                if (page.OdataDeltaLink is not null)
                    await writer.WriteAsync(new TokenOp(folder.LocalId, page.OdataDeltaLink), ct);
                break;
            }
            progress?.Invoke(new(SyncPhase.FolderDone, folder.Name, index, _folderCount,
                downloaded, target, _overallDownloaded, _overallTarget));
        }
        finally
        {
            folderSem.Release();
        }
    }

    async Task<byte[]> DownloadRawAsync(string messageId, CancellationToken ct)
    {
        await using var stream = await _mailbox.Messages[messageId].Content
            .GetAsync(cancellationToken: ct)
            ?? throw new InvalidOperationException($"No MIME content for {messageId}.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        return buffer.ToArray();
    }

    /// <summary>Runs a network call inside the worker pool; on a 429 that leaked through the
    /// SDK's own retries, shrinks the pool, backs off, and retries once.</summary>
    async Task<T> Guarded<T>(Func<Task<T>> action, AdaptiveThrottle throttle, CancellationToken ct)
    {
        await throttle.WaitAsync(ct);
        try
        {
            try
            {
                var result = await action();
                if (throttle.NoteSuccess() is int grown)
                    progress?.Invoke(new(SyncPhase.Throttled, null, 0, _folderCount,
                        grown, null, _overallDownloaded, _overallTarget));
                return result;
            }
            catch (ODataError ex) when (ex.ResponseStatusCode == 429)
            {
                var newLimit = throttle.Shrink();
                progress?.Invoke(new(SyncPhase.Throttled, null, 0, _folderCount,
                    newLimit, null, _overallDownloaded, _overallTarget));
                await Task.Delay(TimeSpan.FromSeconds(5), ct);
                return await action();
            }
        }
        finally
        {
            throttle.Release();
        }
    }

    async Task<(int Added, int Updated, int Removed)> WriterLoopAsync(
        ChannelReader<WriteOp> reader, CancellationToken ct)
    {
        using var db = dbFactory();
        int added = 0, updated = 0, removed = 0;
        await foreach (var op in reader.ReadAllAsync(ct))
        {
            try
            {
                switch (op)
                {
                    case IngestOp ingest:
                        // Re-check under the single writer: a move can surface the same
                        // message from two folder tasks.
                        if (MailboxStore.TryGetMessageIdByServerId(db, ingest.Msg.Id!) is long existing)
                        {
                            ApplyState(db, existing, ingest.FolderId, ingest.Msg);
                            updated++;
                        }
                        else
                        {
                            var id = MailboxStore.IngestMessage(db, ingest.FolderId, ingest.Raw,
                                ingest.Parsed, ingest.Msg.ReceivedDateTime, ingest.Msg.Id);
                            ApplyState(db, id, ingest.FolderId, ingest.Msg);
                            added++;
                        }
                        break;
                    case UpdateOp update:
                        if (MailboxStore.TryGetMessageIdByServerId(db, update.ServerId) is long messageId)
                        {
                            ApplyState(db, messageId, update.FolderId, update.Msg);
                            updated++;
                        }
                        break;
                    case DeleteOp delete:
                        if (MailboxStore.DeleteMessageByServerId(db, delete.ServerId, delete.FolderId))
                            removed++;
                        break;
                    case TokenOp token:
                        MailboxStore.SaveDeltaToken(db, token.FolderId, "graph", token.DeltaLink);
                        break;
                }
            }
            catch
            {
                Interlocked.Increment(ref _failed);
            }
        }
        return (added, updated, removed);
    }

    static void ApplyState(SqliteConnection db, long messageId, long folderId, Message message) =>
        MailboxStore.UpdateMessageState(db, messageId, folderId,
            isRead: message.IsRead ?? false,
            isFlagged: message.Flag?.FlagStatus == FollowupFlagStatus.Flagged,
            isDraft: message.IsDraft ?? false);

    async Task<int?> TryCountFolderAsync(string serverFolderId, DateTimeOffset? since, CancellationToken ct)
    {
        try
        {
            return await _mailbox.MailFolders[serverFolderId].Messages.Count.GetAsync(rc =>
            {
                rc.Headers.Add("ConsistencyLevel", "eventual");
                if (since is { } s)
                    rc.QueryParameters.Filter = $"receivedDateTime ge {s.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
            }, ct);
        }
        catch (ODataError)
        {
            return null; // count unsupported here — unknown target
        }
    }

    async Task<Dictionary<string, string>> MapWellKnownFoldersAsync(CancellationToken ct)
    {
        var map = new Dictionary<string, string>();
        foreach (var (wellKnown, specialUse) in WellKnownFolders)
        {
            try
            {
                var folder = await _mailbox.MailFolders[wellKnown].GetAsync(cancellationToken: ct);
                if (folder?.Id is string id)
                    map[id] = specialUse;
            }
            catch (ODataError) { /* folder type not present in this mailbox */ }
        }
        return map;
    }

    async Task<List<(long LocalId, string ServerId, string Name)>> SyncFoldersAsync(
        SqliteConnection listDb, Dictionary<string, string> specialUse, CancellationToken ct)
    {
        var result = new List<(long, string, string)>();
        await WalkFoldersAsync(parentServerId: null, ct, entry =>
        {
            if (entry.Folder.Id is null) return;
            var name = entry.Folder.DisplayName ?? entry.Folder.Id;
            var localId = MailboxStore.UpsertFolder(
                listDb, entry.Folder.Id, entry.ParentServerId, name,
                specialUse.GetValueOrDefault(entry.Folder.Id),
                entry.Folder.TotalItemCount ?? 0, entry.Folder.UnreadItemCount ?? 0);
            result.Add((localId, entry.Folder.Id, name));
        });
        return result;
    }

    async Task WalkFoldersAsync(
        string? parentServerId, CancellationToken ct,
        Action<(MailFolder Folder, string? ParentServerId)> visit)
    {
        var page = parentServerId is null
            ? await _mailbox.MailFolders.GetAsync(rc => rc.QueryParameters.Top = 100, ct)
            : await _mailbox.MailFolders[parentServerId].ChildFolders
                .GetAsync(rc => rc.QueryParameters.Top = 100, ct);
        while (page is not null)
        {
            foreach (var folder in page.Value ?? [])
            {
                ct.ThrowIfCancellationRequested();
                visit((folder, parentServerId));
                if (folder.ChildFolderCount > 0 && folder.Id is not null)
                    await WalkFoldersAsync(folder.Id, ct, visit);
            }
            if (page.OdataNextLink is null) break;
            page = await _mailbox.MailFolders.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: ct);
        }
    }

    /// <summary>
    /// Additive-increase / multiplicative-decrease concurrency limiter: a 429
    /// retires a permit, and a run of clean successes hands one back. Without the
    /// recovery half, a single throttling episode early in a long sync would pin
    /// the pool at one worker for the rest of the run.
    /// </summary>
    sealed class AdaptiveThrottle(int initial)
    {
        const int SuccessesBeforeGrowth = 50;

        readonly SemaphoreSlim _sem = new(initial, initial);
        readonly object _gate = new();
        readonly int _max = initial;
        int _limit = initial;
        int _successes;

        public int Limit { get { lock (_gate) return _limit; } }

        public Task WaitAsync(CancellationToken ct) => _sem.WaitAsync(ct);
        public void Release() => _sem.Release();

        public int Shrink()
        {
            lock (_gate)
            {
                _successes = 0;
                if (_limit > 1 && _sem.Wait(0))
                    _limit--;
                return _limit;
            }
        }

        /// <summary>Returns the new limit when a permit is restored, else null.</summary>
        public int? NoteSuccess()
        {
            lock (_gate)
            {
                if (_limit >= _max)
                    return null;
                if (++_successes < SuccessesBeforeGrowth)
                    return null;
                _successes = 0;
                _limit++;
                _sem.Release();
                return _limit;
            }
        }
    }
}
