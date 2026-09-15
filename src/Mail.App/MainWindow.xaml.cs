using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
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
using Mail.Storage.Settings;
using Mail.Storage.Security;
using AvalonDock.Layout;
using Mail.Sync.Auth;
using Microsoft.Identity.Client;
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
        /// <summary>Registry row id, for config edits and removal.</summary>
        public long Id { get; init; }

        /// <summary>Owning account row id, for resolving settings up the chain.</summary>
        public long AccountId { get; init; }

        /// <summary>
        /// The signed-in account whose token opens this mailbox. For a shared
        /// mailbox this is somebody else's address — which is precisely why it
        /// is tracked: it decides who we authenticate as to reach it.
        /// </summary>
        public string AccountUpn { get; init; } = "";

        public string Kind { get; init; } = "primary";
        public bool IsShared => Kind != "primary";
        public string DisplayName { get; init; } = "";

        public bool IsFullMirror => Policy == "MirrorServer" || WindowMonths <= 0;
        public string ScopeText => IsFullMirror ? "full mirror" : $"scoped: last {WindowMonths} month(s)";
        public DateTimeOffset? Since =>
            IsFullMirror ? null : DateTimeOffset.UtcNow.AddMonths(-WindowMonths);
    }

    sealed record FolderNode(MailboxHandle Mailbox, long FolderId, string Name);

    /// <summary>The folder the list is showing, for settings that resolve per folder.</summary>
    FolderNode? _openFolder;

    /// <summary>
    /// Where a column change made through the UI is written. The application, so
    /// that dragging a header is not silently a per-folder override; a developer
    /// wanting narrower scope sets it in the settings tree, and that override
    /// still wins when it is read back.
    /// </summary>
    static readonly SettingTarget _columnScope = SettingTarget.Application;

    public sealed record MessageRow(
        object Mailbox, long Id, string? ServerId, string Status, string From, string FromName,
        string FromAddress, string To, string Received, string SizeKb, double SizeVal,
        string Subject, bool IsUnread)
    {
        /// <summary>Sort key for the Received column: the real instant, not the
        /// formatted string, so sorting survives a format change.</summary>
        public DateTimeOffset? ReceivedValue { get; init; }

        /// <summary>
        /// What a screen reader announces for the row, and what UI automation
        /// sees as its name. Without this the record's generated ToString()
        /// leaks the entire object graph, including the mailbox handle.
        /// </summary>
        public override string ToString() =>
            $"{(IsUnread ? "Unread. " : "")}From {FromName}, {Received}: {Subject}";
    }

    public sealed record AttachmentItem(
        object Mailbox, long Id, string Name, string ContentType, string SizeKb,
        string Inline, bool HasContent)
    {
        public long SizeBytes { get; init; }
        public bool IsInline { get; init; }

        /// <summary>What a screen reader announces, rather than the class name.</summary>
        public override string ToString() =>
            $"{Name}, {ContentType}, {SizeKb} KB{(IsInline ? ", inline" : "")}";
    }

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
        /// <summary>
        /// Resolved from the theme on each read. Black text on the dark theme's
        /// ground is unreadable, and a brush captured once would not follow a
        /// theme switch.
        /// </summary>
        public Brush Brush => Level switch
        {
            LogLevel.Debug => Themed("Text.Secondary", Brushes.Gray),
            LogLevel.Verbose => Themed("Text.Secondary", Brushes.Gray),
            LogLevel.Warning => Themed("Risk.Performance", Brushes.DarkOrange),
            LogLevel.Error => Themed("Risk.Destructive", Brushes.Firebrick),
            _ => Themed("Text.Primary", Brushes.Black),
        };
    
        /// <summary>
        /// What a screen reader announces for a log row. The generated
        /// ToString() would read out the whole record shape instead.
        /// </summary>
        public override string ToString() =>
            $"{At:HH:mm:ss} {Level}: {Message}";
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
    /// <summary>Root of the user's data: profile, mailbox databases, caches.</summary>
    string? _dataDir;
    bool _webViewReady;
    string? _currentHtml;
    MailboxHandle? _currentMailbox;

    /// <summary>Shared for Autodiscover; one per process, not one per call.</summary>
    readonly HttpClient _http = new();

    /// <summary>
    /// Mailboxes with a sync in flight. Per mailbox rather than one global flag:
    /// Graph throttles per mailbox, so two mailboxes do not compete, and a newly
    /// added one should not wait behind a six-figure backfill of another.
    /// </summary>
    readonly HashSet<string> _syncing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Folder each mailbox is on, so a failure can say where it stopped.</summary>
    readonly Dictionary<string, string> _syncingFolders = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Resolves settings through folder → mailbox → account → application.</summary>
    SettingsStore? _settings;

    /// <summary>Unsent messages, so closing compose does not discard them.</summary>
    DraftStore? _drafts;
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
        CommandBindings.Add(new CommandBinding(DeleteCommand, (s2, e2) => OnDeleteMessages(s2, e2)));
        CommandBindings.Add(new CommandBinding(MarkReadCommand, (_, _) => SetRead(true)));
        CommandBindings.Add(new CommandBinding(MarkUnreadCommand, (_, _) => SetRead(false)));
        CommandBindings.Add(new CommandBinding(SyncCommand, (s2, e2) => OnSyncClick(s2, e2)));
        CommandBindings.Add(new CommandBinding(SettingsCommand, (s2, e2) => OnSettingsClick(s2, e2)));
        CommandBindings.Add(new CommandBinding(FocusSearchCommand, (_, _) =>
        {
            SearchBox.Focus();
            SearchBox.SelectAll();
        }));
        Loaded += (_, _) =>
        {
            OpenProfile();
            // The HWND exists only now, and the title bar is drawn from it.
            Themes.ThemeManager.ApplyTitleBar(this);

            // Not awaited: a slow or unreachable GitHub must not hold up the
            // window, and the result only reveals a status bar button.
            if (_settings?.GetBool(
                    SettingsCatalog.CheckForUpdates, SettingTarget.Application) == true)
                _ = CheckForUpdatesAsync(announceResult: false);
            ApplyColumnLayout(null);
            ApplyRowDensity(null);
            BuildColumnsMenu();
            UpdateDraftsButton();
            RestoreLayout();
            SyncPaneMenuState();
            // Only after restoring, or loading the saved layout would itself
            // trigger a save of the half-applied state.
            Dock.LayoutChanged += (_, _) =>
            {
                SyncPaneMenuState();
                QueueLayoutSave();
            };

            // LayoutChanged covers structural moves but not a pane being shown
            // or hidden, so each anchorable is watched directly — otherwise
            // closing a pane and quitting brings it back next launch.
            foreach (var pane in Dock.Layout.Descendents()
                         .OfType<AvalonDock.Layout.LayoutAnchorable>())
            {
                pane.IsVisibleChanged += (_, _) =>
                {
                    SyncPaneMenuState();
                    QueueLayoutSave();
                };
            }
            SyncThemeMenuState();
            StartSync();
            // Delta is pull-only and Graph has no push route for a desktop
            // client — its change notifications require a public HTTPS endpoint
            // — so new mail is found by asking. The interval is configurable,
            // and returning to the window also triggers a check, which is what
            // makes the poll interval matter less than it otherwise would.
            StartAutoSync();
            StartLiveUpdates();
        };
        Activated += OnWindowActivated;
        Closing += (_, _) => SaveLayout();
        Closed += (_, _) => CloseAll();

        // Saving only on close loses the arrangement to a crash or a forced
        // quit. Debounced so dragging a splitter does not write on every frame.
        // Long enough that the pane has finished moving into AvalonDock's
        // hidden collection before the snapshot is taken, and that dragging a
        // splitter does not write on every frame.
        _layoutSaveTimer.Interval = TimeSpan.FromSeconds(5);
        _layoutSaveTimer.Tick += (_, _) =>
        {
            _layoutSaveTimer.Stop();
            SaveLayout();
        };
    }

    /// <summary>
    /// Starts (or restarts) the periodic check, honouring the configured
    /// interval. Zero disables it, leaving Sync now and the focus check.
    /// </summary>
    void StartAutoSync()
    {
        _autoSyncTimer?.Stop();
        _doorbell?.Cancel();
        var seconds = _settings?.GetInt(
            SettingsCatalog.AutoSyncSeconds, SettingTarget.Application) ?? 120;
        if (seconds <= 0)
        {
            Log(LogLevel.Verbose, "Automatic mail checks are off.");
            return;
        }

        // Below a minute the service throttles, which delays mail rather than
        // hastening it — so the floor is enforced rather than merely documented.
        seconds = Math.Max(seconds, 30);
        _autoSyncTimer = new System.Windows.Threading.DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(seconds),
        };
        _autoSyncTimer.Tick += (_, _) => StartSync();
        _autoSyncTimer.Start();
        Log(LogLevel.Verbose, $"Checking for new mail every {seconds:N0}s.");
    }

    /// <summary>
    /// Returning to the window is a good moment to check: it is when the user
    /// is about to look, and it costs one delta call against a cheap endpoint.
    /// Rate-limited so alt-tabbing repeatedly does not hammer the service.
    /// </summary>
    void OnWindowActivated(object? sender, EventArgs e)
    {
        bool busy;
        lock (_syncing) busy = _syncing.Count > 0;
        if (_settings is null || busy) return;
        if (!_settings.GetBool(SettingsCatalog.SyncOnFocus, SettingTarget.Application)) return;
        if (DateTimeOffset.UtcNow - _lastFocusSync < TimeSpan.FromSeconds(30)) return;
        _lastFocusSync = DateTimeOffset.UtcNow;
        StartSync();
    }

    DateTimeOffset _lastFocusSync = DateTimeOffset.MinValue;

    CancellationTokenSource? _doorbell;

    /// <summary>Debounces layout saves; see the constructor.</summary>
    readonly System.Windows.Threading.DispatcherTimer _layoutSaveTimer = new();

    /// <summary>
    /// Starts the live-update loop: hold a long poll open per account, and run a
    /// delta pass whenever the server reports a change.
    ///
    /// This is not polling. EWS streaming notifications keep the request open
    /// until something happens or the window closes, which is the only push-like
    /// route available to a desktop client — Graph's own notifications need a
    /// public HTTPS endpoint to deliver to.
    /// </summary>
    void StartLiveUpdates()
    {
        _doorbell?.Cancel();
        _doorbell = new CancellationTokenSource();
        var token = _doorbell.Token;

        foreach (var accountUpn in _mailboxes
                     .Select(m => m.AccountUpn.Length > 0 ? m.AccountUpn : m.Upn)
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (_settings is not null &&
                !_settings.GetBool(SettingsCatalog.LiveUpdates, SettingTarget.Application))
                continue;
            _ = RunDoorbellAsync(accountUpn, token);
        }
    }

    async Task RunDoorbellAsync(string accountUpn, CancellationToken ct)
    {
        // Live updates need the Exchange audience, which the mailbox-lookup
        // capability already grants. Without it, the periodic check stands.
        if (_appDb is null ||
            !MailboxRegistry.HasCapability(_appDb, accountUpn, AccountCapability.MappedLookup.Id))
        {
            Log(LogLevel.Verbose,
                $"{accountUpn}: live updates need the mailbox-lookup permission; " +
                "using periodic checks instead.");
            return;
        }

        var stream = new MailboxEventStream(_http);
        MailboxEventStream.Subscription? subscription = null;
        var failures = 0;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                var token = await ExchangeTokenAsync(accountUpn);
                if (token is null)
                {
                    Log(LogLevel.Verbose, $"{accountUpn}: no Exchange token; live updates off.");
                    return;
                }

                subscription ??= await stream.SubscribeAsync(accountUpn, token, accountUpn, ct);
                if (subscription is null)
                {
                    Log(LogLevel.Verbose,
                        $"{accountUpn}: the server declined a live subscription; " +
                        "using periodic checks.");
                    return;
                }

                // Sync the moment events arrive, not when the window closes —
                // otherwise a long window delays mail rather than delivering it.
                var result = await stream.WaitForEventsAsync(
                    subscription, token,
                    onEvents: events => Dispatcher.InvokeAsync(() =>
                    {
                        Log(LogLevel.Info,
                            $"{accountUpn}: server reported {string.Join(", ", events)} — syncing.");
                        StartSync();
                    }),
                    ct: ct);
                if (ct.IsCancellationRequested) return;
                if (result.SubscriptionLapsed) subscription = null;   // re-subscribe
                failures = 0;
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                // Back off rather than spinning: a laptop waking or a network
                // change drops the connection, and retrying instantly helps
                // nobody.
                subscription = null;
                failures++;
                if (failures >= 5)
                {
                    Log(LogLevel.Warning,
                        $"{accountUpn}: live updates stopped after repeated failures " +
                        $"({ex.Message}). Periodic checks continue.");
                    return;
                }
                try { await Task.Delay(TimeSpan.FromSeconds(15 * failures), ct); }
                catch (OperationCanceledException) { return; }
            }
        }
    }

    /// <summary>Silent-only Exchange token; live updates must never cause a prompt.</summary>
    async Task<string?> ExchangeTokenAsync(string accountUpn)
    {
        try
        {
            var auth = new GraphAuthenticator(
                Path.Combine(_dataDir!, "msal.cache"),
                () => new WindowInteropHelper(this).Handle);
            var accounts = await auth.GetAccountsAsync();
            var account = accounts.FirstOrDefault(a =>
                string.Equals(a.Username, accountUpn, StringComparison.OrdinalIgnoreCase));
            if (account is null) return null;
            var result = await auth.AcquireSilentAsync(account, GraphAuthenticator.ExchangeScopes);
            return result?.AccessToken;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Applies the configured theme. Called at startup and after settings
    /// change, so switching restyles open windows without a restart.
    /// </summary>
    void ApplyTheme()
    {
        var mode = _settings?.GetString(SettingsCatalog.ThemeMode, SettingTarget.Application)
            ?? "system";
        Themes.ThemeManager.Apply(Themes.ThemeManager.Parse(mode));

        // AvalonDock carries its own theme for the parts hand-written styles
        // cannot reach — pane menus, drop targets, the auto-hide strip and the
        // window chrome of floating panes. Setting it is what fixes the light
        // patches that survived the app palette.
        Dock.Theme = Themes.ThemeManager.IsDark
            ? new AvalonDock.Themes.Vs2013DarkTheme()
            : new AvalonDock.Themes.Vs2013LightTheme();
        Log(LogLevel.Verbose,
            $"Theme: {mode}{(mode == "system" ? $" (Windows is {(Themes.ThemeManager.IsDark ? "dark" : "light")})" : "")}.");
    }

    // ---- menu ---------------------------------------------------------------

    void OnExit(object sender, RoutedEventArgs e) => Close();

    void OnFocusSearchMenu(object sender, RoutedEventArgs e)
    {
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    /// <summary>
    /// Shows or hides a docked pane. Docking lets a pane be closed, and a closed
    /// pane with no way back would be a trap — so every one is listed under View
    /// with its state reflected.
    /// </summary>
    void OnTogglePane(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string contentId) return;
        var pane = Dock.Layout.Descendents()
            .OfType<AvalonDock.Layout.LayoutAnchorable>()
            .FirstOrDefault(a => a.ContentId == contentId);
        if (pane is null) return;

        if (item.IsChecked) pane.Show();
        else pane.Hide();
        QueueLayoutSave();
    }

    /// <summary>
    /// Debounced so dragging a splitter does not write on every frame, but
    /// frequent enough that a crash or forced quit does not lose the layout.
    /// </summary>
    void QueueLayoutSave()
    {
        _layoutSaveTimer.Stop();
        _layoutSaveTimer.Start();
    }

    /// <summary>Keeps the View menu honest when a pane is closed by its own X.</summary>
    void SyncPaneMenuState()
    {
        foreach (var (item, contentId) in new[]
                 {
                     (ViewFolders, "folders"), (ViewReader, "reader"), (ViewLog, "log"),
                 })
        {
            var pane = Dock.Layout.Descendents()
                .OfType<AvalonDock.Layout.LayoutAnchorable>()
                .FirstOrDefault(a => a.ContentId == contentId);
            if (pane is not null) item.IsChecked = pane.IsVisible;
        }
    }

    void OnThemeChosen(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem item || item.Tag is not string mode || _settings is null) return;
        _settings.Set(SettingsCatalog.ThemeMode.Key, SettingTarget.Application, mode);
        ApplyTheme();
        SyncThemeMenuState();
    }

    /// <summary>Radio behaviour without RadioButton groups, which do not bind
    /// cleanly to a setting.</summary>
    void SyncThemeMenuState()
    {
        var mode = _settings?.GetString(SettingsCatalog.ThemeMode, SettingTarget.Application)
            ?? "system";
        ThemeSystem.IsChecked = mode == "system";
        ThemeLight.IsChecked = mode == "light";
        ThemeDark.IsChecked = mode == "dark";
    }

    /// <summary>
    /// Returns the panes to their default arrangement. Docking is powerful
    /// enough to make a mess with, so there has to be a way back.
    /// </summary>
    /// <summary>
    /// Discards the saved arrangement and shows every pane. Docking is powerful
    /// enough to make a mess with, so there has to be a way back.
    /// </summary>
    void OnResetLayout(object sender, RoutedEventArgs e)
    {
        try
        {
            if (File.Exists(LayoutFile)) File.Delete(LayoutFile);
        }
        catch (IOException ex)
        {
            Log(LogLevel.Warning, $"Could not remove the saved layout: {ex.Message}");
        }

        foreach (var pane in Dock.Layout.Descendents()
                     .OfType<AvalonDock.Layout.LayoutAnchorable>())
            pane.Show();
        SyncPaneMenuState();
        Log("Layout reset. Pane sizes return to their defaults on restart.");
    }

    string LayoutFile => Path.Combine(_dataDir ?? ".", "profile", "layout.xml");

    /// <summary>
    /// Saves the pane arrangement. The point of docking is arranging things
    /// once, not every session.
    /// </summary>
    void SaveLayout()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(LayoutFile)!);
            var serializer = new AvalonDock.Serializer.Xml.XmlLayoutSerializer(Dock);
            using var writer = new StreamWriter(LayoutFile);
            serializer.Serialize(writer);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Verbose, $"Could not save the layout: {ex.Message}");
        }
    }

    void RestoreLayout()
    {
        if (!File.Exists(LayoutFile)) return;
        try
        {
            var serializer = new AvalonDock.Serializer.Xml.XmlLayoutSerializer(Dock);
            using var reader = new StreamReader(LayoutFile);
            serializer.Deserialize(reader);
            Log(LogLevel.Verbose, "Layout restored.");
        }
        catch (Exception ex)
        {
            // A layout saved by an older build can name panes that no longer
            // exist. Falling back to the default beats refusing to start.
            Log(LogLevel.Warning, $"Saved layout could not be used: {ex.Message}");
        }
    }

    void OnShowSearchHelp(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "Bare words search the full text of every message.\n\n" +
            "Fields:  from: to: cc: bcc: subject: body: filename: in:\n" +
            "Flags:   is:unread  is:read  is:flagged  has:attachment\n" +
            "Dates:   after:2026-01-01  before:yesterday  on:today\n" +
            "Size:    larger:2mb  smaller:100kb\n" +
            "Logic:   AND is implied; use OR, and - or NOT to exclude\n" +
            "Groups:  parentheses, e.g. from:john (is:unread OR has:attachment)\n" +
            "Phrases: \"exact wording\"\n" +
            "Wildcard: * matches any run of characters; \\* for a literal one",
            "eeeMail — search syntax", MessageBoxButton.OK, MessageBoxImage.Information);

    void OnShowShortcuts(object sender, RoutedEventArgs e) =>
        MessageBox.Show(this,
            "Ctrl+N        New message\n" +
            "Ctrl+R        Reply\n" +
            "Ctrl+Shift+R  Reply all\n" +
            "Ctrl+F        Forward\n" +
            "Del           Delete\n" +
            "Ctrl+Q        Mark as read\n" +
            "Ctrl+U        Mark as unread\n" +
            "Ctrl+E        Search\n" +
            "F5            Sync now\n" +
            "Ctrl+,        Settings",
            "eeeMail — keyboard shortcuts", MessageBoxButton.OK, MessageBoxImage.Information);

    void OnShowAbout(object sender, RoutedEventArgs e)
    {
        var version = System.Reflection.Assembly.GetExecutingAssembly()
            .GetName().Version?.ToString() ?? "dev";
        MessageBox.Show(this,
            $"eeeMail {version}\n\n" +
            "A mail client that shows you what actually arrived: real addresses, " +
            "real headers, real MIME.\n\n" +
            "Mail is stored locally in encrypted SQLite databases, one per " +
            "mailbox, with content-addressed deduplication.",
            "About eeeMail", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    // ---- updates -------------------------------------------------------------

    /// <summary>
    /// The release found by the last check, held so the status bar button and
    /// the menu item both act on the same thing rather than re-fetching.
    /// </summary>
    Mail.Core.Update.ReleaseInfo? _availableUpdate;

    /// <summary>
    /// Shared for update traffic, because HttpClient is meant to be long-lived:
    /// one per request exhausts sockets under repeated checks.
    /// </summary>
    static readonly HttpClient _updateHttp = new() { Timeout = TimeSpan.FromMinutes(10) };

    /// <summary>Where the download is staged. Under the profile, not the system temp.</summary>
    string UpdateStagingDirectory =>
        Path.Combine(_dataDir ?? ".", "profile", "update");

    /// <summary>
    /// Looks for a newer release. Quiet by default: a launch check that cannot
    /// reach GitHub says nothing, because failing to check is not news. Asked
    /// for explicitly from the menu, it reports either way.
    /// </summary>
    async Task CheckForUpdatesAsync(bool announceResult)
    {
        if (_settings is null) return;
        var repo = _settings.GetString(
            SettingsCatalog.UpdateRepository, SettingTarget.Application);
        var parts = repo?.Split('/', StringSplitOptions.TrimEntries) ?? [];
        if (parts.Length != 2 || parts.Any(string.IsNullOrEmpty))
        {
            Log(LogLevel.Warning, $"Update source '{repo}' is not owner/repo; skipping check.");
            return;
        }

        try
        {
            var checker = new Mail.Core.Update.UpdateChecker(_updateHttp, parts[0], parts[1]);
            var latest = await checker.LatestAsync();
            var running = System.Reflection.Assembly.GetExecutingAssembly().GetName().Version
                ?? new Version(0, 0, 0);

            if (latest is null || !Mail.Core.Update.UpdateChecker.IsNewer(latest.Version, running))
            {
                _availableUpdate = null;
                UpdateButton.Visibility = Visibility.Collapsed;
                Log($"Up to date ({running.ToString(3)}).");
                if (announceResult)
                    MessageBox.Show(this,
                        $"eeeMail {running.ToString(3)} is the latest release.",
                        "No update available", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }

            _availableUpdate = latest;
            UpdateButton.Content = $"Update to {latest.Tag}";
            UpdateButton.ToolTip =
                $"eeeMail {latest.Tag} is available. Click to see what it is before installing.";
            UpdateButton.SetValue(
                System.Windows.Automation.AutomationProperties.NameProperty,
                $"Update available: {latest.Tag}");
            UpdateButton.Visibility = Visibility.Visible;
            Log($"Update available: {latest.Tag}.");

            // Only surface a window when the user asked. On launch the status bar
            // button is the whole notification.
            if (announceResult) ShowUpdateWindow();
        }
        catch (Exception ex) when (
            ex is HttpRequestException or TaskCanceledException or System.Text.Json.JsonException)
        {
            Log(LogLevel.Warning, $"Update check failed: {ex.Message}");
            if (announceResult)
                MessageBox.Show(this,
                    $"Could not reach the update source.\n\n{ex.Message}",
                    "Update check failed", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    void ShowUpdateWindow()
    {
        if (_availableUpdate is null) return;
        new UpdateWindow(_availableUpdate, _updateHttp, UpdateStagingDirectory, Log)
        {
            Owner = this,
        }.ShowDialog();
    }

    void OnUpdateClick(object sender, RoutedEventArgs e) => ShowUpdateWindow();

    async void OnCheckUpdatesClick(object sender, RoutedEventArgs e) =>
        await CheckForUpdatesAsync(announceResult: true);

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
            // Before any window opens. The theme brushes are DynamicResource, so
            // a dialog shown before the dictionary is merged resolves nothing and
            // renders black text on a black ground. With no settings yet this
            // follows Windows, which is the right default anyway.
            ApplyTheme();

            _dataDir = Mail.Core.Storage.DataLocation.ResolveWithoutCreating(
                AppContext.BaseDirectory);

            // Nothing exists yet, so ask where it should go before building it.
            // These two locations are the ones that hurt to change later: moving
            // them afterwards points the app somewhere new and leaves the old
            // data behind, so the question belongs here.
            var firstRun = !new ProfileKeyStore(
                Mail.Core.Storage.DataLocation.ProfileDirectory(_dataDir)).Exists;
            string? chosenMailDirectory = null;
            var checkUpdates = true;
            if (firstRun)
            {
                var setup = new FirstRunWindow(_dataDir);
                setup.ShowDialog();
                // Closing the window rather than continuing means "not now":
                // starting anyway would create the very thing they declined.
                if (!setup.Completed)
                {
                    Log("Setup cancelled; nothing was created.");
                    StatusText.Text = "Setup was not completed.";
                    Application.Current.Shutdown();
                    return;
                }
                _dataDir = setup.DataDirectory;
                chosenMailDirectory = setup.MailDirectory;
                checkUpdates = setup.CheckForUpdates;
            }

            var profileDir = Mail.Core.Storage.DataLocation.ProfileDirectory(_dataDir);
            Directory.CreateDirectory(profileDir);
            var keyStore = new ProfileKeyStore(profileDir);
            var masterKey = keyStore.Exists ? keyStore.Unlock() : CreateProfile(keyStore);
            _appDb = AppDatabase.Open(Path.Combine(profileDir, "app.db"), masterKey);
            _settings = new SettingsStore(_appDb);
            _drafts = new DraftStore(_appDb);

            if (firstRun)
            {
                // Recorded now the settings store exists. The data directory
                // itself is written to the marker instead, because a setting
                // that says where the settings live cannot be read from there.
                PersistDataDirectory(_dataDir);
                // Recorded so the settings tree shows where the data actually
                // is, rather than an empty box the reader has to interpret.
                _settings.Set(SettingsCatalog.DataDirectory.Key,
                    SettingTarget.Application, _dataDir);
                if (chosenMailDirectory is { Length: > 0 })
                    _settings.Set(SettingsCatalog.MailboxDirectory.Key,
                        SettingTarget.Application, chosenMailDirectory);
                _settings.Set(SettingsCatalog.CheckForUpdates.Key,
                    SettingTarget.Application, checkUpdates ? "true" : "false");
            }

            ApplyTheme();

            LoadMailboxHandles(_dataDir);
            LoadFavourites();
            BuildTree();
            StatusText.Text = $"{_mailboxes.Count} mailbox(es) open.";
            Log($"Profile opened: {_mailboxes.Count} mailbox(es).");

            // No accounts means there is nothing to show and nothing to do, so
            // go straight to where one is added rather than presenting an empty
            // window with no hint about what to do next.
            if (MailboxRegistry.ListAccounts(_appDb).Count == 0)
            {
                Log("No accounts configured; opening settings.");
                Dispatcher.BeginInvoke(() => OnSettingsClick(this, new RoutedEventArgs()),
                    System.Windows.Threading.DispatcherPriority.ApplicationIdle);
            }
        }
        catch (Exception ex)
        {
            StatusText.Text = ex.Message;
            Log(LogLevel.Error, $"ERROR: {ex.Message}");
            MessageBox.Show(this, ex.Message, "eeeMail", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Acts on a change to <c>storage.data_directory</c>.
    ///
    /// The move cannot happen while the app is running — app.db and every
    /// mailbox database are open from the current location, and copying files
    /// out from under live SQLite connections is how a store gets corrupted. So
    /// the new location is recorded and the user is told what to do: close the
    /// app, move the contents, start it again. Saying that plainly beats either
    /// silently ignoring the setting or pretending to move the data.
    /// </summary>
    void ApplyDataDirectoryChange()
    {
        if (_settings is null || _dataDir is null) return;
        var configured = _settings.GetString(
            SettingsCatalog.DataDirectory, SettingTarget.Application);
        if (string.IsNullOrWhiteSpace(configured)) return;

        string target;
        try
        {
            target = Path.GetFullPath(configured);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            Log(LogLevel.Warning, $"Data directory '{configured}' is not a usable path: {ex.Message}");
            return;
        }
        if (string.Equals(target, Path.GetFullPath(_dataDir), StringComparison.OrdinalIgnoreCase))
        {
            // Already where it says. Re-record it anyway: the setting and the
            // marker can drift apart if the marker was lost — reinstalled over,
            // or the app moved — and leaving that alone would silently send the
            // next launch to a fresh empty profile.
            PersistDataDirectory(target);
            return;
        }

        PersistDataDirectory(target);
        Log($"Data directory will be {target} after a restart.");

        var answer = MessageBox.Show(this,
            $"eeeMail will use this location when it next starts:\n\n{target}\n\n" +
            $"Your mail is still in:\n\n{_dataDir}\n\n" +
            "Nothing has been moved. The databases are open right now, so copying " +
            "them while the app is running would risk corrupting them.\n\n" +
            "To finish: close eeeMail, move the contents of the old folder into " +
            "the new one, then start it again. If you start without moving " +
            "anything, eeeMail will create a fresh, empty profile there.\n\n" +
            "Close eeeMail now?",
            "Restart needed to change location",
            MessageBoxButton.YesNo, MessageBoxImage.Information);

        if (answer == MessageBoxResult.Yes) Close();
    }

    /// <summary>
    /// Records a non-default data directory beside the executable, which is the
    /// only place it can go: the setting that describes where the settings live
    /// cannot itself be read out of them. Choosing the default writes nothing,
    /// so a default install stays free of stray files.
    /// </summary>
    void PersistDataDirectory(string chosen)
    {
        var standard = Mail.Core.Storage.DataLocation.ResolveWithoutCreating(
            AppContext.BaseDirectory);
        if (string.Equals(Path.GetFullPath(chosen), Path.GetFullPath(standard),
                StringComparison.OrdinalIgnoreCase))
            return;

        var marker = Path.Combine(
            AppContext.BaseDirectory, Mail.Core.Storage.DataLocation.PortableMarker);
        try
        {
            File.WriteAllText(marker, System.Text.Json.JsonSerializer.Serialize(
                new { dataDirectory = chosen },
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
            Log($"Data directory set to {chosen}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Installed under Program Files without write access, say. The app
            // still works this session; it would just forget the choice, so say
            // so rather than failing silently.
            Log(LogLevel.Warning,
                $"Could not record the data directory next to the app: {ex.Message}");
            MessageBox.Show(this,
                $"eeeMail will use {chosen} for this session, but could not save that " +
                $"choice next to the program:\n\n{ex.Message}\n\n" +
                $"Set the {Mail.Core.Storage.DataLocation.EnvironmentVariable} environment " +
                "variable to make it stick.",
                "eeeMail", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// Where mail, settings and caches live. Defaults to %APPDATA%\eeeMail; see
    /// <see cref="Mail.Core.Storage.DataLocation"/> for the overrides.
    /// </summary>
    static string FindDataDirectory() =>
        Mail.Core.Storage.DataLocation.Resolve(AppContext.BaseDirectory);

    /// <summary>
    /// Where a newly added mailbox's database is created. The setting when one
    /// is given, otherwise "mailboxes" under the data directory. Existing
    /// mailboxes keep the path recorded against them, so changing this affects
    /// only what is added afterwards.
    /// </summary>
    string NewMailboxDirectory()
    {
        var configured = _settings?.GetString(
            SettingsCatalog.MailboxDirectory, SettingTarget.Application);
        var directory = string.IsNullOrWhiteSpace(configured)
            ? Mail.Core.Storage.DataLocation.MailboxDirectory(_dataDir!)
            : Path.GetFullPath(Path.Combine(_dataDir!, configured));
        Directory.CreateDirectory(directory);
        return directory;
    }

    /// <summary>
    /// Creates the profile keys on first run and shows the recovery code.
    ///
    /// The code is displayed rather than merely generated because this is the
    /// only time it can be: it is not stored anywhere in recoverable form, and
    /// it is the only way back into the mail store if Windows cannot unprotect
    /// the master key — after a profile reset, or on a reinstalled machine.
    /// </summary>
    byte[] CreateProfile(ProfileKeyStore keyStore)
    {
        var created = keyStore.Create();
        Log("Created a new profile.");

        // A window rather than a MessageBox: the code cannot be shown again, and
        // a MessageBox offers no way to copy it out.
        new RecoveryCodeWindow(created.RecoveryCode) { Owner = this }.ShowDialog();

        return created.MasterKey;
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
                Header = ColumnLabel(column),
                IsCheckable = true,
                IsChecked = column.Visibility == Visibility.Visible,
                StaysOpenOnClick = true,
            };
            item.Click += (_, _) =>
            {
                column.Visibility = item.IsChecked ? Visibility.Visible : Visibility.Collapsed;
                AutoSizeColumns();
                SaveColumnLayout();
            };
            ColumnsMenu.Items.Add(item);
        }
    }

    /// <summary>
    /// Menu text for a column. The status column's header is deliberately blank
    /// — it holds only glyphs — so it would otherwise be an unclickable empty
    /// menu row, and an unnamed one to a screen reader.
    /// </summary>
    static string ColumnLabel(DataGridColumn column) =>
        column.Header as string is { Length: > 0 } header ? header : "Status";

    // ---- column layout -------------------------------------------------------

    /// <summary>
    /// The setting keys for each column, in the order the XAML declares them.
    /// Keyed by name rather than index so reordering the XAML cannot silently
    /// repoint a saved layout at the wrong column.
    /// </summary>
    Dictionary<string, DataGridColumn> ColumnsByKey() => new(StringComparer.OrdinalIgnoreCase)
    {
        ["status"] = ColStatus,
        ["from"] = ColFrom,
        ["fromname"] = ColFromName,
        ["fromaddress"] = ColFromAddress,
        ["to"] = ColTo,
        ["received"] = ColReceived,
        ["size"] = ColSize,
        ["subject"] = ColSubject,
    };

    /// <summary>
    /// Applies <c>display.message_columns</c>: the listed columns become visible
    /// in the order given, and every column left out is hidden. An unrecognised
    /// key is skipped rather than treated as fatal — the setting is free text a
    /// developer edits by hand, and one typo should not blank the list.
    /// </summary>
    void ApplyColumnLayout(FolderNode? node)
    {
        if (_settings is null) return;
        var chain = node is null
            ? [SettingTarget.Application]
            : ChainFor(node.Mailbox, node.FolderId);
        var spec = _settings.GetString(SettingsCatalog.MessageColumns, chain);
        if (string.IsNullOrWhiteSpace(spec)) return;

        var byKey = ColumnsByKey();
        var wanted = spec.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);
        var seen = new HashSet<DataGridColumn>();
        var index = 0;
        foreach (var key in wanted)
        {
            if (!byKey.TryGetValue(key, out var column))
            {
                Log(LogLevel.Warning, $"Unknown column '{key}' in {SettingsCatalog.MessageColumns.Key}.");
                continue;
            }
            if (!seen.Add(column)) continue;
            column.Visibility = Visibility.Visible;
            column.DisplayIndex = index++;
        }

        // Anything unlisted is hidden. DisplayIndex must stay a permutation, so
        // the hidden ones keep positions after the visible run rather than being
        // left pointing wherever they previously sat.
        foreach (var column in MessageGrid.Columns)
            if (!seen.Contains(column))
            {
                column.Visibility = Visibility.Collapsed;
                column.DisplayIndex = index++;
            }

        BuildColumnsMenu();
        AutoSizeColumns();
    }

    /// <summary>
    /// Writes the current visible columns, in display order, back to the setting
    /// so a change made through the menu or by dragging a header survives a
    /// restart. Saved at whatever scope the folder in view resolves to — the
    /// application, unless the user has overridden it further down.
    /// </summary>
    void SaveColumnLayout()
    {
        if (_settings is null) return;
        var byColumn = ColumnsByKey().ToDictionary(p => p.Value, p => p.Key);
        var keys = MessageGrid.Columns
            .Where(c => c.Visibility == Visibility.Visible)
            .OrderBy(c => c.DisplayIndex)
            .Select(c => byColumn.TryGetValue(c, out var key) ? key : null)
            .Where(k => k is not null);
        _settings.Set(SettingsCatalog.MessageColumns.Key, _columnScope, string.Join(",", keys));
    }

    /// <summary>
    /// Applies <c>display.row_density</c>. Two- and three-line rows wrap the
    /// subject rather than clipping it, which is the point of asking for them,
    /// so the height is a minimum and the subject cell is allowed to grow.
    /// </summary>
    void ApplyRowDensity(FolderNode? node)
    {
        if (_settings is null) return;
        var chain = node is null
            ? [SettingTarget.Application]
            : ChainFor(node.Mailbox, node.FolderId);
        var density = _settings.GetString(SettingsCatalog.RowDensity, chain);
        var lines = density switch { "three-line" => 3, "two-line" => 2, _ => 1 };

        // Derived from the rendered font rather than a fixed pixel count, so the
        // rows still fit their text at a larger system font size.
        var lineHeight = Math.Ceiling(MessageGrid.FontSize * 1.4);
        MessageGrid.RowHeight = double.NaN;                  // let content size it
        MessageGrid.MinRowHeight = lines * lineHeight + 4;   // padding

        var wrap = lines > 1;
        ColSubject.ElementStyle = wrap ? _wrappedCell : null;
        if (ColSubject.Width.IsStar || wrap) return;
        AutoSizeColumns();
    }

    /// <summary>Subject cell for multi-line rows: wraps instead of clipping.</summary>
    static readonly Style _wrappedCell = BuildWrappedCellStyle();

    static Style BuildWrappedCellStyle()
    {
        var style = new Style(typeof(TextBlock));
        style.Setters.Add(new Setter(TextBlock.TextWrappingProperty, TextWrapping.Wrap));
        style.Setters.Add(new Setter(TextBlock.VerticalAlignmentProperty, VerticalAlignment.Top));
        return style;
    }

    // ---- folder tree ---------------------------------------------------------

    void BuildTree()
    {
        FolderTree.Items.Clear();
        FavouritesTree.Items.Clear();
        _folderNodes.Clear();
        _favouriteNodes.Clear();
        FolderNodeViewModel? inboxNode = null;

        // Group by the account whose credentials reach each mailbox. An account
        // with only its own mailbox stays flat — folders hang straight off the
        // account — and a mailbox level appears only once there is more than one
        // to tell apart, so adding a shared mailbox does not add a level of
        // nesting to every other account.
        foreach (var group in _mailboxes.GroupBy(m => m.AccountUpn, StringComparer.OrdinalIgnoreCase))
        {
            var primary = group.FirstOrDefault(m => !m.IsShared) ?? group.First();
            var accountRoot = new FolderNodeViewModel
            {
                Name = $"{group.Key}  [{primary.ScopeText}]",
                IsExpanded = true,
                IsAccountRoot = true,
            };
            FolderTree.Items.Add(accountRoot);
            var nested = group.Count() > 1;

        foreach (var mailbox in group)
        {
            var root = nested
                ? new FolderNodeViewModel
                {
                    Name = mailbox.IsShared
                        ? $"{mailbox.DisplayName} ({mailbox.Upn})"
                        : "This mailbox",
                    IsExpanded = true,
                    IsMailboxRoot = true,
                    Mailbox = mailbox,
                }
                : accountRoot;
            if (nested) accountRoot.Children.Add(root);

            var rows = QueryFolders(mailbox);
            var localCounts = QueryLocalCounts(mailbox);
            var activity = QueryFolderActivity(mailbox);
            var nodes = new Dictionary<long, FolderNodeViewModel>();
            var paths = BuildFolderPaths(rows);

            foreach (var row in rows)
            {
                var node = new FolderNodeViewModel
                {
                    Mailbox = mailbox,
                    FolderId = row.Id,
                    ServerId = row.ServerId,
                    SpecialUse = row.Special,
                    Name = row.Name,
                    FolderPath = paths.GetValueOrDefault(row.Id, "/" + row.Name),
                    AccountName = mailbox.Upn,
                    IsExpanded = true,
                    LastActivity = activity.GetValueOrDefault(row.Id),
                };
                ApplyCounts(node, row, localCounts);
                nodes[row.Id] = node;
                _folderNodes[(mailbox.Upn, row.Id)] = node;
                if (row.Special == "inbox")
                    inboxNode ??= node;
            }

            foreach (var row in rows)
            {
                var parent = row.ParentId is long p && nodes.TryGetValue(p, out var pn)
                    ? pn.Children
                    : root.Children;
                parent.Add(nodes[row.Id]);
            }
            // Favourites live in their own control above the folders, each row
            // labelled with its account so identical folder names stay distinct.
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
                    FolderPath = source.FolderPath,
                    AccountName = mailbox.Upn,
                    Counts = source.Counts,
                    Totals = source.Totals,
                    HasUnread = source.HasUnread,
                    IsFavouriteEntry = true,
                    LastActivity = source.LastActivity,
                };
                FavouritesTree.Items.Add(shortcut);
                _favouriteNodes[(mailbox.Upn, source.FolderId)] = shortcut;
            }
        }
        }

        var hasFavourites = FavouritesTree.Items.Count > 0;
        FavouritesTree.Visibility = hasFavourites ? Visibility.Visible : Visibility.Collapsed;
        FavouritesHeader.Visibility = hasFavourites ? Visibility.Visible : Visibility.Collapsed;
        // The favourites splitter is gone: AvalonDock supplies splitters between
        // panes, so there is no hand-placed one left to show or hide.

        ApplyQuietFilter();
        // Auto-select the inbox only when nothing is selected yet: a rebuild
        // triggered by a background sync must not move the user.
        if (inboxNode is not null && FolderTree.SelectedItem is null)
            SelectNode(inboxNode);
    }

    /// <summary>
    /// Rebuilds the tree after the folder set changes, restoring the selected
    /// folder and which nodes were expanded so a background sync does not yank
    /// the view out from under the user.
    /// </summary>
    void RebuildTreePreservingState()
    {
        var selected = FolderTree.SelectedItem as FolderNodeViewModel;
        var selectedKey = selected?.Mailbox is MailboxHandle m
            ? (m.Upn, selected.FolderId)
            : ((string, long)?)null;
        var collapsed = _folderNodes
            .Where(kv => !kv.Value.IsExpanded)
            .Select(kv => kv.Key)
            .ToHashSet();

        BuildTree();

        foreach (var key in collapsed)
            if (_folderNodes.TryGetValue(key, out var node))
                node.IsExpanded = false;
        if (selectedKey is { } key2 && _folderNodes.TryGetValue(key2, out var restore))
            SelectNode(restore);
    }

    /// <summary>
    /// Resolution chain for a mailbox: itself, its account, then the
    /// application. Folder-scoped settings add the folder in front.
    /// </summary>
    static SettingTarget[] ChainFor(MailboxHandle mailbox) =>
    [
        SettingTarget.Mailbox(mailbox.Id),
        SettingTarget.Account(mailbox.AccountId),
        SettingTarget.Application,
    ];

    static SettingTarget[] ChainFor(MailboxHandle mailbox, long folderId) =>
    [
        SettingTarget.Folder(mailbox.Id, folderId),
        SettingTarget.Mailbox(mailbox.Id),
        SettingTarget.Account(mailbox.AccountId),
        SettingTarget.Application,
    ];

    /// <summary>
    /// The date/time format in force for a place. The list wants a scannable
    /// fixed-width column and the reader reads as prose, so they resolve
    /// separately; both fall back to the general format.
    /// </summary>
    string DateFormat(SettingDefinition setting, MailboxHandle? mailbox = null)
    {
        if (_settings is null) return "yyyy-MM-dd HH:mm:ss";
        var chain = mailbox is null ? [SettingTarget.Application] : ChainFor(mailbox);
        var resolved = _settings.Resolve(setting, chain);
        if (!resolved.IsDefault) return resolved.Value;

        // Nothing set here, so consider the general format — but only if it was
        // itself set. Falling back to *its* default would override the specific
        // default this setting declares, which is the point of having one: the
        // reading pane wants a long form even though the list wants a short one.
        var general = _settings.Resolve(SettingsCatalog.DateTimeFormat, chain);
        return general.IsDefault ? resolved.Value : general.Value;
    }

    string ListDateFormat(MailboxHandle? mailbox = null) =>
        DateFormat(SettingsCatalog.ListDateFormat, mailbox);

    string ReaderDateFormat(MailboxHandle? mailbox = null) =>
        DateFormat(SettingsCatalog.ReaderDateFormat, mailbox);

    /// <summary>Full path per folder (/Organised/GumpyGoblin) for favourite labels.</summary>
    static Dictionary<long, string> BuildFolderPaths(List<FolderRow> rows)
    {
        var byId = rows.ToDictionary(r => r.Id);
        var paths = new Dictionary<long, string>();
        foreach (var row in rows)
        {
            var parts = new List<string> { row.Name };
            var current = row;
            var guard = 0;
            while (current.ParentId is long parentId &&
                   byId.TryGetValue(parentId, out var parent) && guard++ < 32)
            {
                parts.Insert(0, parent.Name);
                current = parent;
            }
            paths[row.Id] = "/" + string.Join("/", parts);
        }
        return paths;
    }

    /// <summary>
    /// Renders local/total then unread: 1,203/26,921  57. Unread keeps its own
    /// column so it right-aligns rather than drifting with the width of the
    /// totals in front of it; bold is enough to tell the two apart.
    /// </summary>
    static void ApplyCounts(
        FolderNodeViewModel node, FolderRow row,
        Dictionary<long, (long Total, long Unread)> local)
    {
        var (localTotal, localUnread) = local.GetValueOrDefault(row.Id);
        // The server total lags during a first sync, so trust whichever is
        // larger rather than claiming to hold more messages than exist.
        var serverTotal = Math.Max(row.ServerTotal, localTotal);

        node.HasUnread = localUnread > 0;
        // Once a folder is fully mirrored, "26,950/26,950" is just noise that
        // steals width from the folder name: show the pair only while they
        // differ, which is exactly when the distinction matters.
        node.Totals = serverTotal <= 0
            ? ""
            : localTotal < serverTotal
                ? $"{localTotal:N0}/{serverTotal:N0}"
                : $"{localTotal:N0}";
        node.Counts = localUnread > 0 ? $"{localUnread:N0}" : "";
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

    /// <summary>
    /// Reads favourites from folder-scoped settings, migrating the old ui_state
    /// JSON blob on first run. Keeping them as settings means they appear in the
    /// settings window like anything else, rather than being invisible state
    /// only this window knows how to write.
    /// </summary>
    void LoadFavourites()
    {
        _favourites.Clear();
        if (_appDb is null || _settings is null) return;
        try
        {
            MigrateFavouritesFromUiState();

            foreach (var mailbox in _mailboxes)
            {
                var ids = FavouriteFolderIds(mailbox);
                if (ids.Count > 0) _favourites[mailbox.Upn] = ids;
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Debug, $"Could not read favourites: {ex.Message}");
        }
    }

    /// <summary>Folder ids flagged as favourites for one mailbox.</summary>
    List<long> FavouriteFolderIds(MailboxHandle mailbox)
    {
        var result = new List<long>();
        if (_appDb is null) return result;
        using var cmd = _appDb.CreateCommand();
        // Folder targets are "{mailboxId}:{folderId}", so one query per mailbox
        // beats resolving each folder in turn.
        cmd.CommandText = """
            SELECT target FROM settings
            WHERE key = @k AND scope = 0 AND value = 'true' AND target LIKE @prefix;
            """;
        cmd.Parameters.AddWithValue("@k", SettingsCatalog.ShowInFavourites.Key);
        cmd.Parameters.AddWithValue("@prefix", $"{mailbox.Id}:%");
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var parts = reader.GetString(0).Split(':');
            if (parts.Length == 2 && long.TryParse(parts[1], out var folderId))
                result.Add(folderId);
        }
        return result;
    }

    /// <summary>
    /// One-time move of the old JSON blob into settings. The blob was keyed by
    /// UPN and held folder ids, so each entry becomes a folder-scoped override.
    /// </summary>
    void MigrateFavouritesFromUiState()
    {
        if (_appDb is null || _settings is null) return;
        string? json;
        using (var read = _appDb.CreateCommand())
        {
            read.CommandText = "SELECT value FROM ui_state WHERE key = 'favourites';";
            json = read.ExecuteScalar() as string;
        }
        if (string.IsNullOrWhiteSpace(json)) return;

        var legacy = System.Text.Json.JsonSerializer
            .Deserialize<Dictionary<string, List<long>>>(json) ?? [];
        var moved = 0;
        foreach (var (upn, folderIds) in legacy)
        {
            var mailbox = _mailboxes.FirstOrDefault(m =>
                string.Equals(m.Upn, upn, StringComparison.OrdinalIgnoreCase));
            if (mailbox is null) continue;
            foreach (var folderId in folderIds)
            {
                _settings.Set(SettingsCatalog.ShowInFavourites.Key,
                    SettingTarget.Folder(mailbox.Id, folderId), "true");
                moved++;
            }
        }

        // Remove the blob only once its contents are safely in settings, so an
        // interrupted migration retries rather than losing the favourites.
        using var clear = _appDb.CreateCommand();
        clear.CommandText = "DELETE FROM ui_state WHERE key = 'favourites';";
        clear.ExecuteNonQuery();
        if (moved > 0)
            Log(LogLevel.Debug, $"Moved {moved:N0} favourite(s) into per-folder settings.");
    }

    void SaveFavourites()
    {
        if (_settings is null) return;
        try
        {
            // Write the flag per folder; the settings window shows the same value.
            foreach (var mailbox in _mailboxes)
            {
                var wanted = FavouritesFor(mailbox.Upn).ToHashSet();
                var existing = FavouriteFolderIds(mailbox).ToHashSet();

                foreach (var folderId in wanted.Except(existing))
                    _settings.Set(SettingsCatalog.ShowInFavourites.Key,
                        SettingTarget.Folder(mailbox.Id, folderId), "true");
                foreach (var folderId in existing.Except(wanted))
                    _settings.Clear(SettingsCatalog.ShowInFavourites.Key,
                        SettingTarget.Folder(mailbox.Id, folderId));
            }
        }
        catch (Exception ex)
        {
            Log(LogLevel.Warning, $"Could not save favourites: {ex.Message}");
        }
    }

    /// <summary>
    /// Resolves the node a context menu was opened on. A ContextMenu lives
    /// outside the visual tree, so its DataContext is the row that was
    /// right-clicked, which is not necessarily the selected one.
    /// </summary>
    static FolderNodeViewModel? NodeFor(object sender) => sender switch
    {
        MenuItem { DataContext: FolderNodeViewModel node } => node,
        FrameworkElement { DataContext: FolderNodeViewModel node } => node,
        _ => null,
    };

    void OnAddFavourite(object sender, RoutedEventArgs e)
    {
        var node = NodeFor(sender) ?? FolderTree.SelectedItem as FolderNodeViewModel;
        if (node is null || node.Mailbox is not MailboxHandle mailbox || node.IsGroupHeader)
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
        var node = NodeFor(sender) ?? FolderTree.SelectedItem as FolderNodeViewModel;
        if (node is null || node.Mailbox is not MailboxHandle mailbox)
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
        // Favourites are an explicit choice; never hide them.
        foreach (var item in FavouritesTree.Items.OfType<FolderNodeViewModel>())
            item.Visibility = Visibility.Visible;
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

                // A first sync discovers folders after the tree was built, so
                // refreshing counts alone would leave a new account looking
                // empty until the next launch. Rebuild when the folder set
                // changes; otherwise just update the numbers in place, which
                // keeps expansion and selection intact.
                var known = _folderNodes.Keys.Count(k => k.Upn == mailbox.Upn);
                if (folders.Count != known)
                {
                    RebuildTreePreservingState();
                    return;
                }

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

    void OnFavouriteSelected(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (FavouritesTree.SelectedItem is FolderNodeViewModel node &&
            node.Mailbox is MailboxHandle mailbox)
            LoadFolder(new FolderNode(mailbox, node.FolderId, node.Name));
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
        // Both resolve per folder, so they are applied before the fill rather
        // than once at startup: moving between folders can change either.
        _openFolder = node;
        ApplyColumnLayout(node);
        ApplyRowDensity(node);

        using var cmd = node.Mailbox.Db.CreateCommand();
        cmd.CommandText = RowSelect + " WHERE m.folder_id = @f ORDER BY m.received_at DESC LIMIT 5000;";
        cmd.Parameters.AddWithValue("@f", node.FolderId);
        FillList(node.Mailbox, cmd);
        StatusText.Text =
            $"{node.Mailbox.Upn} / {node.Name}: {MessageGrid.Items.Count:N0} message(s)" +
            LocalFolderSizeSuffix(node.Mailbox, node.FolderId);
        _ = ShowServerFolderSizeAsync(node);
    }

    void FillList(MailboxHandle mailbox, SqliteCommand cmd)
    {
        // Resolved once per fill rather than per row: the chain walk is cheap
        // but a five-thousand-row list makes anything per-row worth avoiding.
        var dateFormat = ListDateFormat(mailbox);
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
                            .ToLocalTime().ToString(dateFormat),
                    SizeKb: sizeVal.ToString("N0"),
                    SizeVal: sizeVal,
                    Subject: reader.IsDBNull(1) ? "" : reader.GetString(1),
                    IsUnread: reader.GetInt64(4) == 0)
                {
                    ReceivedValue = reader.IsDBNull(2)
                        ? null
                        : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(2)),
                });
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

    /// <summary>
    /// Why a sign-in prompt is about to appear. A browser window opening with no
    /// explanation is the hardest kind of problem to diagnose — it was exactly
    /// what made a stale capability record hard to trace — so every interactive
    /// sign-in states the account, what triggered it, and why the cache could
    /// not answer.
    /// </summary>
    async Task<AuthenticationResult> SignInExplainedAsync(
        GraphAuthenticator auth, string accountUpn, string[]? scopes, string reason)
    {
        var wanted = scopes is null
            ? "the standard mail permissions"
            : string.Join(", ", scopes.Select(x => x[(x.LastIndexOf('/') + 1)..]));
        Log(LogLevel.Info,
            $"Sign-in needed for {accountUpn}: {reason}. " +
            $"Requesting {wanted}. A browser window will open.");
        try
        {
            var result = scopes is null
                ? await auth.SignInInteractiveAsync(accountUpn)
                : await auth.SignInInteractiveAsync(accountUpn, scopes: scopes);
            Log(LogLevel.Info, $"Sign-in completed for {accountUpn}.");
            return result;
        }
        catch (MsalClientException ex) when (ex.ErrorCode == "authentication_canceled")
        {
            // MSAL says "canceled" whenever the browser closes without a token,
            // including when it closed because the tenant demanded approval.
            Log(LogLevel.Warning,
                $"Sign-in for {accountUpn} did not complete. If approval was " +
                "requested, approve it and try again — no second sign-in is needed.");
            throw;
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Sign-in failed for {accountUpn}: {ex.Message}");
            throw;
        }
    }

    /// <summary>Raw access token for calls the typed SDK cannot express (raw-MIME send).</summary>
    async Task<string> GetAccessTokenAsync(MailboxHandle mailbox)
    {
        var auth = new GraphAuthenticator(
            Path.Combine(_dataDir!, "msal.cache"),
            () => new WindowInteropHelper(this).Handle);
        var accountUpn = mailbox.AccountUpn.Length > 0 ? mailbox.AccountUpn : mailbox.Upn;
        var accounts = await auth.GetAccountsAsync();
        var account = accounts.FirstOrDefault(a =>
            string.Equals(a.Username, accountUpn, StringComparison.OrdinalIgnoreCase));
        var result = account is null ? null : await auth.AcquireSilentAsync(account);
        result ??= await SignInExplainedAsync(auth, accountUpn, null,
            account is null
                ? "this account has no cached credentials on this machine"
                : "the saved credentials have expired and could not be renewed silently");
        return result.AccessToken;
    }

    async Task<GraphServiceClient> GetGraphAsync(MailboxHandle mailbox) =>
        await GetGraphForAccountAsync(
            mailbox.AccountUpn.Length > 0 ? mailbox.AccountUpn : mailbox.Upn);

    /// <summary>
    /// A Graph client authenticated as the given signed-in account. Shared
    /// mailboxes have no credentials of their own, so they are reached with the
    /// token of the account that holds rights on them — which is also why the
    /// cache is keyed by account rather than by mailbox.
    /// </summary>
    async Task<GraphServiceClient> GetGraphForAccountAsync(string accountUpn)
    {
        if (_graphClients.TryGetValue(accountUpn, out var cached))
            return cached;
        var auth = new GraphAuthenticator(
            Path.Combine(_dataDir!, "msal.cache"),
            () => new WindowInteropHelper(this).Handle);
        // Ask for exactly the scopes this account consented to: requesting more
        // than was granted cannot be satisfied from cache, and would drag every
        // account into a fresh prompt for a feature only some of them can use.
        var scopes = AccountCapability.GraphScopesFor(
            MailboxRegistry.GetCapabilities(_appDb!, accountUpn));
        var accounts = await auth.GetAccountsAsync();
        var account = accounts.FirstOrDefault(a =>
            string.Equals(a.Username, accountUpn, StringComparison.OrdinalIgnoreCase));
        var token = account is null ? null : await auth.AcquireSilentAsync(account, scopes);

        // Optional capabilities must never cost a sign-in prompt. If the wider
        // set cannot be satisfied from cache, the recorded capabilities are
        // stale — the scopes were asked for once but not granted — so fall back
        // to the scopes the app cannot work without, and correct the record.
        // Without this the app opens a browser on every single launch.
        if (token is null && account is not null && scopes.Length > GraphAuthenticator.MailScopes.Length)
        {
            token = await auth.AcquireSilentAsync(account, GraphAuthenticator.MailScopes);
            if (token is not null)
            {
                var held = token.Scopes.ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var capability in AccountCapability.All.Where(c => !c.Required))
                {
                    var missing = capability.Scopes.Any(want =>
                        want.StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase) &&
                        !held.Any(h => h.EndsWith(want[(want.LastIndexOf('/') + 1)..],
                            StringComparison.OrdinalIgnoreCase)));
                    if (missing && MailboxRegistry.HasCapability(_appDb!, accountUpn, capability.Id))
                    {
                        MailboxRegistry.SetCapability(_appDb!, accountUpn, capability.Id, false);
                        Log(LogLevel.Warning,
                            $"{accountUpn}: \"{capability.Name}\" was recorded but its permission " +
                            "was never granted — turning it off. Re-enable it in Settings to request it again.");
                    }
                }
            }
        }

        if (token is not null)
            Log(LogLevel.Debug, $"Silent token acquired for {accountUpn}.");
        else
        {
            token = await SignInExplainedAsync(auth, accountUpn, scopes,
                account is null
                    ? "no cached credentials for this account on this machine"
                    : "the saved credentials could not be renewed silently (expired, " +
                      "revoked, or the password changed)");
        }
        var client = new GraphServiceClient(
            new BaseBearerTokenAuthenticationProvider(new GraphTokenProvider(auth, token)));
        _graphClients[accountUpn] = client;
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

        // Resolved per message rather than per line: cheap, but the folder-level
        // chain walk should not run once per recipient.
        var readerFormat = ReaderDateFormat(mailbox);
        var showAddresses = _settings?.GetBool(
            SettingsCatalog.ShowAddressesNotNames, ChainFor(mailbox)) ?? true;

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
                // The address is never dropped in favour of a display name: a
                // name that reads "PayPal" over an address that does not is
                // exactly the substitution this client exists to refuse.
                byKind[reader.GetInt64(0)].Add(
                    string.IsNullOrEmpty(name) || name == email
                        ? email
                        : showAddresses ? $"{name} <{email}>" : name);
            }
            for (var kind = 0; kind < KindLabels.Length; kind++)
                if (byKind.TryGetValue(kind, out var list))
                    envelope.AppendLine($"{KindLabels[kind],-8}: {string.Join("; ", list)}");
        }
        using (var cmd = mailbox.Db.CreateCommand())
        {
            cmd.CommandText = """
                SELECT subject, coalesce(sent_at, received_at)
                FROM messages WHERE id = @id;
                """;
            cmd.Parameters.AddWithValue("@id", messageId);
            using var reader = cmd.ExecuteReader();
            if (reader.Read())
            {
                var when = reader.IsDBNull(1)
                    ? ""
                    : DateTimeOffset.FromUnixTimeSeconds(reader.GetInt64(1))
                        .ToLocalTime().ToString(readerFormat);
                envelope.AppendLine($"{"Date",-8}: {when}");
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

        _allAttachments = [.. MailboxStore.GetAttachments(mailbox.Db, messageId)
            .Select(a => new AttachmentItem(
                mailbox, a.Id, a.FileName ?? "(unnamed)", a.ContentType ?? "",
                (a.Size / 1024.0).ToString("N0"), a.IsInline ? "yes" : "", a.HasContent)
            {
                SizeBytes = a.Size,
                IsInline = a.IsInline,
            })];
        ShowInlineBox.IsChecked = _settings?.GetBool(
            SettingsCatalog.ShowInlineAttachments, ChainFor(mailbox)) ?? false;
        OpenAttachmentButton.Visibility = _settings?.GetBool(
            SettingsCatalog.OpenAttachmentsExternally, SettingTarget.Application) == true
                ? Visibility.Visible : Visibility.Collapsed;
        ApplyAttachmentFilter();

        // The setting supplies the default; the toggle overrides it per message.
        _themeMessageBodies = _settings?.GetBool(
            SettingsCatalog.ThemeMessageBodies, ChainFor(mailbox)) ?? false;
        BodyThemeToggle.IsChecked = _themeMessageBodies;
        _ = RenderPreviewAsync();
    }

    /// <summary>Double-clicking a row saves it, same as the Save button.</summary>
    void OnAttachmentSave(object sender, MouseButtonEventArgs e) => SaveSelectedAttachment();

    List<AttachmentItem> _allAttachments = [];

    /// <summary>
    /// Applies the inline filter. Inline parts are embedded images — logos and
    /// signature graphics — so listing them by default buries the file someone
    /// actually attached among a dozen decorations.
    /// </summary>
    void ApplyAttachmentFilter()
    {
        var showInline = ShowInlineBox.IsChecked == true;
        var shown = showInline
            ? _allAttachments
            : [.. _allAttachments.Where(a => !a.IsInline)];
        AttachmentList.ItemsSource = shown;

        var hidden = _allAttachments.Count - shown.Count;
        var bytes = shown.Sum(a => a.SizeBytes);
        AttachmentSummary.Text = _allAttachments.Count == 0
            ? "No attachments."
            : $"{shown.Count:N0} shown ({FormatBytes(bytes)})" +
              (hidden > 0 ? $", {hidden:N0} inline hidden" : "");
        ClearAttachmentPreview();
    }

    void OnToggleInline(object sender, RoutedEventArgs e)
    {
        ApplyAttachmentFilter();
        // Remember the choice at the level being viewed, so it is not re-made
        // for every message.
        if (_settings is not null && _currentMailbox is { } mailbox)
        {
            _settings.Set(SettingsCatalog.ShowInlineAttachments.Key,
                SettingTarget.Mailbox(mailbox.Id),
                ShowInlineBox.IsChecked == true ? "true" : "false");
        }
    }

    /// <summary>
    /// Bytes at a readable scale. Mailboxes reach gigabytes, and "6,451.6 MB"
    /// is a number the reader has to convert themselves.
    /// </summary>
    static string FormatBytes(long bytes) =>
        bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):N2} GB"
      : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):N1} MB"
      : bytes >= 1024 ? $"{bytes / 1024.0:N0} KB"
      : $"{bytes:N0} bytes";

    void ClearAttachmentPreview()
    {
        AttachmentImage.Source = null;
        AttachmentImage.Visibility = Visibility.Collapsed;
        AttachmentText.Text = "";
        AttachmentText.Visibility = Visibility.Collapsed;
        AttachmentPreviewHint.Visibility = Visibility.Visible;
        AttachmentPreviewHint.Text = _allAttachments.Count == 0
            ? "This message has no attachments."
            : "Select an attachment to preview it.";
    }

    /// <summary>
    /// Previews the selected attachment. Rendering happens from the stored bytes
    /// with no network access of any kind — an attachment is untrusted input, and
    /// the preview must not become a way for it to reach out.
    /// </summary>
    void OnAttachmentSelected(object sender, SelectionChangedEventArgs e)
    {
        ClearAttachmentPreview();
        if (AttachmentList.SelectedItem is not AttachmentItem item ||
            item.Mailbox is not MailboxHandle mailbox)
            return;

        if (_settings?.GetBool(SettingsCatalog.PreviewAttachments, ChainFor(mailbox)) == false)
        {
            AttachmentPreviewHint.Text = "Preview is turned off for this mailbox.";
            return;
        }
        if (!item.HasContent)
        {
            AttachmentPreviewHint.Text = "This attachment's content was not downloaded.";
            return;
        }

        var limitKb = _settings?.GetInt(SettingsCatalog.PreviewSizeLimitKb, ChainFor(mailbox)) ?? 8192;
        if (limitKb > 0 && item.SizeBytes > limitKb * 1024L)
        {
            AttachmentPreviewHint.Text =
                $"{FormatBytes(item.SizeBytes)} exceeds the {limitKb:N0} KB preview limit. " +
                "Save it to open it.";
            return;
        }

        var content = MailboxStore.GetAttachmentContent(mailbox.Db, item.Id);
        if (content is null)
        {
            AttachmentPreviewHint.Text = "Content missing from the local store.";
            return;
        }

        var type = item.ContentType.ToLowerInvariant();
        var name = item.Name.ToLowerInvariant();
        try
        {
            if (type.StartsWith("image/") || name.EndsWith(".png") || name.EndsWith(".jpg") ||
                name.EndsWith(".jpeg") || name.EndsWith(".gif") || name.EndsWith(".bmp"))
            {
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                // No network, no external references: decode the bytes we hold.
                image.CreateOptions = System.Windows.Media.Imaging.BitmapCreateOptions.IgnoreColorProfile;
                image.StreamSource = new MemoryStream(content);
                image.EndInit();
                image.Freeze();
                AttachmentImage.Source = image;
                AttachmentImage.Visibility = Visibility.Visible;
                AttachmentPreviewHint.Visibility = Visibility.Collapsed;
                return;
            }

            if (IsTextLike(type, name))
            {
                var text = Encoding.UTF8.GetString(content);
                AttachmentText.Text = text.Length <= RawDisplayCap
                    ? text
                    : text[..RawDisplayCap] + $"{Environment.NewLine}… (truncated for display)";
                AttachmentText.Visibility = Visibility.Visible;
                AttachmentPreviewHint.Visibility = Visibility.Collapsed;
                return;
            }

            AttachmentPreviewHint.Text =
                $"No preview for {(item.ContentType.Length > 0 ? item.ContentType : "this type")}. " +
                $"{FormatBytes(item.SizeBytes)} — save it to open it.";
        }
        catch (Exception ex)
        {
            AttachmentPreviewHint.Text = $"Could not preview this file: {ex.Message}";
        }
    }

    static bool IsTextLike(string contentType, string fileName) =>
        contentType.StartsWith("text/") ||
        contentType is "application/json" or "application/xml" or "application/javascript" ||
        fileName.EndsWith(".txt") || fileName.EndsWith(".log") || fileName.EndsWith(".csv") ||
        fileName.EndsWith(".json") || fileName.EndsWith(".xml") || fileName.EndsWith(".md") ||
        fileName.EndsWith(".eml") || fileName.EndsWith(".ics") || fileName.EndsWith(".yml") ||
        fileName.EndsWith(".yaml") || fileName.EndsWith(".ini") || fileName.EndsWith(".config");

    void OnSaveSelectedAttachment(object sender, RoutedEventArgs e) => SaveSelectedAttachment();

    void SaveSelectedAttachment()
    {
        if (AttachmentList.SelectedItem is not AttachmentItem item ||
            item.Mailbox is not MailboxHandle mailbox)
        {
            StatusText.Text = "Select an attachment first.";
            return;
        }
        if (!item.HasContent)
        {
            Log(LogLevel.Warning, $"Attachment '{item.Name}' has no stored content.");
            return;
        }
        var dialog = new Microsoft.Win32.SaveFileDialog { FileName = SafeFileName(item.Name) };
        if (dialog.ShowDialog(this) != true) return;
        var content = MailboxStore.GetAttachmentContent(mailbox.Db, item.Id);
        if (content is null)
        {
            Log(LogLevel.Warning, $"Attachment '{item.Name}' content missing.");
            return;
        }
        File.WriteAllBytes(dialog.FileName, content);
        Log($"Saved attachment '{item.Name}' ({FormatBytes(content.Length)}).");
    }

    /// <summary>Exports everything currently listed, honouring the inline filter.</summary>
    void OnExportAllAttachments(object sender, RoutedEventArgs e)
    {
        var items = (AttachmentList.ItemsSource as IEnumerable<AttachmentItem>)?.ToList() ?? [];
        if (items.Count == 0)
        {
            StatusText.Text = "Nothing to export.";
            return;
        }

        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = $"Export {items.Count:N0} attachment(s) to…",
        };
        if (dialog.ShowDialog(this) != true) return;

        var written = 0;
        var skipped = 0;
        foreach (var item in items)
        {
            if (item.Mailbox is not MailboxHandle mailbox || !item.HasContent) { skipped++; continue; }
            var content = MailboxStore.GetAttachmentContent(mailbox.Db, item.Id);
            if (content is null) { skipped++; continue; }
            try
            {
                // Never overwrite: two parts can share a filename, and silently
                // losing one export to another is worse than a suffixed name.
                var path = UniquePath(dialog.FolderName, SafeFileName(item.Name));
                File.WriteAllBytes(path, content);
                written++;
            }
            catch (Exception ex)
            {
                skipped++;
                Log(LogLevel.Warning, $"Could not export '{item.Name}': {ex.Message}");
            }
        }
        var summary = $"Exported {written:N0} attachment(s) to {dialog.FolderName}" +
                      (skipped > 0 ? $" ({skipped:N0} skipped)" : "") + ".";
        StatusText.Text = summary;
        Log(summary);
    }

    /// <summary>
    /// Strips path separators and reserved characters. An attachment filename
    /// comes from whoever sent the message, so it is never trusted as a path.
    /// </summary>
    static string SafeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var cleaned = new string([.. name.Select(c => invalid.Contains(c) ? '_' : c)]).Trim();
        return cleaned.Length == 0 ? "attachment" : cleaned;
    }

    static string UniquePath(string folder, string fileName)
    {
        var path = Path.Combine(folder, fileName);
        if (!File.Exists(path)) return path;
        var stem = Path.GetFileNameWithoutExtension(fileName);
        var extension = Path.GetExtension(fileName);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate)) return candidate;
        }
    }

    /// <summary>
    /// Hands an attachment to its associated application. Off by default and
    /// warned about: this is the step that turns a received file into a running
    /// program, with none of this app's sandboxing.
    /// </summary>
    void OnOpenAttachment(object sender, RoutedEventArgs e)
    {
        if (_settings?.GetBool(SettingsCatalog.OpenAttachmentsExternally,
                SettingTarget.Application) != true)
            return;
        if (AttachmentList.SelectedItem is not AttachmentItem item ||
            item.Mailbox is not MailboxHandle mailbox || !item.HasContent)
            return;

        var confirm = MessageBox.Show(
            this,
            $"Open '{item.Name}' in its associated application?\n\n" +
            "The file came from whoever sent this message and will run outside " +
            "this app's protections.",
            "eeeMail — open attachment",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning, MessageBoxResult.Cancel);
        if (confirm != MessageBoxResult.OK) return;

        var content = MailboxStore.GetAttachmentContent(mailbox.Db, item.Id);
        if (content is null) return;
        try
        {
            var path = UniquePath(Path.Combine(Path.GetTempPath(), "eeeMail"), SafeFileName(item.Name));
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllBytes(path, content);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = path,
                UseShellExecute = true,
            });
            Log(LogLevel.Warning, $"Opened attachment '{item.Name}' externally.");
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Could not open '{item.Name}': {ex.Message}");
        }
    }

    /// <summary>
    /// Bytes actually stored locally for a folder. Free — it is a sum over rows
    /// this app already holds — so it shows immediately while the server figure
    /// is fetched.
    /// </summary>
    static string LocalFolderSizeSuffix(MailboxHandle mailbox, long folderId)
    {
        try
        {
            using var cmd = mailbox.Db.CreateCommand();
            cmd.CommandText = "SELECT coalesce(sum(size), 0) FROM messages WHERE folder_id = @f;";
            cmd.Parameters.AddWithValue("@f", folderId);
            var bytes = Convert.ToInt64(cmd.ExecuteScalar());
            return bytes > 0 ? $" · {FormatBytes(bytes)} held" : "";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>
    /// Appends the server's size for the folder. Graph does not report folder
    /// bytes, so this comes from EWS with the token already held for live
    /// updates; without that capability the local figure stands alone.
    /// </summary>
    async Task ShowServerFolderSizeAsync(FolderNode node)
    {
        if (_appDb is null || node.Mailbox is not MailboxHandle mailbox) return;
        var accountUpn = mailbox.AccountUpn.Length > 0 ? mailbox.AccountUpn : mailbox.Upn;
        if (!MailboxRegistry.HasCapability(_appDb, accountUpn, AccountCapability.MappedLookup.Id))
            return;

        try
        {
            // Cached per mailbox: one EWS call covers every folder, and folder
            // sizes do not move fast enough to be worth re-fetching per click.
            if (!_folderSizes.TryGetValue(mailbox.Upn, out var sizes))
            {
                var token = await ExchangeTokenAsync(accountUpn);
                if (token is null) return;
                sizes = await new MailboxFolderSizes(_http)
                    .GetAsync(mailbox.Upn, token, accountUpn);
                if (sizes.Count == 0) return;
                _folderSizes[mailbox.Upn] = sizes;
            }

            if (!sizes.TryGetValue(node.Name, out var size) || size.Bytes <= 0) return;
            // Only update if the user is still on the folder we fetched for.
            if (FolderTree.SelectedItem is not FolderNodeViewModel current ||
                current.FolderId != node.FolderId) return;

            StatusText.Text =
                $"{mailbox.Upn} / {node.Name}: {MessageGrid.Items.Count:N0} message(s)" +
                LocalFolderSizeSuffix(mailbox, node.FolderId) +
                $" · {FormatBytes(size.Bytes)} on server";
        }
        catch (Exception ex)
        {
            Log(LogLevel.Verbose, $"Folder size unavailable: {ex.Message}");
        }
    }

    readonly Dictionary<string, IReadOnlyDictionary<string, MailboxFolderSizes.FolderSize>>
        _folderSizes = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A themed brush by key. Resolved on each read rather than cached, so a
    /// theme switch restyles rows that are already built.
    /// </summary>
    internal static Brush Themed(string key, Brush fallback) =>
        Application.Current?.TryFindResource(key) as Brush ?? fallback;

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
            if (_themeMessageBodies) html = ApplyThemeToHtml(html);
            PreviewView.CoreWebView2.NavigateToString(html);
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Preview failed: {ex.Message}");
            StatusText.Text = $"Preview failed: {ex.Message}";
        }
    }

    /// <summary>Whether the reading pane is currently restyling message bodies.</summary>
    bool _themeMessageBodies;

    /// <summary>
    /// Restyles a message body to match the app theme.
    ///
    /// Deliberately conservative. A blanket CSS filter inversion is the easy
    /// approach and the wrong one: it turns photographs into negatives and
    /// mangles logos. This instead supplies a dark ground and light text as
    /// *defaults*, which the sender's own rules still override — so a message
    /// that styles itself keeps its design, and one that does not becomes
    /// readable rather than black-on-black.
    ///
    /// Images are left completely alone. A signature graphic with a baked-in
    /// white background will still show as a white block; that is honest, and
    /// preferable to inverting the photograph next to it.
    /// </summary>
    static string ApplyThemeToHtml(string html)
    {
        var background = ThemeHex("Chrome.Background", "#282C34");
        var text = ThemeHex("Text.Primary", "#ABB2BF");
        var link = ThemeHex("Accent", "#528BFF");
        var border = ThemeHex("Chrome.Border", "#3E4451");

        // :where() keeps specificity at zero, so anything the sender declared —
        // inline or in their own <style> — still wins. That is the whole trick:
        // fill in what the message left unstated, override nothing it stated.
        var css =
            $"<style id=\"eeemail-theme\">" +
            $":where(html,body){{background:{background} !important;color:{text};}}" +
            $":where(p,div,span,td,th,li,h1,h2,h3,h4,h5,h6,blockquote,pre,code){{color:inherit;}}" +
            $":where(a){{color:{link};}}" +
            $":where(table,td,th,hr){{border-color:{border};}}" +
            // Measured against 38 real messages: 28 declare no background at
            // all, so the defaults above cover most of them. The other 10 set a
            // white background on a table — a template artefact rather than a
            // design choice — which is cleared here. Only white and near-white
            // are touched: a sender who chose a colour keeps it.
            $"[bgcolor='#ffffff'],[bgcolor='#fff'],[bgcolor='white']," +
            $"[style*='background-color:#ffffff'],[style*='background-color: #ffffff']," +
            $"[style*='background-color:#fff;'],[style*='background-color:white']," +
            $"[style*='background:#ffffff'],[style*='background: #ffffff']" +
            $"{{background-color:{background} !important;}}" +
            $"</style>";

        // Into <head> when there is one, otherwise in front of everything: an
        // email body is frequently a fragment rather than a whole document.
        var headEnd = html.IndexOf("</head>", StringComparison.OrdinalIgnoreCase);
        return headEnd >= 0
            ? html[..headEnd] + css + html[headEnd..]
            : css + html;
    }

    /// <summary>A theme brush as a CSS hex string.</summary>
    static string ThemeHex(string key, string fallback)
    {
        if (Application.Current?.TryFindResource(key) is SolidColorBrush brush)
        {
            var c = brush.Color;
            return $"#{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        return fallback;
    }

    /// <summary>
    /// Toggles restyling for the message being read. Per-message rather than
    /// only a setting, because whether restyling helps depends entirely on how
    /// the sender built the message — the reader has to be able to flip it.
    /// </summary>
    void OnToggleBodyTheme(object sender, RoutedEventArgs e)
    {
        _themeMessageBodies = BodyThemeToggle.IsChecked == true;
        BodyThemeToggle.ToolTip = _themeMessageBodies
            ? "Showing the message restyled to match the app. Click to see it as sent."
            : "Showing the message as it was sent. Click to restyle it to match the app.";
        _ = RenderPreviewAsync();
    }

    async Task EnsurePreviewAsync()
    {
        if (_webViewReady)
            return;
        var dataDir = Path.Combine(_dataDir ?? Path.GetTempPath(), "webview2");
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

    // The Delete tooltip promised a Del shortcut that did not exist. These are
    // the keys a mail client is expected to honour, so they are bound rather
    // than left to the context menu.
    public static readonly RoutedCommand DeleteCommand = new();
    public static readonly RoutedCommand MarkReadCommand = new();
    public static readonly RoutedCommand MarkUnreadCommand = new();
    public static readonly RoutedCommand SyncCommand = new();
    public static readonly RoutedCommand SettingsCommand = new();
    public static readonly RoutedCommand FocusSearchCommand = new();

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

    void OpenCompose(Draft? seed, long draftId = 0)
    {
        if (_mailboxes.Count == 0)
        {
            StatusText.Text = "No account configured.";
            return;
        }
        var accounts = _mailboxes.Select(m => m.Upn).ToList();
        var window = new ComposeWindow(accounts, SendAsync, seed, _drafts, draftId, SignatureFor)
        {
            Owner = this,
        };
        window.ShowDialog();
        UpdateDraftsButton();
    }

    /// <summary>
    /// Shows how many unsent messages are waiting. A draft that is saved but
    /// invisible is barely better than one that was lost.
    /// </summary>
    void UpdateDraftsButton()
    {
        var count = _drafts?.Count() ?? 0;
        DraftsButton.Content = count == 0 ? "Drafts" : $"Drafts ({count:N0})";
        DraftsButton.Visibility = count == 0 ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>Offers the saved drafts and reopens the chosen one.</summary>
    void OnDraftsClick(object sender, RoutedEventArgs e)
    {
        if (_drafts is null) return;
        var saved = _drafts.List();
        if (saved.Count == 0)
        {
            StatusText.Text = "No saved drafts.";
            UpdateDraftsButton();
            return;
        }

        var menu = new ContextMenu();
        foreach (var draft in saved)
        {
            var item = new MenuItem
            {
                Header = $"{draft.Display}  —  {draft.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm}",
                Tag = draft.Id,
            };
            item.Click += (_, _) => ResumeDraft(draft.Id);
            menu.Items.Add(item);
        }
        menu.Items.Add(new Separator());
        var discard = new MenuItem { Header = $"Discard all ({saved.Count:N0})" };
        discard.Click += (_, _) => DiscardAllDrafts(saved.Count);
        menu.Items.Add(discard);

        menu.PlacementTarget = DraftsButton;
        menu.IsOpen = true;
    }

    void ResumeDraft(long id)
    {
        if (_drafts?.Get(id) is not { } saved) return;
        var seed = new Draft(
            saved.From,
            Split(saved.To), Split(saved.Cc), Split(saved.Bcc),
            saved.Subject, saved.Body,
            saved.InReplyTo,
            saved.References is { Length: > 0 } refs
                ? refs.Split(' ', StringSplitOptions.RemoveEmptyEntries) : null,
            [.. saved.Attachments.Select(a =>
                new DraftAttachment(a.FileName, a.ContentType, a.Content))],
            saved.HtmlBody);
        OpenCompose(seed, id);

        static IReadOnlyList<string> Split(string value) =>
            value.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries |
                                    StringSplitOptions.TrimEntries);
    }

    void DiscardAllDrafts(int count)
    {
        if (_drafts is null) return;
        var confirm = MessageBox.Show(
            this,
            $"Discard {count:N0} saved draft(s)? They cannot be recovered.",
            "eeeMail — discard drafts",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        foreach (var draft in _drafts.List()) _drafts.Delete(draft.Id);
        Log($"Discarded {count:N0} draft(s).");
        UpdateDraftsButton();
    }

    /// <summary>
    /// The signature for a sending account, or null when none applies. Replies
    /// are excluded by default: appending a full signature to every message in
    /// a thread is what produces the stacked-signature mess at the bottom of
    /// long exchanges.
    /// </summary>
    string? SignatureFor(string fromAddress, bool isReply)
    {
        if (_settings is null || _appDb is null) return null;
        var mailbox = _mailboxes.FirstOrDefault(m =>
            string.Equals(m.Upn, fromAddress, StringComparison.OrdinalIgnoreCase));
        SettingTarget[] chain = mailbox is null
            ? [SettingTarget.Application]
            : [SettingTarget.Account(mailbox.AccountId), SettingTarget.Application];

        if (_settings.GetString(SettingsCatalog.SignatureSource, chain) != "local") return null;
        if (isReply && !_settings.GetBool(SettingsCatalog.SignatureOnReply, chain)) return null;

        var text = _settings.GetString(SettingsCatalog.SignatureText, chain);
        return string.IsNullOrWhiteSpace(text) ? null : text;
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

    /// <summary>
    /// Opens the unified settings window. Values are written as they change, so
    /// a reload afterwards is only needed when something structural moved.
    /// </summary>
    void OnSettingsClick(object sender, RoutedEventArgs e)
    {
        if (_appDb is null || _dataDir is null) return;
        var window = new SettingsWindow(_appDb)
        {
            Owner = this,
            AddAccount = AddAccountInteractiveAsync,
            Capabilities = accountUpn => ShowCapabilities(accountUpn),
            AddSharedMailbox = accountUpn => ShowSharedMailboxPicker(_dataDir, accountUpn),
        };
        window.ShowDialog();
        if (!window.ChangesApplied)
            return;

        // Reconcile rather than reopening everything: closing a mailbox that is
        // mid-sync to add an unrelated one would throw away its progress, and a
        // new mailbox should not wait for that either.
        Log("Settings changed.");
        ApplyDataDirectoryChange();
        ApplyTheme();
        ApplyColumnLayout(_openFolder);
        ApplyRowDensity(_openFolder);
        StartAutoSync();     // the interval may have changed
        StartLiveUpdates();  // and so may the accounts or the live-update setting
        ReconcileMailboxes();
    }

    /// <summary>
    /// Brings the open mailboxes in line with the registry: opens ones that were
    /// added, closes ones that were removed, and leaves the rest alone. Newly
    /// added mailboxes start syncing straight away rather than queueing behind
    /// whatever else is running.
    /// </summary>
    void ReconcileMailboxes()
    {
        if (_appDb is null || _dataDir is null) return;
        var registry = MailboxRegistry.List(_appDb, enabledOnly: true);

        var wanted = registry.Select(e => e.Upn).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var open = _mailboxes.Select(m => m.Upn).ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Removed or disabled: close and forget.
        foreach (var mailbox in _mailboxes.Where(m => !wanted.Contains(m.Upn)).ToList())
        {
            Log($"Closing {mailbox.Upn}.");
            mailbox.Db.Dispose();
            _mailboxes.Remove(mailbox);
        }

        // Added: open and start it immediately.
        var added = new List<MailboxHandle>();
        foreach (var entry in registry.Where(e => !open.Contains(e.Upn)))
        {
            var dbPath = Path.IsPathRooted(entry.DbPath)
                ? entry.DbPath
                : Path.Combine(_dataDir, entry.DbPath);
            try
            {
                var handle = new MailboxHandle(
                    entry.Upn, dbPath, entry.Dek, entry.WindowMonths, entry.Policy,
                    MailboxDatabase.Open(dbPath, entry.Dek))
                {
                    Id = entry.Id,
                    AccountId = entry.AccountId,
                    AccountUpn = entry.AccountUpn,
                    Kind = entry.Kind,
                    DisplayName = entry.DisplayName,
                };
                _mailboxes.Add(handle);
                added.Add(handle);
                Log($"Opened {entry.Upn} ({entry.Kind}).");
            }
            catch (Exception ex)
            {
                Log(LogLevel.Error, $"Could not open {entry.Upn}: {ex.Message}");
            }
        }

        // Registry order may have changed even when the set did not — moving an
        // account reorders the tree without adding or removing anything.
        var order = registry.Select((e, i) => (e.Upn, Index: i))
            .ToDictionary(x => x.Upn, x => x.Index, StringComparer.OrdinalIgnoreCase);
        _mailboxes.Sort((a, b) =>
            order.GetValueOrDefault(a.Upn, int.MaxValue)
                 .CompareTo(order.GetValueOrDefault(b.Upn, int.MaxValue)));

        RebuildTreePreservingState();
        StatusText.Text = $"{_mailboxes.Count} mailbox(es) open.";

        foreach (var mailbox in added)
            _ = SyncMailboxAsync(mailbox);
    }

    /// <summary>Signs a new account in and registers its mailbox.</summary>
    async Task<string> AddAccountInteractiveAsync()
    {
        if (_appDb is null || _dataDir is null) return "";

        // Ask before opening a browser at a provider's sign-in page. Microsoft
        // is the only one that works, but jumping straight there said nothing
        // about what is supported and assumed the account they meant.
        var chooser = new AddAccountWindow
        {
            Owner = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault()
                    ?? (Window)this,
        };
        chooser.ShowDialog();
        if (chooser.Provider is null)
        {
            Log("Add account cancelled.");
            return "";
        }

        try
        {
            var auth = new GraphAuthenticator(
                Path.Combine(_dataDir, "msal.cache"),
                () => new WindowInteropHelper(this).Handle);
            Log(LogLevel.Info,
                "Sign-in needed to add an account: a browser window will open. " +
                "Only the standard mail permissions are requested.");
            var result = await auth.SignInInteractiveAsync();
            var upn = result.Account.Username;

            if (MailboxRegistry.List(_appDb).Any(m =>
                    string.Equals(m.Upn, upn, StringComparison.OrdinalIgnoreCase)))
            {
                Log(LogLevel.Warning, $"{upn} is already registered.");
                return "";
            }

            MailboxRegistry.AddAccountWithMailbox(
                _appDb, upn, NewMailboxDirectory());
            Log($"Added account {upn}.");
            return upn;
        }
        catch (Exception ex)
        {
            Log(LogLevel.Error, $"Could not add the account: {ex.Message}");
            return "";
        }
    }

    void ShowCapabilities(string accountUpn)
    {
        if (_appDb is null) return;
        var window = new CapabilitiesWindow(_appDb, accountUpn)
        {
            Owner = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault() ?? (Window)this,
            ApplyCapabilities = ApplyCapabilitiesAsync,
        };
        window.ShowDialog();
    }

    bool ShowSharedMailboxPicker(string dataDirectory, string accountUpn)
    {
        if (_appDb is null) return false;
        var picker = new SharedMailboxWindow(
            _appDb, dataDirectory, GetGraphForAccountAsync, GetMappedMailboxesAsync, accountUpn)
        {
            Owner = Application.Current.Windows.OfType<SettingsWindow>().FirstOrDefault() ?? (Window)this,
        };
        picker.ShowDialog();
        return picker.MailboxesAdded;
    }

    /// <summary>
    /// Signs an account in requesting the scopes for a capability set, and
    /// reports which capabilities actually came back.
    ///
    /// Graph and Exchange are separate resources and a token may only name one,
    /// so they are requested in turn — the Exchange one only when a capability
    /// on that resource was asked for, to avoid a second prompt nobody needs.
    /// </summary>
    async Task<IReadOnlyList<string>> ApplyCapabilitiesAsync(
        string accountUpn, IReadOnlyList<string> wanted)
    {
        var auth = new GraphAuthenticator(
            Path.Combine(_dataDir!, "msal.cache"),
            () => new WindowInteropHelper(this).Handle);
        var granted = new List<string>();

        // Capabilities needing no scope are local gates: honour them as asked.
        foreach (var id in wanted)
            if (AccountCapability.ById(id) is { Scopes.Count: 0 })
                granted.Add(id);

        var graphScopes = AccountCapability.GraphScopesFor(wanted);
        var result = await SignInForScopesAsync(auth, accountUpn, graphScopes);
        AddGranted(result?.Scopes, "https://graph.microsoft.com/");

        if (AccountCapability.NeedsExchangeToken(wanted))
        {
            Log($"Requesting Exchange permissions for {accountUpn}…");
            var exchange = await SignInForScopesAsync(
                auth, accountUpn, GraphAuthenticator.ExchangeScopes);
            AddGranted(exchange?.Scopes, "https://outlook.office365.com/");
        }

        _graphClients.Remove(accountUpn);   // cached token predates the new scopes
        Log($"Capabilities for {accountUpn}: {string.Join(", ", granted)}");
        return granted;

        void AddGranted(IEnumerable<string>? scopes, string resource)
        {
            if (scopes is null) return;
            var held = scopes.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var capability in AccountCapability.All.Where(c => !c.Required))
            {
                var relevant = capability.Scopes
                    .Where(s => s.StartsWith(resource, StringComparison.OrdinalIgnoreCase))
                    .ToList();
                if (relevant.Count == 0) continue;
                // A scope may come back bare or fully qualified depending on the
                // tenant, so compare on the trailing name.
                if (relevant.All(s => held.Any(h =>
                        h.EndsWith(s[(s.LastIndexOf('/') + 1)..], StringComparison.OrdinalIgnoreCase))) &&
                    !granted.Contains(capability.Id))
                    granted.Add(capability.Id);
            }
        }
    }

    /// <summary>
    /// Silent first: after an administrator approves a request the grant already
    /// exists, so returning here completes with no browser at all.
    /// </summary>
    async Task<AuthenticationResult?> SignInForScopesAsync(
        GraphAuthenticator auth, string accountUpn, string[] scopes)
    {
        try
        {
            var accounts = await auth.GetAccountsAsync();
            var existing = accounts.FirstOrDefault(a =>
                string.Equals(a.Username, accountUpn, StringComparison.OrdinalIgnoreCase));
            var silent = existing is null ? null : await auth.AcquireSilentAsync(existing, scopes);
            if (silent is not null)
            {
                Log(LogLevel.Info,
                    $"{accountUpn}: the requested permissions were already granted — " +
                    "no sign-in needed.");
                return silent;
            }
            return await SignInExplainedAsync(auth, accountUpn, scopes,
                "you changed which capabilities this account may use, and the new " +
                "permissions have not been granted yet");
        }
        catch (MsalException)
        {
            // Refused or cancelled: the caller reports what was granted, and an
            // empty result is a truthful "nothing".
            return null;
        }
    }

    /// <summary>
    /// Asks Exchange which mailboxes are mapped to this account, the way Outlook
    /// does. Needs a token for the Exchange audience rather than Graph, so it is
    /// acquired here where interactive sign-in is possible. Returns nothing when
    /// the permission is absent or the account has no Exchange behind it, which
    /// is the normal case for consumer accounts.
    /// </summary>
    async Task<IReadOnlyList<AutodiscoverMailboxes.AlternateMailbox>> GetMappedMailboxesAsync(
        string accountUpn)
    {
        try
        {
            var auth = new GraphAuthenticator(
                Path.Combine(_dataDir!, "msal.cache"),
                () => new WindowInteropHelper(this).Handle);
            var accounts = await auth.GetAccountsAsync();
            var account = accounts.FirstOrDefault(a =>
                string.Equals(a.Username, accountUpn, StringComparison.OrdinalIgnoreCase));
            if (account is null) return [];

            // Only ask when the user granted it: without the capability this
            // would pop a consent prompt they already declined.
            if (!MailboxRegistry.HasCapability(_appDb!, accountUpn, AccountCapability.MappedLookup.Id))
            {
                Log(LogLevel.Verbose,
                    $"{accountUpn}: skipping mailbox lookup — \"{AccountCapability.MappedLookup.Name}\" is off.");
                return [];
            }

            // Deliberately silent-only: discovery is a convenience and must
            // never be the reason a browser appears.
            var token = await auth.AcquireSilentAsync(account, GraphAuthenticator.ExchangeScopes);
            if (token is null)
            {
                Log(LogLevel.Warning,
                    $"{accountUpn}: mailbox lookup needs Exchange permissions that are not " +
                    "granted. Enable it in Settings to request them.");
                return [];
            }

            var mapped = await new AutodiscoverMailboxes(_http)
                .GetAlternateMailboxesAsync(accountUpn, token.AccessToken);
            Log(LogLevel.Debug,
                $"Autodiscover: {mapped.Count:N0} mapped mailbox(es) for {accountUpn}.");
            return mapped;
        }
        catch (Exception ex)
        {
            // Never fatal: the picker still works by typed address.
            Log(LogLevel.Verbose, $"Autodiscover unavailable for {accountUpn}: {ex.Message}");
            return [];
        }
    }

    /// <summary>Re-reads the mailbox registry (after config changes) and rebuilds the tree.</summary>
    void ReloadMailboxes()
    {
        if (_appDb is null || _dataDir is null)
            return;
        LoadMailboxHandles(_dataDir);
        BuildTree();
        StatusText.Text = $"{_mailboxes.Count} mailbox(es) open.";
    }

    /// <summary>
    /// Fills <see cref="_mailboxes"/> from the registry. Startup and reload share
    /// this: when they were separate queries the reload path silently dropped the
    /// owning account, which a shared mailbox needs in order to authenticate.
    /// </summary>
    void LoadMailboxHandles(string dataDirectory)
    {
        foreach (var entry in MailboxRegistry.List(_appDb!, enabledOnly: true))
        {
            var dbPath = Path.IsPathRooted(entry.DbPath)
                ? entry.DbPath
                : Path.Combine(_dataDir, entry.DbPath);
            _mailboxes.Add(new MailboxHandle(
                entry.Upn, dbPath, entry.Dek, entry.WindowMonths, entry.Policy,
                MailboxDatabase.Open(dbPath, entry.Dek))
            {
                Id = entry.Id,
                AccountId = entry.AccountId,
                AccountUpn = entry.AccountUpn,
                Kind = entry.Kind,
                DisplayName = entry.DisplayName,
            });
        }
    }

    async void StartSync()
    {
        if (_dataDir is null || _mailboxes.Count == 0)
            return;

        // Fan out: each mailbox syncs on its own, so adding one starts it
        // immediately rather than queueing behind whatever else is running.
        foreach (var mailbox in _mailboxes.ToList())
            _ = SyncMailboxAsync(mailbox);
    }

    /// <summary>
    /// Syncs one mailbox. Independent of the others: its own guard, its own
    /// progress, and its own failure. A mailbox already syncing is left alone
    /// rather than started twice.
    /// </summary>
    async Task SyncMailboxAsync(MailboxHandle mailbox)
    {
        if (_dataDir is null) return;
        lock (_syncing)
        {
            if (!_syncing.Add(mailbox.Upn)) return;
        }
        UpdateSyncChrome();
        try
        {
            {
                _syncingFolders[mailbox.Upn] = "";

                // Settings win over the legacy mailbox columns: the columns are
                // only a fallback until every profile has been through V4.
                var chain = ChainFor(mailbox);
                var policy = _settings?.GetString(SettingsCatalog.SyncPolicy, chain) ?? mailbox.Policy;
                var windowMonths = _settings?.GetInt(SettingsCatalog.SyncWindowMonths, chain)
                    ?? mailbox.WindowMonths;
                if (_settings is not null && !_settings.GetBool(SettingsCatalog.SyncEnabled, chain))
                {
                    Log(LogLevel.Info, $"{mailbox.Upn}: skipped — sync is turned off for it.");
                    return;
                }

                var graph = await GetGraphAsync(mailbox);
                var since = policy == "MirrorServer" || windowMonths <= 0
                    ? (DateTimeOffset?)null
                    : DateTimeOffset.UtcNow.AddMonths(-windowMonths);
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
                                _syncingFolders[mailbox.Upn] = p.FolderName ?? "";
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

                var concurrency = _settings?.GetInt(SettingsCatalog.MaxConcurrentDownloads, chain) ?? 4;
                var sync = new GraphMailboxSync(
                    graph, () => MailboxDatabase.Open(mailbox.DbPath, mailbox.Dek), OnProgress,
                    maxConcurrentDownloads: Math.Clamp(concurrency, 1, 16),
                    mailboxAddress: mailbox.Upn);
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
            // Name where it stopped: "sync failed" with no location is the least
            // actionable message there is, and one mailbox failing must not read
            // as though everything did.
            var folder = _syncingFolders.GetValueOrDefault(mailbox.Upn, "");
            var where = folder.Length > 0 ? $"{mailbox.Upn} / {folder}" : mailbox.Upn;
            SyncLabel.Text = $"Sync failed at {where}: {ex.Message}";
            Log(LogLevel.Error, $"SYNC ERROR at {where}: {ex.Message}");
        }
        finally
        {
            lock (_syncing) _syncing.Remove(mailbox.Upn);
            _syncingFolders.Remove(mailbox.Upn);
            UpdateSyncChrome();
        }
    }

    /// <summary>
    /// Reflects how many mailboxes are syncing. The button stays usable while
    /// some are running so another can be started; only the ones in flight are
    /// skipped.
    /// </summary>
    void UpdateSyncChrome()
    {
        int running;
        lock (_syncing) running = _syncing.Count;

        SyncButton.Content = running == 0 ? "Sync now" : $"Syncing ({running})";
        SyncBar.IsIndeterminate = running > 0;
        if (running == 0)
        {
            SyncBar.Value = 0;
            SyncBar.Maximum = 100;
            ScanLabel.Text = "";
        }
    }
}
