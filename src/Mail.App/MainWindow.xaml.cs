using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
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
        object Mailbox, long Id, string? ServerId, string From, string FromName,
        string FromAddress, string To, string Received, string SizeKb, double SizeVal,
        string Subject, bool IsUnread);

    readonly List<MailboxHandle> _mailboxes = [];
    readonly Dictionary<(string Upn, long FolderId), TreeViewItem> _folderItems = [];
    readonly Dictionary<string, GraphServiceClient> _graphClients = [];
    readonly ObservableCollection<string> _log = [];
    SqliteConnection? _appDb;
    string? _scratchRoot;
    bool _syncRunning;
    bool _webViewReady;
    string? _currentHtml;
    bool _previewDirty;
    MailboxHandle? _currentMailbox;
    long _currentMessageId;

    public MainWindow()
    {
        InitializeComponent();
        ActivityLog.ItemsSource = _log;
        Loaded += (_, _) =>
        {
            OpenProfile();
            BuildColumnsMenu();
            StartSync();
        };
        Closed += (_, _) => CloseAll();
    }

    void Log(string line)
    {
        _log.Add($"{DateTime.Now:HH:mm:ss}  {line}");
        while (_log.Count > 500)
            _log.RemoveAt(0);
        if (ActivityLog.Items.Count > 0)
            ActivityLog.ScrollIntoView(ActivityLog.Items[^1]);
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
            Log($"Profile opened: {_mailboxes.Count} mailbox(es).");
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            Log($"ERROR: {ex.Message}");
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
            };
            ColumnsMenu.Items.Add(item);
        }
    }

    // ---- folder tree ---------------------------------------------------------

    void BuildTree()
    {
        FolderTree.Items.Clear();
        _folderItems.Clear();
        TreeViewItem? inboxItem = null;
        foreach (var mailbox in _mailboxes)
        {
            var root = new TreeViewItem
            {
                Header = $"{mailbox.Upn}  [{mailbox.ScopeText}]",
                IsExpanded = true,
            };
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

    sealed record FolderRow(
        long Id, long? ParentId, string Name, string? Special, string? ServerId,
        long ServerTotal, long ServerUnread);

    static List<FolderRow> QueryFolders(MailboxHandle mailbox)
    {
        using var cmd = mailbox.Db.CreateCommand();
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

    static string FolderHeader(FolderRow row, Dictionary<long, (long Total, long Unread)> local)
    {
        var (localTotal, localUnread) = local.GetValueOrDefault(row.Id);
        if (localTotal < row.ServerTotal)
            return $"{row.Name} ({localTotal:N0}/{row.ServerTotal:N0}) ({localUnread:N0}/{row.ServerUnread:N0})";
        return localUnread > 0 ? $"{row.Name} ({localUnread:N0})" : row.Name;
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
        SELECT m.id, m.subject, m.received_at, m.size, m.is_read, m.server_id,
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
                var name = reader.IsDBNull(6) ? "" : reader.GetString(6);
                var email = reader.IsDBNull(7) ? "" : reader.GetString(7);
                var sizeVal = reader.IsDBNull(3) ? 0 : reader.GetInt64(3) / 1024.0;
                rows.Add(new MessageRow(
                    mailbox,
                    reader.GetInt64(0),
                    reader.IsDBNull(5) ? null : reader.GetString(5),
                    From: string.IsNullOrEmpty(name) || name == email ? email : $"{name} <{email}>",
                    FromName: name == email ? "" : name,
                    FromAddress: email,
                    To: reader.IsDBNull(8) ? "" : reader.GetString(8),
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
            Log($"Server update failed (local change kept, next sync reconciles): {ex.Message}");
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
            Log($"Server delete failed (local change kept, next sync reconciles): {ex.Message}");
        }
    }

    void OnMoveToOpened(object sender, RoutedEventArgs e)
    {
        MoveToMenu.Items.Clear();
        var mailbox = SelectedRows().FirstOrDefault()?.Mailbox as MailboxHandle;
        if (mailbox is null && (FolderTree.SelectedItem as TreeViewItem)?.Tag is FolderNode node)
            mailbox = node.Mailbox;
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
            Log($"Server move failed (local change kept, next sync reconciles): {ex.Message}");
        }
    }

    void RefreshAfterAction(MailboxHandle mailbox)
    {
        UpdateTreeCounts(mailbox);
        if (FolderTree.SelectedItem is TreeViewItem { Tag: FolderNode node })
            LoadFolder(node);
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
        token ??= await auth.SignInInteractiveAsync(mailbox.Upn);
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
            Log($"ERROR loading message {row.Id}: {ex.Message}");
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

        _previewDirty = true;
        if (ReadingTabs.SelectedIndex == 0)
            _ = RenderPreviewAsync();
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

    void OnReadingTabChanged(object sender, SelectionChangedEventArgs e)
    {
        if (ReferenceEquals(e.Source, ReadingTabs) && ReadingTabs.SelectedIndex == 0 && _previewDirty)
            _ = RenderPreviewAsync();
    }

    async Task RenderPreviewAsync()
    {
        try
        {
            await EnsurePreviewAsync();
            _previewDirty = false;
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
            Log($"Preview failed: {ex.Message}");
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
            Dispatcher.BeginInvoke(() => Log($"Preview blocked: {Truncate(uri, 90)}"));
        };
        core.NavigationStarting += (_, args) =>
        {
            if (args.Uri.StartsWith("about:", StringComparison.OrdinalIgnoreCase) ||
                args.Uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                return;
            args.Cancel = true;
            Dispatcher.BeginInvoke(() =>
            {
                StatusText.Text = $"Link blocked (opens externally in a later build): {args.Uri}";
                Log($"Link click blocked: {Truncate(args.Uri, 90)}");
            });
        };
        core.NewWindowRequested += (_, args) => args.Handled = true;
        _webViewReady = true;
        Log("Preview engine initialized (scripts off, network fenced).");
    }

    static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";

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
            StatusText.Text = $"Search '{term}' in {mailbox.Upn}: {MessageGrid.Items.Count:N0} hit(s)";
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
        Log("Sync started.");
        try
        {
            foreach (var mailbox in _mailboxes.ToList())
            {
                var graph = await GetGraphAsync(mailbox);
                var since = mailbox.Since;
                var stopwatch = Stopwatch.StartNew();
                var lastTreeUpdate = 0;

                void OnProgress(GraphMailboxSync.SyncProgressEvent p) => Dispatcher.BeginInvoke(() =>
                {
                    switch (p.Phase)
                    {
                        case GraphMailboxSync.SyncPhase.Folders:
                            SyncLabel.Text = "Listing folders…";
                            break;
                        case GraphMailboxSync.SyncPhase.Counting:
                            SyncLabel.Text = $"Counting… ({p.FolderIndex:N0}/{p.FolderCount:N0}) {p.FolderName}";
                            break;
                        case GraphMailboxSync.SyncPhase.Throttled:
                            Log($"Throttled (429) — download workers reduced to {p.FolderDownloaded:N0}.");
                            break;
                        case GraphMailboxSync.SyncPhase.Downloading:
                            if (p.OverallTarget is int total && total > 0)
                            {
                                SyncBar.IsIndeterminate = false;
                                SyncBar.Maximum = total;
                                SyncBar.Value = Math.Min(p.OverallDownloaded, total);
                            }
                            var eta = "";
                            if (p.OverallTarget is int t && p.OverallDownloaded > 5)
                            {
                                var rate = p.OverallDownloaded / Math.Max(stopwatch.Elapsed.TotalSeconds, 0.1);
                                var remaining = Math.Max(t - p.OverallDownloaded, 0);
                                eta = $" · {rate:N1}/s · ETA {TimeSpan.FromSeconds(remaining / Math.Max(rate, 0.1)):hh\\:mm\\:ss}";
                            }
                            SyncLabel.Text =
                                $"{p.FolderName} ({p.FolderIndex:N0}/{p.FolderCount:N0}): " +
                                $"{p.FolderDownloaded:N0}/{p.FolderTarget?.ToString("N0") ?? "?"} · " +
                                $"overall {p.OverallDownloaded:N0}/{p.OverallTarget?.ToString("N0") ?? "?"}{eta}";
                            if (p.OverallDownloaded - lastTreeUpdate >= 50)
                            {
                                lastTreeUpdate = p.OverallDownloaded;
                                UpdateTreeCounts(mailbox);
                            }
                            break;
                        case GraphMailboxSync.SyncPhase.FolderDone:
                            if (p.FolderDownloaded > 0)
                                Log($"{p.FolderName}: +{p.FolderDownloaded:N0}");
                            UpdateTreeCounts(mailbox);
                            break;
                    }
                });

                var sync = new GraphMailboxSync(
                    graph, () => MailboxDatabase.Open(mailbox.DbPath, mailbox.Dek), OnProgress);
                var stats = await sync.SyncAsync(since);
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
            if (FolderTree.SelectedItem is TreeViewItem { Tag: FolderNode node })
                LoadFolder(node);
        }
        catch (Exception ex)
        {
            SyncLabel.Text = $"Sync failed: {ex.Message}";
            Log($"SYNC ERROR: {ex.Message}");
        }
        finally
        {
            _syncRunning = false;
            SyncButton.IsEnabled = true;
            SyncBar.IsIndeterminate = false;
        }
    }
}
