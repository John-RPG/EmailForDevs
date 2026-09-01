using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Mail.Core.Compose;
using Mail.Core.Ingest;
using Mail.Core.Search;
using Mail.Storage;
using Mail.Storage.Database;
using Mail.Storage.Security;
using Mail.Sync.Auth;
using Mail.Sync.Graph;
using Microsoft.Data.Sqlite;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;
using Microsoft.Web.WebView2.Core;
using MimeKit;

namespace Mail.App;

/// <summary>
/// Dev shell: folder tree + activity log | DataGrid message list with a column
/// chooser | reading pane (locked-down rendered preview with cid: images,
/// plain text, HTML source, headers, byte-exact MIME). Message actions write
/// back to Graph. In-app parallel sync with counted targets, determinate
/// progress, and ETA. Reads the dev profile produced by tools/SyncSmoke.
/// </summary>
public partial class MainWindow : Window
{
    const int RawDisplayCap = 2 * 1024 * 1024;
    const int PreviewHtmlCap = 1_500_000;
    const string CidHost = "https://eeemail.local/cid/";

    sealed record MailboxHandle(
        string Upn, string DbPath, byte[] Dek, int WindowMonths, string Policy, SqliteConnection Db)
    {
        public bool IsFullMirror => Policy == "MirrorServer" || WindowMonths <= 0;
        public string ScopeText => IsFullMirror ? "full mirror" : $"scoped: last {WindowMonths} month(s)";
        public DateTimeOffset? Since =>
            IsFullMirror ? null : DateTimeOffset.UtcNow.AddMonths(-WindowMonths);
    }

    sealed record FolderNode(MailboxHandle Mailbox, long FolderId, string Name);

    public sealed record MessageRow(
        object Mailbox, long Id, string? ServerId, string Status, string From, string FromName,
        string FromAddress, string To, string Received, string SizeKb, double SizeVal,
        string Subject, bool IsUnread);

    public sealed record AttachmentItem(
        object Mailbox, long Id, string Name, string ContentType, string SizeKb,
        string Inline, bool HasContent);

    public enum LogLevel { Debug, Verbose, Info, Warning, Error }

    public sealed record LogEntry(DateTime At, LogLevel Level, string Message)
    {
        public string Display => $"{Icon} {At:HH:mm:ss}  {Message}";
        public string Icon => Level switch
        {
            LogLevel.Debug => "⚙",
            LogLevel.Verbose => "·",
            LogLevel.Info => "ℹ",
            LogLevel.Warning => "⚠",
            _ => "✖",
        };
        public Brush Brush => Level switch
        {
            LogLevel.Debug => Brushes.Gray,
            LogLevel.Verbose => Brushes.Gray,
            LogLevel.Warning => Brushes.DarkOrange,
            LogLevel.Error => Brushes.Firebrick,
            _ => Brushes.Black,
        };
    }

    readonly List<MailboxHandle> _mailboxes = [];
    readonly Dictionary<(string Upn, long FolderId), FolderNodeViewModel> _folderNodes = [];
    readonly Dictionary<(string Upn, long FolderId), FolderNodeViewModel> _favouriteNodes = [];
    Dictionary<string, List<long>> _favourites = [];
    readonly Dictionary<string, GraphServiceClient> _graphClients = [];
    readonly ObservableCollection<LogEntry> _log = [];
    readonly HashSet<LogLevel> _enabledLevels =
        [LogLevel.Verbose, LogLevel.Info, LogLevel.Warning, LogLevel.Error];
    readonly HashSet<string> _treeRefreshPending = [];
    ICollectionView? _logView;
    System.Windows.Threading.DispatcherTimer? _autoSyncTimer;
    SqliteConnection? _appDb;
    string? _scratchRoot;
    bool _syncRunning;
    bool _webViewReady;
    string? _currentHtml;
    MailboxHandle? _currentMailbox;
    long _currentMessageId;

    public MainWindow()
    {
        InitializeComponent();
        _logView = CollectionViewSource.GetDefaultView(_log);
        _logView.Filter = o => o is LogEntry entry && _enabledLevels.Contains(entry.Level);
        ActivityLog.ItemsSource = _logView;
        CommandBindings.Add(new CommandBinding(ComposeCommand, (_, _) => OpenCompose(null)));
        CommandBindings.Add(new CommandBinding(ReplyCommand, (_, _) => ReplyToSelected(ReplyKind.Reply)));
        CommandBindings.Add(new CommandBinding(ReplyAllCommand, (_, _) => ReplyToSelected(ReplyKind.ReplyAll)));
        CommandBindings.Add(new CommandBinding(ForwardCommand, (_, _) => ReplyToSelected(ReplyKind.Forward)));
        Loaded += (_, _) =>
        {
            OpenProfile();
            BuildColumnsMenu();
            StartSync();
            // Delta is pull-only: poll periodically so server-side changes
            // (new mail, reads/moves made elsewhere) show up without a click.
            _autoSyncTimer = new System.Windows.Threading.DispatcherTimer
            {
                Interval = TimeSpan.FromMinutes(2),
            };
            _autoSyncTimer.Tick += (_, _) => StartSync();
            _autoSyncTimer.Start();
        };
        Closed += (_, _) => CloseAll();
    }

    void Log(string line) => Log(LogLevel.Info, line);

    void Log(LogLevel level, string line)
    {
        var entry = new LogEntry(DateTime.Now, level, line);
        _log.Add(entry);
        while (_log.Count > 1000)
            _log.RemoveAt(0);
        if (_enabledLevels.Contains(level))
            ActivityLog.ScrollIntoView(entry);
    }

    void OnLogFilterChanged(object sender, RoutedEventArgs e)
    {
        _enabledLevels.Clear();
        if (FltDebug.IsChecked == true) _enabledLevels.Add(LogLevel.Debug);
        if (FltVerbose.IsChecked == true) _enabledLevels.Add(LogLevel.Verbose);
        if (FltInfo.IsChecked == true) _enabledLevels.Add(LogLevel.Info);
        if (FltWarning.IsChecked == true) _enabledLevels.Add(LogLevel.Warning);
        if (FltError.IsChecked == true) _enabledLevels.Add(LogLevel.Error);
        _logView?.Refresh();
    }

    void OnLogClear(object sender, RoutedEventArgs e) => _log.Clear();

    // ---- startup -------------------------------------------------------------

    void OpenProfile()
    {
        try
        {
            _scratchRoot = FindScratchRoot()
                ?? throw new InvalidOperationException(
                    "No dev profile found. Run `dotnet run --project tools/SyncSmoke` first.");
            var repoRoot = Path.GetDirectoryName(_scratchRoot)!;
            var keyStore = new ProfileKeyStore(Path.Combine(_scratchRoot, "profile"));
            var masterKey = keyStore.Unlock();
            _appDb = AppDatabase.Open(Path.Combine(_scratchRoot, "profile", "app.db"), masterKey);

            using var cmd = _appDb.CreateCommand();
            cmd.CommandText = """
                SELECT upn, db_path, dek, coalesce(sync_window_months, 0),
                       coalesce(sync_policy, 'MirrorServer')
                FROM mailboxes WHERE enabled = 1 ORDER BY position, id;
                """;
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
            {
                var upn = reader.GetString(0);
                var dbPath = reader.GetString(1);
                if (!Path.IsPathRooted(dbPath))
                    dbPath = Path.Combine(repoRoot, dbPath);
                var dek = (byte[])reader.GetValue(2);
                _mailboxes.Add(new MailboxHandle(
                    upn, dbPath, dek, reader.GetInt32(3), reader.GetString(4),
                    MailboxDatabase.Open(dbPath, dek)));
            }
            LoadFavourites();
            BuildTree();
            StatusText.Text = $"{_mailboxes.Count} mailbox(es) open.";
            Log($"Profile opened: {_mailboxes.Count} mailbox(es).");
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            Log(LogLevel.Error, $"ERROR: {ex.Message}");
            MessageBox.Show(this, ex.Message, "eeeMail", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    static string? FindScratchRoot()
    {
        foreach (var start in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
            for (var dir = new DirectoryInfo(start); dir is not null; dir = dir.Parent)
                if (Directory.Exists(Path.Combine(dir.FullName, ".scratch", "profile")))
                    return Path.Combine(dir.FullName, ".scratch");
        return null;
    }

    void CloseAll()
    {
        _autoSyncTimer?.Stop();
        foreach (var mailbox in _mailboxes)
        {
            try
            {
                // Sweep blobs orphaned by deletes, then fold the WAL back into the
                // main file so on-disk size reflects reality and a copy of the .db
                // alone is a complete backup.
                var swept = MailboxDatabase.CollectGarbageBlobs(mailbox.Db);
                using var cmd = mailbox.Db.CreateCommand();
                cmd.CommandText = "PRAGMA wal_checkpoint(TRUNCATE);";
                cmd.ExecuteNonQuery();
                if (swept > 0)
                    Log(LogLevel.Debug, $"{mailbox.Upn}: swept {swept:N0} orphaned blob(s).");
            }
            catch
            {
                // Never let housekeeping block shutdown.
            }
            mailbox.Db.Dispose();
        }
        _appDb?.Dispose();
    }

    void BuildColumnsMenu()
    {
        ColumnsMenu.Items.Clear();
        foreach (var column in MessageGrid.Columns)
        {
            var item = new MenuItem
            {
                Header = column.Header,
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
            };
            item.Click += (_, _) =>
            {
                column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                AutoSizeColumns();
            };
            ColumnsMenu.Items.Add(item);
        }
    }

    // ---- folder tree ---------------------------------------------------------

    void BuildTree()
    {
        FolderTree.Items.Clear();
        _folderNodes.Clear();
        FolderNodeViewModel? inboxNode = null;

        foreach (var mailbox in _mailboxes)
        {
            var root = new FolderNodeViewModel
            {
                Name = $"{mailbox.Upn}  [{mailbox.ScopeText}]",
                IsExpanded = true,
            };

            var rows = QueryFolders(mailbox);
            var localCounts = QueryLocalCounts(mailbox);
            var activity = QueryFolderActivity(mailbox);
            var nodes = new Dictionary<long, FolderNodeViewModel>();

            foreach (var row in rows)
            {
                var node = new FolderNodeViewModel
                {
                    Mailbox = mailbox,
                    FolderId = row.Id,
                    ServerId = row.ServerId,
                    SpecialUse = row.Special,
                    Name = row.Name,
                    IsExpanded = true,
                    LastActivity = activity.GetValueOrDefault(row.Id),
                };
                ApplyCounts(node, row, localCounts);
                nodes[row.Id] = node;
                _folderNodes[(mailbox.Upn, row.Id)] = node;
                if (row.Special == "inbox")
                    inboxNode ??= node;
            }

            // Favourites first, as a group of shortcuts to real folders.
            var favourites = new FolderNodeViewModel
            {
                Name = "Favourites",
                IsGroupHeader = true,
                IsExpanded = true,
            };
            foreach (var favouriteId in FavouritesFor(mailbox.Upn))
            {
                if (!nodes.TryGetValue(favouriteId, out var source)) continue;
                var shortcut = new FolderNodeViewModel
                {
                    Mailbox = mailbox,
                    FolderId = source.FolderId,
                    ServerId = source.ServerId,
                    SpecialUse = source.SpecialUse,
                    Name = source.Name,
                    Counts = source.Counts,
                    HasUnread = source.HasUnread,
                    IsFavouriteEntry = true,
                    LastActivity = source.LastActivity,
                };
                favourites.Children.Add(shortcut);
                _favouriteNodes[(mailbox.Upn, source.FolderId)] = shortcut;
            }
            if (favourites.Children.Count > 0)
                root.Children.Add(favourites);

            foreach (var row in rows)
            {
                var parent = row.ParentId is long p && nodes.TryGetValue(p, out var pn)
                    ? pn.Children
                    : root.Children;
                parent.Add(nodes[row.Id]);
            }
            FolderTree.Items.Add(root);
        }

        ApplyQuietFilter();
        if (inboxNode is not null)
            SelectNode(inboxNode);
    }

    static void ApplyCounts(
        FolderNodeViewModel node, FolderRow row,
        Dictionary<long, (long Total, long Unread)> local)
    {
        var (localTotal, localUnread) = local.GetValueOrDefault(row.Id);
        node.HasUnread = localUnread > 0;
        node.Counts = localTotal < row.ServerTotal
            ? $"{localTotal:N0}/{row.ServerTotal:N0}" +
              (row.ServerUnread > 0 ? $"  {localUnread:N0}/{row.ServerUnread:N0}" : "")
            : localUnread > 0 ? $"{localUnread:N0}" : "";
    }

    /// <summary>Newest received_at per folder, for the quiet-folder filter.</summary>
    static Dictionary<long, DateTimeOffset> QueryFolderActivity(MailboxHandle mailbox) =>
        QueryFolderActivity(mailbox.Db);

    static Dictionary<long, DateTimeOffset> QueryFolderActivity(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = "SELECT folder_id, max(received_at) FROM messages GROUP BY folder_id;";
        var map = new Dictionary<long, DateTimeOffset>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            if (!reader.IsDBNull(1))
                map[reader.GetInt64(0)] = DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1));
        return map;
    }

    void SelectNode(FolderNodeViewModel node)
    {
        // TreeView selection is container-driven; realise the containers first.
        FolderTree.UpdateLayout();
        if (FindContainer(FolderTree, node) is TreeViewItem container)
        {
            container.IsSelected = true;
            container.BringIntoView();
        }
        else if (node.Mailbox is MailboxHandle mailbox)
        {
            LoadFolder(new FolderNode(mailbox, node.FolderId, node.Name));
        }
    }

    static TreeViewItem? FindContainer(ItemsControl parent, object item)
    {
        if (parent.ItemContainerGenerator.ContainerFromItem(item) is TreeViewItem match)
            return match;
        foreach (var child in parent.Items)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(child) is not TreeViewItem container)
                continue;
            container.UpdateLayout();
            if (FindContainer(container, item) is TreeViewItem nested)
                return nested;
        }
        return null;
    }

    // ---- favourites and filtering -------------------------------------------

    List<long> FavouritesFor(string upn) =>
        _favourites.TryGetValue(upn, out var list) ? list : [];

    void LoadFavourites()
    {
        _favourites.Clear();
        if (_appDb is null) return;
        try
        {
            using var cmd = _appDb.CreateCommand();
            cmd.CommandText = "SELECT value FROM ui_state WHERE key = 'favourites';";
            if (cmd.ExecuteScalar() is string json && json.Length > 0)
                _favourites = System.Text.Json.JsonSerializer
                    .Deserialize<Dictionary<string, List<long>>>(json) ?? [];
        }
        catch (Exception ex)
        {
            Log(LogLevel.Debug, $"Could not read favourites: {ex.Message}");
        }
    }

    void SaveFavourites()
    {
        if (_appDb is null) return;
        try
        {
            using var cmd = _appDb.CreateCommand();
            cmd.CommandText = """
                INSERT INTO ui_state(key, value) VALUES('favourites', @v)
                ON CONFLICT(key) DO UPDATE SET value = @v;
                """;
            cmd.Parameters.AddWithValue("@v", System.Text.Json.JsonSerializer.Serialize(_favourites));
            cmd.ExecuteNonQuery();
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"Could not save favourites: {ex.Message}");
        }
    }

    void OnAddFavourite(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is not FolderNodeViewModel node ||
            node.Mailbox is not MailboxHandle mailbox || node.IsGroupHeader)
            return;
        if (!_favourites.TryGetValue(mailbox.Upn, out var list))
            _favourites[mailbox.Upn] = list = [];
        if (!list.Contains(node.FolderId))
        {
            list.Add(node.FolderId);
            SaveFavourites();
            BuildTree();
            Log($"Added {node.Name} to favourites.");
        }
    }

    void OnRemoveFavourite(object sender, RoutedEventArgs e)
    {
        if (FolderTree.SelectedItem is not FolderNodeViewModel node ||
            node.Mailbox is not MailboxHandle mailbox)
            return;
        if (_favourites.TryGetValue(mailbox.Upn, out var list) && list.Remove(node.FolderId))
        {
            SaveFavourites();
            BuildTree();
            Log($"Removed {node.Name} from favourites.");
        }
    }

    void OnHideQuietChanged(object sender, RoutedEventArgs e) => ApplyQuietFilter();

    /// <summary>
    /// Hides folders with nothing received in the last month. Folders with
    /// unread mail, favourites, special-use folders and any ancestor of a
    /// visible folder always stay: hiding those would lose mail, not noise.
    /// </summary>
    void ApplyQuietFilter()
    {
        var hide = HideQuietToggle.IsChecked == true;
        var cutoff = DateTimeOffset.UtcNow.AddMonths(-1);
        foreach (var item in FolderTree.Items.OfType<FolderNodeViewModel>())
            ApplyQuietFilter(item, hide, cutoff);
    }

    static bool ApplyQuietFilter(FolderNodeViewModel node, bool hide, DateTimeOffset cutoff)
    {
        var anyChildVisible = false;
        foreach (var child in node.Children)
            anyChildVisible |= ApplyQuietFilter(child, hide, cutoff);

        var keep =
            !hide ||
            node.IsGroupHeader ||
            node.Mailbox is null ||
            node.IsFavouriteEntry ||
            node.HasUnread ||
            node.SpecialUse is not null ||
            (node.LastActivity is { } last && last >= cutoff) ||
            anyChildVisible;

        node.Visibility = keep ? Visibility.Visible : Visibility.Collapsed;
        return keep;
    }

    void OnExpandAll(object sender, RoutedEventArgs e) => SetExpansion(true);
    void OnCollapseAll(object sender, RoutedEventArgs e) => SetExpansion(false);

    void SetExpansion(bool expanded)
    {
        foreach (var item in FolderTree.Items.OfType<FolderNodeViewModel>())
            SetExpansion(item, expanded, isRoot: true);
    }

    static void SetExpansion(FolderNodeViewModel node, bool expanded, bool isRoot = false)
    {
        node.IsExpanded = isRoot || expanded; // mailbox roots stay open
        foreach (var child in node.Children)
            SetExpansion(child, expanded);
    }

    sealed record FolderRow(
        long Id, long? ParentId, string Name, string? Special, string? ServerId,
        long ServerTotal, long ServerUnread);

    static List<FolderRow> QueryFolders(MailboxHandle mailbox) => QueryFolders(mailbox.Db);

    static List<FolderRow> QueryFolders(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT id, parent_id, name, special_use, server_id, total_count, unread_count FROM folders
            ORDER BY CASE special_use
                         WHEN 'inbox' THEN 0 WHEN 'drafts' THEN 1 WHEN 'sent' THEN 2
                         WHEN 'archive' THEN 3 WHEN 'junk' THEN 4 WHEN 'trash' THEN 5
                         ELSE 9 END,
                     name COLLATE NOCASE;
            """;
        var rows = new List<FolderRow>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            rows.Add(new FolderRow(
                reader.GetInt64(0),
                reader.IsDBNull(1) ? null : reader.GetInt64(1),
                reader.GetString(2),
                reader.IsDBNull(3) ? null : reader.GetString(3),
                reader.IsDBNull(4) ? null : reader.GetString(4),
                reader.GetInt64(5), reader.GetInt64(6)));
        return rows;
    }

    static Dictionary<long, (long Total, long Unread)> QueryLocalCounts(MailboxHandle mailbox) =>
        QueryLocalCounts(mailbox.Db);

    static Dictionary<long, (long Total, long Unread)> QueryLocalCounts(SqliteConnection db)
    {
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT folder_id, count(*), coalesce(sum(1 - is_read), 0)
            FROM messages GROUP BY folder_id;
            """;
        var counts = new Dictionary<long, (long, long)>();
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
            counts[reader.GetInt64(0)] = (reader.GetInt64(1), reader.GetInt64(2));
        return counts;
    }

    static string FolderHeader(FolderRow row, Dictionary<long, (long Total, long Unread)> local)
    {
        var (localTotal, localUnread) = local.GetValueOrDefault(row.Id);
        if (localTotal < row.ServerTotal)
            return $"{row.Name} ({localTotal:N0}/{row.ServerTotal:N0}) ({localUnread:N0}/{row.ServerUnread:N0})";
        return localUnread > 0 ? $"{row.Name} ({localUnread:N0})" : row.Name;
    }

    /// <summary>
    /// Refreshes folder headers without blocking the UI: the rollup query runs on
    /// a worker thread (its own connection, since SqliteConnection is not thread
    /// safe) and only the header assignment returns to the dispatcher. Overlapping
    /// requests collapse into one.
    /// </summary>
    void UpdateTreeCounts(MailboxHandle mailbox)
    {
        if (!_treeRefreshPending.Add(mailbox.Upn))
            return; // a refresh for this mailbox is already in flight
        _ = Task.Run(() =>
        {
            Dictionary<long, (long Total, long Unread)> counts;
            List<FolderRow> folders;
            Dictionary<long, DateTimeOffset> activity;
            try
            {
                using var db = MailboxDatabase.Open(mailbox.DbPath, mailbox.Dek);
                counts = QueryLocalCounts(db);
                folders = QueryFolders(db);
                activity = QueryFolderActivity(db);
            }
            catch
            {
                _treeRefreshPending.Remove(mailbox.Upn);
                return;
            }
            Dispatcher.BeginInvoke(() =>
            {
                _treeRefreshPending.Remove(mailbox.Upn);
                foreach (var row in folders)
                {
                    if (_folderNodes.TryGetValue((mailbox.Upn, row.Id), out var node))
                    {
                        ApplyCounts(node, row, counts);
                        node.LastActivity = activity.GetValueOrDefault(row.Id);
                    }
                    if (_favouriteNodes.TryGetValue((mailbox.Upn, row.Id), out var favourite))
                        ApplyCounts(favourite, row, counts);
                }
            });
        });
    }

    void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (FolderTree.SelectedItem is FolderNodeViewModel node &&
            node.Mailbox is MailboxHandle mailbox && !node.IsGroupHeader)
            LoadFolder(new FolderNode(mailbox, node.FolderId, node.Name));
    }

    // ---- message list --------------------------------------------------------

    const string RowSelect = """
        SELECT m.id, m.subject, m.received_at, m.size, m.is_read, m.server_id,
               m.has_attachments, m.is_flagged, m.importance,
               fa.display_name, fa.email,
               (SELECT a2.email
                FROM message_addresses ma2 JOIN addresses a2 ON a2.id = ma2.address_id
                WHERE ma2.message_id = m.id AND ma2.kind = 1
                ORDER BY ma2.position LIMIT 1) AS to_email
        FROM messages m
        LEFT JOIN message_addresses ma ON ma.message_id = m.id AND ma.kind = 0 AND ma.position = 0
        LEFT JOIN addresses fa ON fa.id = ma.address_id
        """;

    void LoadFolder(FolderNode node)
    {
        using var cmd = node.Mailbox.Db.CreateCommand();
        cmd.CommandText = RowSelect + " WHERE m.folder_id = @f ORDER BY m.received_at DESC LIMIT 5000;";
        cmd.Parameters.AddWithValue("@f", node.FolderId);
        FillList(node.Mailbox, cmd);
        StatusText.Text = $"{node.Mailbox.Upn} / {node.Name}: {MessageGrid.Items.Count:N0} message(s)";
    }

    void FillList(MailboxHandle mailbox, SqliteCommand cmd)
    {
        var rows = new List<MessageRow>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var name = reader.IsDBNull(9) ? "" : reader.GetString(9);
                var email = reader.IsDBNull(10) ? "" : reader.GetString(10);
                var sizeVal = reader.IsDBNull(3) ? 0 : reader.GetInt64(3) / 1024.0;
                var status =
                    (reader.GetInt64(6) != 0 ? "📎" : "") +
                    (reader.GetInt64(7) != 0 ? "⚑" : "") +
                    (reader.GetInt64(8) switch { 2 => "❗", 0 => "▼", _ => "" });
                rows.Add(new MessageRow(
                    mailbox,
                    reader.GetInt64(0),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    Status: status,
                    From: string.IsNullOrEmpty(name) || name == email ? email : $"{name} <{email}>",
                    FromName: name == email ? "" : name,
                    FromAddress: email,
                    To: reader.IsDBNull(11) ? "" : reader.GetString(11),
                    Received: reader.IsDBNull(2)
                        ? ""
                        : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2))
                            .ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    SizeKb: sizeVal.ToString("N0"),
                    SizeVal: sizeVal,
                    Subject: reader.IsDBNull(1) ? "" : reader.GetString(1),
                    IsUnread: reader.GetInt64(4) == 0));
            }
        }
        MessageGrid.ItemsSource = rows;
        AutoSizeColumns();
    }

    /// <summary>Re-fits Auto columns to the currently realized rows; Subject keeps its star width.</summary>
    void AutoSizeColumns()
    {
        foreach (var column in MessageGrid.Columns)
            if (!ReferenceEquals(column, ColSubject) && column.Visibility == Visibility.Visible)
                column.Width = 0;
        Dispatcher.BeginInvoke(() =>
        {
            foreach (var column in MessageGrid.Columns)
                if (!ReferenceEquals(column, ColSubject) && column.Visibility == Visibility.Visible)
                    column.Width = DataGridLength.Auto;
        }, System.Windows.Threading.DispatcherPriority.Background);
    }

    // ---- message actions -----------------------------------------------------

    List<MessageRow> SelectedRows() => [.. MessageGrid.SelectedItems.OfType<MessageRow>()];

    void OnGridKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Delete)
        {
            e.Handled = true;
            OnDeleteMessages(sender, new RoutedEventArgs());
        }
    }

    void OnMarkRead(object sender, RoutedEventArgs e) => SetRead(true);
    void OnMarkUnread(object sender, RoutedEventArgs e) => SetRead(false);

    async void SetRead(bool isRead)
    {
        var rows = SelectedRows();
        if (rows.Count == 0 || rows[0].Mailbox is not MailboxHandle mailbox) return;
        foreach (var row in rows)
            MailboxStore.SetMessageRead(mailbox.Db, row.Id, isRead);
        RefreshAfterAction(mailbox);
        try
        {
            var graph = await GetGraphAsync(mailbox);
            foreach (var row in rows.Where(r => r.ServerId is not null))
                await graph.Me.Messages[row.ServerId].PatchAsync(
                    new Microsoft.Graph.Models.Message { IsRead = isRead });
            Log($"Marked {rows.Count:N0} message(s) {(isRead ? "read" : "unread")} (server updated).");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"Server update failed (local change kept, next sync reconciles): {ex.Message}");
        }
    }

    async void OnDeleteMessages(object sender, RoutedEventArgs e)
    {
        var rows = SelectedRows();
        if (rows.Count == 0 || rows[0].Mailbox is not MailboxHandle mailbox) return;
        var trash = QueryFolders(mailbox).FirstOrDefault(f => f.Special == "trash");
        if (trash is null)
        {
            Log("No trash folder known — refusing to delete.");
            return;
        }
        foreach (var row in rows)
            MailboxStore.MoveMessageLocal(mailbox.Db, row.Id, trash.Id);
        RefreshAfterAction(mailbox);
        try
        {
            var graph = await GetGraphAsync(mailbox);
            foreach (var row in rows.Where(r => r.ServerId is not null))
                await graph.Me.Messages[row.ServerId].Move.PostAsync(
                    new Microsoft.Graph.Me.Messages.Item.Move.MovePostRequestBody
                    { DestinationId = "deleteditems" });
            Log($"Moved {rows.Count:N0} message(s) to Deleted Items (server updated).");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"Server delete failed (local change kept, next sync reconciles): {ex.Message}");
        }
    }

    void OnMoveToOpened(object sender, RoutedEventArgs e)
    {
        MoveToMenu.Items.Clear();
        var mailbox = SelectedRows().FirstOrDefault()?.Mailbox as MailboxHandle
            ?? (FolderTree.SelectedItem as FolderNodeViewModel)?.Mailbox as MailboxHandle;
        if (mailbox is null) return;
        foreach (var folder in QueryFolders(mailbox).Where(f => f.ServerId is not null))
        {
            var item = new MenuItem { Header = folder.Name };
            item.Click += (_, _) => MoveSelectedTo(mailbox, folder);
            MoveToMenu.Items.Add(item);
        }
    }

    async void MoveSelectedTo(MailboxHandle mailbox, FolderRow target)
    {
        var rows = SelectedRows();
        if (rows.Count == 0) return;
        foreach (var row in rows)
            MailboxStore.MoveMessageLocal(mailbox.Db, row.Id, target.Id);
        RefreshAfterAction(mailbox);
        try
        {
            var graph = await GetGraphAsync(mailbox);
            foreach (var row in rows.Where(r => r.ServerId is not null))
                await graph.Me.Messages[row.ServerId].Move.PostAsync(
                    new Microsoft.Graph.Me.Messages.Item.Move.MovePostRequestBody
                    { DestinationId = target.ServerId });
            Log($"Moved {rows.Count:N0} message(s) to {target.Name} (server updated).");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"Server move failed (local change kept, next sync reconciles): {ex.Message}");
        }
    }

    void RefreshAfterAction(MailboxHandle mailbox)
    {
        UpdateTreeCounts(mailbox);
        if (FolderTree.SelectedItem is FolderNodeViewModel selected &&
            selected.Mailbox is MailboxHandle selectedMailbox && !selected.IsGroupHeader)
            LoadFolder(new FolderNode(selectedMailbox, selected.FolderId, selected.Name));
    }

    /// <summary>Raw access token for calls the typed SDK cannot express (raw-MIME send).</summary>
    async Task<string> GetAccessTokenAsync(MailboxHandle mailbox)
    {
        var auth = new GraphAuthenticator(
            Path.Combine(_scratchRoot!, "msal.cache"),
            () => new WindowInteropHelper(this).Handle);
        var accounts = await auth.GetAccountsAsync();
        var account = accounts.FirstOrDefault(a =>
            string.Equals(a.Username, mailbox.Upn, StringComparison.OrdinalIgnoreCase));
        var result = account is null ? null : await auth.AcquireSilentAsync(account);
        result ??= await auth.SignInInteractiveAsync(mailbox.Upn);
        return result.AccessToken;
    }

    async Task<GraphServiceClient> GetGraphAsync(MailboxHandle mailbox)
    {
        if (_graphClients.TryGetValue(mailbox.Upn, out var cached))
            return cached;
        var auth = new GraphAuthenticator(
            Path.Combine(_scratchRoot!, "msal.cache"),
            () => new WindowInteropHelper(this).Handle);
        var accounts = await auth.GetAccountsAsync();
        var account = accounts.FirstOrDefault(a =>
            string.Equals(a.Username, mailbox.Upn, StringComparison.OrdinalIgnoreCase));
        var token = account is null ? null : await auth.AcquireSilentAsync(account);
        if (token is not null)
            Log(LogLevel.Debug, $"Silent token acquired for {mailbox.Upn}.");
        else
        {
            Log($"Interactive sign-in required for {mailbox.Upn}…");
            token = await auth.SignInInteractiveAsync(mailbox.Upn);
        }
        var client = new GraphServiceClient(
            new BaseBearerTokenAuthenticationProvider(new GraphTokenProvider(auth, token)));
        _graphClients[mailbox.Upn] = client;
        return client;
    }

    // ---- reading pane --------------------------------------------------------

    void OnMessageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (MessageGrid.SelectedItem is not MessageRow row || row.Mailbox is not MailboxHandle mailbox)
            return;
        try
        {
            ShowMessage(mailbox, row.Id);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to load message {row.Id}: {ex.Message}";
            Log(LogLevel.Error, $"Loading message {row.Id} failed: {ex.Message}");
        }
    }

    static readonly string[] KindLabels = ["From", "To", "Cc", "Bcc", "Reply-To", "Sender"];

    void ShowMessage(MailboxHandle mailbox, long messageId)
    {
        _currentMailbox = mailbox;
        _currentMessageId = messageId;

        var envelope = new StringBuilder();
        using (var cmd = mailbox.Db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT ma.kind, a.email, a.display_name
                FROM message_addresses ma JOIN addresses a ON a.id = ma.address_id
                WHERE ma.message_id = @id ORDER BY ma.kind, ma.position;
                """;
            cmd.Parameters.AddWithValue("@id", messageId);
            using var reader = cmd.ExecuteReader();
            var byKind = new Dictionary<long, List<string>>();
            while (reader.Read())
            {
                var email = reader.GetString(1);
                var name = reader.IsDBNull(2) ? null : reader.GetString(2);
                byKind.TryAdd(reader.GetInt64(0), []);
                byKind[reader.GetInt64(0)].Add(
                    string.IsNullOrEmpty(name) || name == email ? email : $"{name} <{email}>");
            }
            for (var kind = 0; kind < KindLabels.Length; kind++)
                if (byKind.TryGetValue(kind, out var list))
                    envelope.AppendLine($"{KindLabels[kind],-8}: {string.Join("; ", list)}");
        }
        using (var cmd = mailbox.Db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT subject, datetime(coalesce(sent_at, received_at), 'unixepoch', 'localtime')
                FROM messages WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", messageId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                envelope.AppendLine($"{"Date",-8}: {(reader.IsDBNull(1) ? "" : reader.GetString(1))}");
                envelope.AppendLine($"{"Subject",-8}: {(reader.IsDBNull(0) ? "" : reader.GetString(0))}");
            }
        }
        EnvelopeText.Text = envelope.ToString().TrimEnd();

        var raw = MailboxStore.GetRawMessage(mailbox.Db, messageId);
        var mime = MimeMessage.Load(new MemoryStream(raw));
        _currentHtml = mime.HtmlBody;
        BodyText.Text = mime.TextBody
            ?? (_currentHtml is null ? "(no text body)" : HtmlText.ToPlainText(_currentHtml));
        HtmlSourceText.Text = _currentHtml ?? "(no HTML body)";

        var headerEnd = FindHeaderEnd(raw);
        HeadersText.Text = Encoding.Latin1.GetString(raw, 0, headerEnd < 0 ? raw.Length : headerEnd);
        RawText.Text = raw.Length <= RawDisplayCap
            ? Encoding.Latin1.GetString(raw)
            : Encoding.Latin1.GetString(raw, 0, RawDisplayCap) +
              $"{Environment.NewLine}… (truncated for display: {raw.Length:N0} bytes total)";

        AttachmentList.ItemsSource = MailboxStore.GetAttachments(mailbox.Db, messageId)
            .Select(a => new AttachmentItem(
                mailbox, a.Id, a.FileName ?? "(unnamed)", a.ContentType ?? "",
                (a.Size / 1024.0).ToString("N0"), a.IsInline ? "yes" : "", a.HasContent))
            .ToList();

        _ = RenderPreviewAsync(); // preview pane is always visible now
    }

    void OnAttachmentSave(object sender, MouseButtonEventArgs e)
    {
        if (AttachmentList.SelectedItem is not AttachmentItem item ||
            item.Mailbox is not MailboxHandle mailbox)
            return;
        if (!item.HasContent)
        {
            Log(LogLevel.Warning, $"Attachment '{item.Name}' has no stored content.");
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = item.Name };
        if (dialog.ShowDialog(this) != true)
            return;
        var content = MailboxStore.GetAttachmentContent(mailbox.Db, item.Id);
        if (content is null)
        {
            Log(LogLevel.Warning, $"Attachment '{item.Name}' content missing.");
            return;
        }
        File.WriteAllBytes(dialog.FileName, content);
        Log($"Saved attachment '{item.Name}' ({content.Length / 1024.0:N0} KB).");
    }

    static int FindHeaderEnd(byte[] raw)
    {
        for (var i = 0; i + 1 < raw.Length; i++)
        {
            if (raw[i] != (byte)'\n') continue;
            if (raw[i + 1] == (byte)'\n') return i + 1;
            if (i + 2 < raw.Length && raw[i + 1] == (byte)'\r' && raw[i + 2] == (byte)'\n') return i + 1;
        }
        return -1;
    }

    // ---- rendered preview (locked-down WebView2) -----------------------------

    async Task RenderPreviewAsync()
    {
        try
        {
            await EnsurePreviewAsync();
            var html = _currentHtml;
            if (html is null)
            {
                var text = System.Net.WebUtility.HtmlEncode(BodyText.Text);
                html = $"<html><body><pre style=\"font-family:Consolas,monospace;white-space:pre-wrap\">{text}</pre></body></html>";
            }
            else
            {
                html = Regex.Replace(html, "cid:", CidHost, RegexOptions.IgnoreCase);
                if (html.Length > PreviewHtmlCap)
                    html = html[..PreviewHtmlCap];
            }
            PreviewView.CoreWebView2.NavigateToString(html);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Preview failed: {ex.Message}");
            StatusText.Text = $"Preview failed: {ex.Message}";
        }
    }

    async Task EnsurePreviewAsync()
    {
        if (_webViewReady)
            return;
        var dataDir = Path.Combine(_scratchRoot ?? Path.GetTempPath(), "webview2");
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: dataDir);
        await PreviewView.EnsureCoreWebView2Async(environment);

        var core = PreviewView.CoreWebView2;
        var settings = core.Settings;
        settings.IsScriptEnabled = false;
        settings.AreDefaultScriptDialogsEnabled = false;
        settings.AreDevToolsEnabled = false;
        settings.IsWebMessageEnabled = false;
        settings.AreHostObjectsAllowed = false;
        settings.IsPasswordAutosaveEnabled = false;
        settings.IsGeneralAutofillEnabled = false;
        settings.IsStatusBarEnabled = false;
        settings.AreDefaultContextMenusEnabled = false;

        core.AddWebResourceRequestedFilter("*", CoreWebView2WebResourceContext.All);
        core.WebResourceRequested += (_, args) =>
        {
            var uri = args.Request.Uri;
            if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase) ||
                uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase))
                return;
            if (uri.StartsWith(CidHost, StringComparison.OrdinalIgnoreCase))
            {
                var contentId = Uri.UnescapeDataString(uri[CidHost.Length..]);
                if (_currentMailbox is not null &&
                    MailboxStore.TryGetInlineAttachment(_currentMailbox.Db, _currentMessageId, contentId)
                        is { } attachment)
                {
                    args.Response = core.Environment.CreateWebResourceResponse(
                        new MemoryStream(attachment.Content), 200, "OK",
                        $"Content-Type: {attachment.ContentType}");
                    return;
                }
                args.Response = core.Environment.CreateWebResourceResponse(null, 404, "Not Found", "");
                return;
            }
            args.Response = core.Environment.CreateWebResourceResponse(null, 403, "Blocked", "");
            Dispatcher.BeginInvoke(() => Log(LogLevel.Verbose, $"Preview blocked: {Truncate(uri, 90)}"));
        };
        core.NavigationStarting += (_, args) =>
        {
            if (args.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                args.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return;
            args.Cancel = true;
            var target = args.Uri;
            Dispatcher.BeginInvoke(() => ConfirmOpenLink(target));
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        _webViewReady = true;
        Log(LogLevel.Debug, "Preview engine initialized (scripts off, network fenced).");
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

    /// <summary>
    /// Opens a link from a message only after showing the real destination, with
    /// a warning when the host uses punycode or an unusual scheme (classic
    /// phishing tricks this client refuses to hide).
    /// </summary>
    void ConfirmOpenLink(string uri)
    {
        Uri.TryCreate(uri, UriKind.Absolute, out var parsed);
        var host = parsed?.Host ?? "(unparseable)";
        var warning = "";
        if (host.Contains("xn--", StringComparison.OrdinalIgnoreCase))
            warning = "\n\nWARNING: this host uses punycode, which can imitate another domain.";
        if (parsed is not null && parsed.Scheme is not ("https" or "http" or "mailto"))
            warning += $"\n\nWARNING: unusual scheme '{parsed.Scheme}'.";

        var answer = MessageBox.Show(
            this,
            $"Open this link in your browser?\n\nHost: {host}\n\nFull URL:\n{uri}{warning}",
            "eeeMail - open link",
            MessageBoxButton.OKCancel,
            warning.Length > 0 ? MessageBoxImage.Warning : MessageBoxImage.Question);
        if (answer != MessageBoxResult.OK)
        {
            Log(LogLevel.Verbose, $"Link declined: {Truncate(uri, 90)}");
            return;
        }
        try
        {
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
            Log($"Opened link externally: {Truncate(uri, 90)}");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Could not open link: {ex.Message}");
        }
    }

    // ---- compose -------------------------------------------------------------

    public static readonly RoutedCommand ComposeCommand = new();
    public static readonly RoutedCommand ReplyCommand = new();
    public static readonly RoutedCommand ReplyAllCommand = new();
    public static readonly RoutedCommand ForwardCommand = new();

    void OnComposeNew(object sender, RoutedEventArgs e) => OpenCompose(null);
    void OnReply(object sender, RoutedEventArgs e) => ReplyToSelected(ReplyKind.Reply);
    void OnReplyAll(object sender, RoutedEventArgs e) => ReplyToSelected(ReplyKind.ReplyAll);
    void OnForward(object sender, RoutedEventArgs e) => ReplyToSelected(ReplyKind.Forward);

    void ReplyToSelected(ReplyKind kind)
    {
        if (MessageGrid.SelectedItem is not MessageRow row || row.Mailbox is not MailboxHandle mailbox)
        {
            StatusText.Text = "Select a message first.";
            return;
        }
        try
        {
            var raw = MailboxStore.GetRawMessage(mailbox.Db, row.Id);
            var original = MimeMessage.Load(new MemoryStream(raw));
            var draft = ReplyBuilder.BuildReply(original, mailbox.Upn, kind);

            if (kind == ReplyKind.Forward)
            {
                // Carry the original's real attachments into the forward.
                var attachments = new List<DraftAttachment>();
                foreach (var stored in MailboxStore.GetAttachments(mailbox.Db, row.Id))
                {
                    if (stored.IsInline || !stored.HasContent) continue;
                    var content = MailboxStore.GetAttachmentContent(mailbox.Db, stored.Id);
                    if (content is null) continue;
                    attachments.Add(new DraftAttachment(
                        stored.FileName ?? "attachment",
                        stored.ContentType ?? "application/octet-stream",
                        content));
                }
                draft = draft with { Attachments = attachments };
            }
            OpenCompose(draft);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Could not build {kind}: {ex.Message}");
            StatusText.Text = $"Could not build {kind}: {ex.Message}";
        }
    }

    void OpenCompose(Draft? seed)
    {
        if (_mailboxes.Count == 0)
        {
            StatusText.Text = "No account configured.";
            return;
        }
        var accounts = _mailboxes.Select(m => m.Upn).ToList();
        var window = new ComposeWindow(accounts, SendAsync, seed) { Owner = this };
        window.ShowDialog();
    }

    /// <summary>
    /// Sends the exact MIME we built (threading headers included) via Graph's
    /// raw-MIME sendMail, so the service does not rebuild the headers for us.
    /// </summary>
    async Task SendAsync(string fromAddress, MimeMessage message)
    {
        var mailbox = _mailboxes.FirstOrDefault(m =>
            string.Equals(m.Upn, fromAddress, StringComparison.OrdinalIgnoreCase))
            ?? throw new InvalidOperationException($"No account for {fromAddress}.");

        using var buffer = new MemoryStream();
        message.WriteTo(buffer);
        var base64 = Convert.ToBase64String(buffer.ToArray());

        // The typed SDK rebuilds the message from its own model, which would
        // discard our threading headers. Post the raw MIME instead: Graph's
        // /sendMail accepts base64 MIME with Content-Type text/plain.
        var token = await GetAccessTokenAsync(mailbox);
        using var http = new System.Net.Http.HttpClient();
        using var request = new System.Net.Http.HttpRequestMessage(
            System.Net.Http.HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/sendMail")
        {
            Content = new System.Net.Http.StringContent(
                base64, System.Text.Encoding.ASCII, "text/plain"),
        };
        request.Headers.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", token);
        var response = await http.SendAsync(request);
        if (!response.IsSuccessStatusCode)
        {
            var detail = await response.Content.ReadAsStringAsync();
            throw new InvalidOperationException(
                $"Graph refused the message ({(int)response.StatusCode}): {Truncate(detail, 300)}");
        }

        Log($"Sent '{message.Subject}' from {fromAddress} to " +
            $"{string.Join(", ", message.To.Mailboxes.Select(m => m.Address))}.");
    }

    // ---- search --------------------------------------------------------------

    void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
            RunSearch();
    }

    void OnSearchClick(object sender, RoutedEventArgs e) => RunSearch();

    void RunSearch()
    {
        var term = SearchBox.Text.Trim();
        if (term.Length == 0)
        {
            SearchBox.Background = System.Windows.Media.Brushes.Transparent;
            if (FolderTree.SelectedItem is FolderNodeViewModel selected &&
                selected.Mailbox is MailboxHandle selectedMailbox && !selected.IsGroupHeader)
                LoadFolder(new FolderNode(selectedMailbox, selected.FolderId, selected.Name));
            return;
        }
        var mailbox = (FolderTree.SelectedItem as FolderNodeViewModel)?.Mailbox as MailboxHandle
            ?? _mailboxes.FirstOrDefault();
        if (mailbox is null) return;
        try
        {
            var compiled = SqliteQueryCompiler.Compile(QueryParser.Parse(term));
            using var cmd = mailbox.Db.CreateCommand();
            cmd.CommandText = RowSelect +
                $" WHERE {compiled.WhereSql} ORDER BY m.received_at DESC LIMIT 500;";
            foreach (var (name, value) in compiled.Parameters)
                cmd.Parameters.AddWithValue(name, value);
            FillList(mailbox, cmd);
            StatusText.Text = $"Search '{term}' in {mailbox.Upn}: {MessageGrid.Items.Count:N0} hit(s)";
            SearchBox.Background = System.Windows.Media.Brushes.Transparent;
        }
        catch (Exception ex) when (ex is QueryParseException or QueryCompilationException)
        {
            SearchBox.Background = new SolidColorBrush(Color.FromRgb(255, 235, 235));
            StatusText.Text = $"Search: {ex.Message}";
            Log(LogLevel.Warning, $"Search query rejected: {ex.Message}");
        }
    }

    // ---- sync ----------------------------------------------------------------

    void OnSyncClick(object sender, RoutedEventArgs e) => StartSync();

    void OnAccountsClick(object sender, RoutedEventArgs e)
    {
        if (_appDb is null || _scratchRoot is null)
            return;
        var window = new AccountsWindow(_appDb, _scratchRoot) { Owner = this };
        window.ShowDialog();
        if (!window.ChangesApplied)
            return;
        Log("Account settings changed — reloading mailboxes.");
        foreach (var mailbox in _mailboxes)
            mailbox.Db.Dispose();
        _mailboxes.Clear();
        _graphClients.Clear();
        ReloadMailboxes();
        StartSync();
    }

    /// <summary>Re-reads the mailbox registry (after config changes) and rebuilds the tree.</summary>
    void ReloadMailboxes()
    {
        if (_appDb is null || _scratchRoot is null)
            return;
        var repoRoot = Path.GetDirectoryName(_scratchRoot)!;
        using var cmd = _appDb.CreateCommand();
        cmd.CommandText = """
            SELECT upn, db_path, dek, coalesce(sync_window_months, 0),
                   coalesce(sync_policy, 'MirrorServer')
            FROM mailboxes WHERE enabled = 1 ORDER BY position, id;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var upn = reader.GetString(0);
            var dbPath = reader.GetString(1);
            if (!Path.IsPathRooted(dbPath))
                dbPath = Path.Combine(repoRoot, dbPath);
            var dek = (byte[])reader.GetValue(2);
            _mailboxes.Add(new MailboxHandle(
                upn, dbPath, dek, reader.GetInt32(3), reader.GetString(4),
                MailboxDatabase.Open(dbPath, dek)));
        }
        BuildTree();
        StatusText.Text = $"{_mailboxes.Count} mailbox(es) open.";
    }

    async void StartSync()
    {
        if (_syncRunning || _scratchRoot is null || _mailboxes.Count == 0)
            return;
        _syncRunning = true;
        SyncButton.IsEnabled = false;
        SyncBar.IsIndeterminate = true;
        SyncLabel.Text = "Sync starting…";
        Log("Sync started.");
        try
        {
            foreach (var mailbox in _mailboxes.ToList())
            {
                var graph = await GetGraphAsync(mailbox);
                var since = mailbox.Since;
                var stopwatch = Stopwatch.StartNew();
                var lastTreeUpdate = 0;
                var lastDownloadLog = 0;
                var lastScanFolder = "";
                var lastScanLog = 0;
                var rateSamples = new Queue<(TimeSpan At, int Downloaded)>();

                void OnProgress(GraphMailboxSync.SyncProgressEvent p) => Dispatcher.BeginInvoke(() =>
                {
                    switch (p.Phase)
                    {
                        case GraphMailboxSync.SyncPhase.Folders:
                            SyncLabel.Text = "Listing folders…";
                            break;
                        case GraphMailboxSync.SyncPhase.Counting:
                            SyncLabel.Text = $"Counting… ({p.FolderIndex:N0}/{p.FolderCount:N0}) {p.FolderName}";
                            if (p.FolderTarget is int folderTarget)
                                Log(LogLevel.Verbose, $"{p.FolderName}: {folderTarget:N0} message(s) to fetch.");
                            break;
                        case GraphMailboxSync.SyncPhase.Scanning:
                            ScanLabel.Text =
                                $"{p.FolderName} ({p.FolderIndex:N0}/{p.FolderCount:N0}): " +
                                $"scanned {p.FolderDownloaded:N0}";
                            if (p.FolderName != lastScanFolder)
                            {
                                lastScanFolder = p.FolderName ?? "";
                                lastScanLog = 0;
                                Log(LogLevel.Verbose, $"{p.FolderName}: listing…");
                            }
                            if (p.FolderDownloaded - lastScanLog >= 500)
                            {
                                lastScanLog = p.FolderDownloaded;
                                Log(LogLevel.Verbose, $"{p.FolderName}: {p.FolderDownloaded:N0} item(s) scanned…");
                            }
                            break;
                        case GraphMailboxSync.SyncPhase.Throttled:
                            // FolderDownloaded carries the new worker limit.
                            Log(LogLevel.Warning,
                                $"Concurrency now {p.FolderDownloaded:N0} worker(s) (429 backoff / recovery).");
                            break;
                        case GraphMailboxSync.SyncPhase.Downloading:
                            if (p.OverallTarget is int total && total > 0)
                            {
                                SyncBar.IsIndeterminate = false;
                                SyncBar.Maximum = total;
                                SyncBar.Value = Math.Min(p.OverallDownloaded, total);
                            }
                            // Rate over a sliding ~90s window so listing/resume phases
                            // and stalls don't poison the ETA.
                            var eta = "";
                            var now = stopwatch.Elapsed;
                            rateSamples.Enqueue((now, p.OverallDownloaded));
                            while (rateSamples.Count > 2 && now - rateSamples.Peek().At > TimeSpan.FromSeconds(90))
                                rateSamples.Dequeue();
                            var oldest = rateSamples.Peek();
                            var windowSeconds = (now - oldest.At).TotalSeconds;
                            if (p.OverallTarget is int t && windowSeconds > 5 &&
                                p.OverallDownloaded > oldest.Downloaded)
                            {
                                var rate = (p.OverallDownloaded - oldest.Downloaded) / windowSeconds;
                                var remaining = Math.Max(t - p.OverallDownloaded, 0);
                                eta = $" · {rate:N1}/s · ETA {TimeSpan.FromSeconds(remaining / Math.Max(rate, 0.01)):hh\\:mm\\:ss}";
                            }
                            SyncLabel.Text =
                                $"{p.FolderName} ({p.FolderIndex:N0}/{p.FolderCount:N0}): " +
                                $"{p.FolderDownloaded:N0}/{p.FolderTarget?.ToString("N0") ?? "?"} · " +
                                $"overall {p.OverallDownloaded:N0}/{p.OverallTarget?.ToString("N0") ?? "?"}{eta}";
                            if (p.OverallDownloaded - lastDownloadLog >= 250)
                            {
                                lastDownloadLog = p.OverallDownloaded;
                                Log(LogLevel.Verbose,
                                    $"Downloaded {p.OverallDownloaded:N0}/{p.OverallTarget?.ToString("N0") ?? "?"} so far…");
                            }
                            if (p.OverallDownloaded - lastTreeUpdate >= 50)
                            {
                                lastTreeUpdate = p.OverallDownloaded;
                                UpdateTreeCounts(mailbox);
                            }
                            break;
                        case GraphMailboxSync.SyncPhase.FolderDone:
                            if (p.FolderDownloaded > 0)
                                Log($"{p.FolderName}: +{p.FolderDownloaded:N0} downloaded.");
                            else
                                Log(LogLevel.Verbose, $"{p.FolderName}: up to date.");
                            ScanLabel.Text = "";
                            UpdateTreeCounts(mailbox);
                            break;
                    }
                });

                var sync = new GraphMailboxSync(
                    graph, () => MailboxDatabase.Open(mailbox.DbPath, mailbox.Dek), OnProgress);
                // Task.Run keeps the engine (and its await continuations — page
                // classification, MIME parsing) off the UI dispatcher entirely.
                var stats = await Task.Run(() => sync.SyncAsync(since));
                stopwatch.Stop();
                UpdateTreeCounts(mailbox);
                var summary = stats.Added + stats.Updated + stats.Removed == 0
                    ? $"Up to date ({mailbox.ScopeText}) — {stats.Folders:N0} folders checked in {stopwatch.Elapsed.TotalSeconds:N1}s"
                    : $"Sync complete ({mailbox.ScopeText}): +{stats.Added:N0} ~{stats.Updated:N0} -{stats.Removed:N0}" +
                      (stats.Failed > 0 ? $" ({stats.Failed:N0} failed)" : "") +
                      $" in {stopwatch.Elapsed.TotalMinutes:N1} min";
                SyncLabel.Text = summary;
                Log(summary);
            }
            if (FolderTree.SelectedItem is FolderNodeViewModel selected &&
                selected.Mailbox is MailboxHandle selectedMailbox && !selected.IsGroupHeader)
                LoadFolder(new FolderNode(selectedMailbox, selected.FolderId, selected.Name));
        }
        catch (Exception ex)
        {
            SyncLabel.Text = $"Sync failed: {ex.Message}";
            Log(LogLevel.Error, $"SYNC ERROR: {ex.Message}");
        }
        finally
        {
            _syncRunning = false;
            SyncButton.IsEnabled = true;
            SyncBar.IsIndeterminate = false;
            SyncBar.Value = 0;
            SyncBar.Maximum = 100;
            ScanLabel.Text = "";
        }
    }
}
