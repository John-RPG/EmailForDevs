using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using Mail.Core.Search;
using Mail.Storage;
using Mail.Storage.Database;
using Mail.Storage.Security;
using Mail.Sync.Auth;
using Mail.Sync.Graph;
using Microsoft.Data.Sqlite;
using Microsoft.Graph;
using Microsoft.Kiota.Abstractions.Authentication;

namespace Mail.App;

/// <summary>
/// First shell: folder tree | dense message list | honest reading pane below
/// (plain text, real addresses, headers, byte-exact raw source), with in-app
/// incremental sync reporting into the status bar. Reads the dev profile
/// produced by tools/SyncSmoke.
/// </summary>
public partial class MainWindow : Window
{
    const int RawDisplayCap = 2 * 1024 * 1024;

    sealed record MailboxHandle(string Upn, string DbPath, byte[] Dek, int WindowMonths, SqliteConnection Db);
    sealed record FolderNode(MailboxHandle Mailbox, long FolderId, string Name);
    public sealed record MessageRow(
        object Mailbox, long Id, string FromName, string FromAddress, string To,
        string Received, string SizeKb, string Subject, bool IsUnread);

    readonly List<MailboxHandle> _mailboxes = [];
    readonly Dictionary<(string Upn, long FolderId), TreeViewItem> _folderItems = [];
    SqliteConnection? _appDb;
    string? _scratchRoot;
    bool _syncRunning;

    public MainWindow()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            OpenProfile();
            StartSync();
        };
        Closed += (_, _) => CloseAll();
    }

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
                SELECT upn, db_path, dek, coalesce(sync_window_months, 1)
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
                    upn, dbPath, dek, reader.GetInt32(3), MailboxDatabase.Open(dbPath, dek)));
            }
            BuildTree();
            StatusText.Text = $"{_mailboxes.Count} mailbox(es) open.";
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
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
        foreach (var mailbox in _mailboxes)
            mailbox.Db.Dispose();
        _appDb?.Dispose();
    }

    // ---- folder tree ---------------------------------------------------------

    void BuildTree()
    {
        FolderTree.Items.Clear();
        _folderItems.Clear();
        TreeViewItem? inboxItem = null;
        foreach (var mailbox in _mailboxes)
        {
            var root = new TreeViewItem { Header = mailbox.Upn, IsExpanded = true };
            var rows = QueryFolders(mailbox);
            var localCounts = QueryLocalCounts(mailbox);
            var items = new Dictionary<long, TreeViewItem>();
            foreach (var row in rows)
            {
                var item = new TreeViewItem
                {
                    Header = FolderHeader(row, localCounts),
                    Tag = new FolderNode(mailbox, row.Id, row.Name),
                };
                items[row.Id] = item;
                _folderItems[(mailbox.Upn, row.Id)] = item;
                if (row.Special == "inbox")
                    inboxItem ??= item;
            }
            foreach (var row in rows)
            {
                var parent = row.ParentId is long p && items.TryGetValue(p, out var pi)
                    ? (ItemsControl)pi : root;
                parent.Items.Add(items[row.Id]);
            }
            FolderTree.Items.Add(root);
        }
        if (inboxItem is not null)
            inboxItem.IsSelected = true;
    }

    sealed record FolderRow(long Id, long? ParentId, string Name, string? Special, long ServerTotal, long ServerUnread);

    static List<FolderRow> QueryFolders(MailboxHandle mailbox)
    {
        using var cmd = mailbox.Db.CreateCommand();
        cmd.CommandText = """
            SELECT id, parent_id, name, special_use, total_count, unread_count FROM folders
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
                reader.GetInt64(4), reader.GetInt64(5)));
        return rows;
    }

    static Dictionary<long, (long Total, long Unread)> QueryLocalCounts(MailboxHandle mailbox)
    {
        using var cmd = mailbox.Db.CreateCommand();
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

    /// <summary>"Name (local/serverTotal) (localUnread/serverUnread)" while the local copy
    /// trails the server (downloading / windowed); plain "(unread)" once caught up.</summary>
    static string FolderHeader(FolderRow row, Dictionary<long, (long Total, long Unread)> local)
    {
        var (localTotal, localUnread) = local.GetValueOrDefault(row.Id);
        if (localTotal < row.ServerTotal)
            return $"{row.Name} ({localTotal}/{row.ServerTotal}) ({localUnread}/{row.ServerUnread})";
        return localUnread > 0 ? $"{row.Name} ({localUnread})" : row.Name;
    }

    void UpdateTreeCounts(MailboxHandle mailbox)
    {
        var localCounts = QueryLocalCounts(mailbox);
        foreach (var row in QueryFolders(mailbox))
            if (_folderItems.TryGetValue((mailbox.Upn, row.Id), out var item))
                item.Header = FolderHeader(row, localCounts);
    }

    void OnFolderSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (FolderTree.SelectedItem is TreeViewItem { Tag: FolderNode node })
            LoadFolder(node);
    }

    // ---- message list --------------------------------------------------------

    const string RowSelect = """
        SELECT m.id, m.subject, m.received_at, m.size, m.is_read,
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
        cmd.CommandText = RowSelect + " WHERE m.folder_id = @f ORDER BY m.received_at DESC LIMIT 2000;";
        cmd.Parameters.AddWithValue("@f", node.FolderId);
        FillList(node.Mailbox, cmd);
        StatusText.Text = $"{node.Mailbox.Upn} / {node.Name}: {MessageList.Items.Count} message(s)";
    }

    void FillList(MailboxHandle mailbox, SqliteCommand cmd)
    {
        var rows = new List<MessageRow>();
        using (var reader = cmd.ExecuteReader())
        {
            while (reader.Read())
            {
                var name = reader.IsDBNull(5) ? "" : reader.GetString(5);
                var email = reader.IsDBNull(6) ? "" : reader.GetString(6);
                rows.Add(new MessageRow(
                    mailbox,
                    reader.GetInt64(0),
                    FromName: name == email ? "" : name,
                    FromAddress: email,
                    To: reader.IsDBNull(7) ? "" : reader.GetString(7),
                    Received: reader.IsDBNull(2)
                        ? ""
                        : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2))
                            .ToLocalTime().ToString("yyyy-MM-dd HH:mm"),
                    SizeKb: reader.IsDBNull(3) ? "" : (reader.GetInt64(3) / 1024.0).ToString("N0"),
                    Subject: reader.IsDBNull(1) ? "" : reader.GetString(1),
                    IsUnread: reader.GetInt64(4) == 0));
            }
        }
        MessageList.ItemsSource = rows;
        StretchSubjectColumn();
    }

    void OnListSizeChanged(object sender, SizeChangedEventArgs e) => StretchSubjectColumn();

    void StretchSubjectColumn()
    {
        var fixedWidth = FromNameColumn.ActualWidth + FromAddressColumn.ActualWidth +
                         ToColumn.ActualWidth + ReceivedColumn.ActualWidth + SizeColumn.ActualWidth;
        var remaining = MessageList.ActualWidth - fixedWidth - 35; // scrollbar + chrome
        if (remaining > 120)
            SubjectColumn.Width = remaining;
    }

    // ---- reading pane --------------------------------------------------------

    void OnMessageSelected(object sender, SelectionChangedEventArgs e)
    {
        if (MessageList.SelectedItem is not MessageRow row || row.Mailbox is not MailboxHandle mailbox)
            return;
        try
        {
            ShowMessage(mailbox, row.Id);
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Failed to load message {row.Id}: {ex.Message}";
        }
    }

    static readonly string[] KindLabels = ["From", "To", "Cc", "Bcc", "Reply-To", "Sender"];

    void ShowMessage(MailboxHandle mailbox, long messageId)
    {
        // Real addresses, always — grouped by kind, never contact-substituted.
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

        // Plain text body straight from the FTS store.
        using (var cmd = mailbox.Db.CreateCommand())
        {
            cmd.CommandText = "SELECT body_text FROM messages_fts WHERE rowid = @id;";
            cmd.Parameters.AddWithValue("@id", messageId);
            BodyText.Text = cmd.ExecuteScalar() as string ?? "(no text body)";
        }

        // Headers + raw source, byte-exact from the segment store.
        var raw = MailboxStore.GetRawMessage(mailbox.Db, messageId);
        var headerEnd = FindHeaderEnd(raw);
        HeadersText.Text = Encoding.Latin1.GetString(raw, 0, headerEnd < 0 ? raw.Length : headerEnd);
        RawText.Text = raw.Length <= RawDisplayCap
            ? Encoding.Latin1.GetString(raw)
            : Encoding.Latin1.GetString(raw, 0, RawDisplayCap) +
              $"{Environment.NewLine}… (truncated for display: {raw.Length:N0} bytes total)";
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
            if (FolderTree.SelectedItem is TreeViewItem { Tag: FolderNode node })
                LoadFolder(node);
            return;
        }
        var mailbox = (FolderTree.SelectedItem as TreeViewItem)?.Tag is FolderNode selected
            ? selected.Mailbox
            : _mailboxes.FirstOrDefault();
        if (mailbox is null) return;
        try
        {
            var compiled = SqliteQueryCompiler.Compile(QueryNode.Text(term));
            using var cmd = mailbox.Db.CreateCommand();
            cmd.CommandText = RowSelect +
                $" WHERE {compiled.WhereSql} ORDER BY m.received_at DESC LIMIT 500;";
            foreach (var (name, value) in compiled.Parameters)
                cmd.Parameters.AddWithValue(name, value);
            FillList(mailbox, cmd);
            StatusText.Text = $"Search '{term}' in {mailbox.Upn}: {MessageList.Items.Count} hit(s)";
        }
        catch (QueryCompilationException ex)
        {
            StatusText.Text = $"Search error: {ex.Message}";
        }
    }

    // ---- sync ----------------------------------------------------------------

    void OnSyncClick(object sender, RoutedEventArgs e) => StartSync();

    async void StartSync()
    {
        if (_syncRunning || _scratchRoot is null || _mailboxes.Count == 0)
            return;
        _syncRunning = true;
        SyncButton.IsEnabled = false;
        SyncBar.IsIndeterminate = true;
        SyncLabel.Text = "Sync starting…";
        try
        {
            var auth = new GraphAuthenticator(
                Path.Combine(_scratchRoot, "msal.cache"),
                () => new WindowInteropHelper(this).Handle);
            foreach (var mailbox in _mailboxes.ToList())
            {
                var accounts = await auth.GetAccountsAsync();
                var account = accounts.FirstOrDefault(a =>
                    string.Equals(a.Username, mailbox.Upn, StringComparison.OrdinalIgnoreCase));
                var token = account is null ? null : await auth.AcquireSilentAsync(account);
                token ??= await auth.SignInInteractiveAsync(mailbox.Upn);

                var graph = new GraphServiceClient(
                    new BaseBearerTokenAuthenticationProvider(new GraphTokenProvider(auth, token)));
                var since = DateTimeOffset.UtcNow.AddMonths(-mailbox.WindowMonths);
                var stats = await Task.Run(async () =>
                {
                    // The sync task gets its own connection; WAL keeps UI reads happy.
                    using var syncDb = MailboxDatabase.Open(mailbox.DbPath, mailbox.Dek);
                    var sync = new GraphMailboxSync(graph, syncDb,
                        log: line => Dispatcher.BeginInvoke(() => SyncLabel.Text = line.Trim()),
                        onFolderSynced: () => Dispatcher.BeginInvoke(() => UpdateTreeCounts(mailbox)));
                    return await sync.SyncAsync(since);
                });
                UpdateTreeCounts(mailbox);
                SyncLabel.Text = $"{mailbox.Upn}: +{stats.Added} ~{stats.Updated} -{stats.Removed}" +
                                 (stats.Failed > 0 ? $" ({stats.Failed} failed)" : "");
            }
            if (FolderTree.SelectedItem is TreeViewItem { Tag: FolderNode node })
                LoadFolder(node);
        }
        catch (Exception ex)
        {
            SyncLabel.Text = $"Sync failed: {ex.Message}";
        }
        finally
        {
            _syncRunning = false;
            SyncButton.IsEnabled = true;
            SyncBar.IsIndeterminate = false;
            SyncBar.Value = 0;
        }
    }
}
