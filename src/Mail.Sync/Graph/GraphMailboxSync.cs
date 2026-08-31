using System.Runtime.Versioning;
using Mail.Core.Ingest;
using Mail.Storage;
using Microsoft.Data.Sqlite;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace Mail.Sync.Graph;

/// <summary>
/// Mirrors a Graph mailbox into a mailbox database. Folders are listed
/// recursively; messages use per-folder delta queries (token checkpointed in
/// sync_state, so every run after the first is incremental). New messages are
/// downloaded as raw RFC 5322 via the $value endpoint — byte-exact source of
/// truth — and fed through the MIME ingestion pipeline; changed messages get
/// flag/folder updates; removals are applied move-safely.
///
/// Progress: folders needing an initial download are $count-ed up front so the
/// caller can render a determinate bar and ETA; incremental folders have an
/// unknown (usually tiny) target.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GraphMailboxSync(
    GraphServiceClient graph, SqliteConnection db,
    Action<GraphMailboxSync.SyncProgressEvent>? progress = null)
{
    public enum SyncPhase { Folders, Counting, Downloading, FolderDone }

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

    int _overallDownloaded;
    int? _overallTarget;

    /// <summary>Full sync pass. <paramref name="since"/> bounds the initial window per folder
    /// (WindowedCache); ignored on incremental runs, which resume from the delta token.</summary>
    public async Task<SyncStats> SyncAsync(DateTimeOffset? since = null, CancellationToken ct = default)
    {
        progress?.Invoke(new(SyncPhase.Folders, null, 0, 0, 0, null, 0, null));
        var specialUse = await MapWellKnownFoldersAsync(ct);
        var folders = await SyncFoldersAsync(specialUse, ct);

        // Pre-count folders that face an initial (non-delta) download so the
        // caller gets a determinate overall target.
        var targets = new int?[folders.Count];
        var overallKnown = 0;
        var anyKnown = false;
        for (var i = 0; i < folders.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(new(SyncPhase.Counting, folders[i].Name, i + 1, folders.Count, 0, null, 0, null));
            if (MailboxStore.GetDeltaToken(db, folders[i].LocalId) is null)
            {
                targets[i] = await TryCountFolderAsync(folders[i].ServerId, since, ct);
                if (targets[i] is int known)
                {
                    overallKnown += known;
                    anyKnown = true;
                }
            }
        }
        _overallTarget = anyKnown ? overallKnown : null;
        _overallDownloaded = 0;

        int added = 0, updated = 0, removed = 0, failed = 0;
        for (var i = 0; i < folders.Count; i++)
        {
            ct.ThrowIfCancellationRequested();
            var (localId, serverId, name) = folders[i];
            var (a, u, r, f) = await SyncFolderMessagesAsync(
                localId, serverId, name, i + 1, folders.Count, targets[i], since, ct);
            added += a; updated += u; removed += r; failed += f;
            progress?.Invoke(new(SyncPhase.FolderDone, name, i + 1, folders.Count,
                a, targets[i], _overallDownloaded, _overallTarget));
        }
        return new SyncStats(folders.Count, added, updated, removed, failed);
    }

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
            return null; // count unsupported here — fall back to unknown target
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
        Dictionary<string, string> specialUse, CancellationToken ct)
    {
        var result = new List<(long, string, string)>();
        await WalkFoldersAsync(parentServerId: null, ct, folder =>
        {
            if (folder.Folder.Id is null) return;
            var name = folder.Folder.DisplayName ?? folder.Folder.Id;
            var localId = MailboxStore.UpsertFolder(
                db, folder.Folder.Id, folder.ParentServerId, name,
                specialUse.GetValueOrDefault(folder.Folder.Id),
                folder.Folder.TotalItemCount ?? 0, folder.Folder.UnreadItemCount ?? 0);
            result.Add((localId, folder.Folder.Id, name));
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

    async Task<(int Added, int Updated, int Removed, int Failed)> SyncFolderMessagesAsync(
        long localFolderId, string serverFolderId, string name,
        int folderIndex, int folderCount, int? folderTarget,
        DateTimeOffset? since, CancellationToken ct)
    {
        int added = 0, updated = 0, removed = 0, failed = 0;
        var deltaBuilder = graph.Me.MailFolders[serverFolderId].Messages.Delta;
        var storedToken = MailboxStore.GetDeltaToken(db, localFolderId);

        progress?.Invoke(new(SyncPhase.Downloading, name, folderIndex, folderCount,
            0, folderTarget, _overallDownloaded, _overallTarget));

        var page = storedToken is not null
            ? await deltaBuilder.WithUrl(storedToken).GetAsDeltaGetResponseAsync(cancellationToken: ct)
            : await deltaBuilder.GetAsDeltaGetResponseAsync(rc =>
            {
                rc.QueryParameters.Select = DeltaSelect;
                if (since is { } s)
                    rc.QueryParameters.Filter =
                        $"receivedDateTime ge {s.UtcDateTime:yyyy-MM-ddTHH:mm:ssZ}";
            }, ct);

        while (page is not null)
        {
            foreach (var message in page.Value ?? [])
            {
                ct.ThrowIfCancellationRequested();
                if (message.Id is null) continue;
                try
                {
                    switch (Classify(message))
                    {
                        case ChangeKind.Removed:
                            if (MailboxStore.DeleteMessageByServerId(db, message.Id, localFolderId))
                                removed++;
                            break;
                        case ChangeKind.Existing when
                            MailboxStore.TryGetMessageIdByServerId(db, message.Id) is long existingId:
                            ApplyState(existingId, localFolderId, message);
                            updated++;
                            break;
                        default:
                            await DownloadAndIngestAsync(localFolderId, message, ct);
                            added++;
                            _overallDownloaded++;
                            progress?.Invoke(new(SyncPhase.Downloading, name, folderIndex, folderCount,
                                added, folderTarget, _overallDownloaded, _overallTarget));
                            break;
                    }
                }
                catch (ODataError)
                {
                    failed++;
                }
            }
            if (page.OdataNextLink is not null)
            {
                page = await deltaBuilder.WithUrl(page.OdataNextLink)
                    .GetAsDeltaGetResponseAsync(cancellationToken: ct);
                continue;
            }
            if (page.OdataDeltaLink is not null)
                MailboxStore.SaveDeltaToken(db, localFolderId, "graph", page.OdataDeltaLink);
            break;
        }
        return (added, updated, removed, failed);
    }

    enum ChangeKind { New, Existing, Removed }

    ChangeKind Classify(Message message)
    {
        if (message.AdditionalData?.ContainsKey("@removed") == true)
            return ChangeKind.Removed;
        return MailboxStore.TryGetMessageIdByServerId(db, message.Id!) is not null
            ? ChangeKind.Existing
            : ChangeKind.New;
    }

    void ApplyState(long messageId, long folderId, Message message) =>
        MailboxStore.UpdateMessageState(db, messageId, folderId,
            isRead: message.IsRead ?? false,
            isFlagged: message.Flag?.FlagStatus == FollowupFlagStatus.Flagged,
            isDraft: message.IsDraft ?? false);

    async Task DownloadAndIngestAsync(long folderId, Message message, CancellationToken ct)
    {
        await using var stream = await graph.Me.Messages[message.Id!].Content
            .GetAsync(cancellationToken: ct)
            ?? throw new InvalidOperationException($"No MIME content for {message.Id}.");
        using var buffer = new MemoryStream();
        await stream.CopyToAsync(buffer, ct);
        var raw = buffer.ToArray();
        var parsed = MimeMessageParser.Parse(raw);
        var localId = MailboxStore.IngestMessage(
            db, folderId, raw, parsed, message.ReceivedDateTime, message.Id);
        ApplyState(localId, folderId, message);
    }
}
