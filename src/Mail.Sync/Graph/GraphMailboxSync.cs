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
    int maxConcurrentDownloads = 1,
    int maxConcurrentFolders = 1)
{
    public enum SyncPhase { Folders, Counting, Downloading, FolderDone, Throttled }

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
            var deltaBuilder = graph.Me.MailFolders[folder.ServerId].Messages.Delta;
            var downloaded = 0;
            progress?.Invoke(new(SyncPhase.Downloading, folder.Name, index, _folderCount,
                0, target, _overallDownloaded, _overallTarget));

            var page = storedToken is not null
                ? await Guarded(() => deltaBuilder.WithUrl(storedToken)
                    .GetAsDeltaGetResponseAsync(cancellationToken: ct), throttle, ct)
                : await Guarded(() => deltaBuilder.GetAsDeltaGetResponseAsync(rc =>
                {
                    rc.QueryParameters.Select = DeltaSelect;
                    if (since is { } s)
                        rc.QueryParameters.Filter =
                            $"receivedDateTime ge {s.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
                }, ct), throttle, ct);

            while (page is not null)
            {
                foreach (var message in page.Value ?? [])
                {
                    ct.ThrowIfCancellationRequested();
                    if (message.Id is null) continue;
                    try
                    {
                        if (message.AdditionalData?.ContainsKey("@removed") == true)
                        {
                            await writer.WriteAsync(new DeleteOp(message.Id, folder.LocalId), ct);
                        }
                        else if (MailboxStore.TryGetMessageIdByServerId(readDb, message.Id) is not null)
                        {
                            await writer.WriteAsync(new UpdateOp(message.Id, folder.LocalId, message), ct);
                        }
                        else
                        {
                            var raw = await Guarded(() => DownloadRawAsync(message.Id, ct), throttle, ct);
                            var parsed = MimeMessageParser.Parse(raw); // CPU work stays on the worker
                            await writer.WriteAsync(new IngestOp(folder.LocalId, raw, parsed, message), ct);
                            downloaded++;
                            var overall = Interlocked.Increment(ref _overallDownloaded);
                            progress?.Invoke(new(SyncPhase.Downloading, folder.Name, index, _folderCount,
                                downloaded, target, overall, _overallTarget));
                        }
                    }
                    catch (ODataError)
                    {
                        Interlocked.Increment(ref _failed);
                    }
                }
                if (page.OdataNextLink is not null)
                {
                    var next = page.OdataNextLink;
                    page = await Guarded(() => deltaBuilder.WithUrl(next)
                        .GetAsDeltaGetResponseAsync(cancellationToken: ct), throttle, ct);
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
        await using var stream = await graph.Me.Messages[messageId].Content
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
                return await action();
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
            return await graph.Me.MailFolders[serverFolderId].Messages.Count.GetAsync(rc =>
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
                var folder = await graph.Me.MailFolders[wellKnown].GetAsync(cancellationToken: ct);
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
            ? await graph.Me.MailFolders.GetAsync(rc => rc.QueryParameters.Top = 100, ct)
            : await graph.Me.MailFolders[parentServerId].ChildFolders
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
            page = await graph.Me.MailFolders.WithUrl(page.OdataNextLink).GetAsync(cancellationToken: ct);
        }
    }

    /// <summary>Semaphore whose effective limit only shrinks (permits retired on 429).</summary>
    sealed class AdaptiveThrottle(int initial)
    {
        readonly SemaphoreSlim _sem = new(initial, initial);
        readonly object _gate = new();
        int _limit = initial;

        public Task WaitAsync(CancellationToken ct) => _sem.WaitAsync(ct);
        public void Release() => _sem.Release();

        public int Shrink()
        {
            lock (_gate)
            {
                if (_limit > 1 && _sem.Wait(0))
                    _limit--;
                return _limit;
            }
        }
    }
}
