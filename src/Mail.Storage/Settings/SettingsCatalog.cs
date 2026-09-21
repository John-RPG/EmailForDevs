using static Mail.Storage.Settings.SettingScope;

namespace Mail.Storage.Settings;

/// <summary>
/// Every setting eeeMail exposes, declared once.
///
/// The guiding rule is that a developer should be able to change anything that
/// is theirs to change, including the sharp things — with the risk stated rather
/// than the switch hidden. What is deliberately absent is not policy but data:
/// key material and access tokens have actions (rotate, revoke, export recovery
/// code) but their bytes are never rendered, because displaying them is what
/// puts them into screenshots and scrollback.
/// </summary>
public static class SettingsCatalog
{
    // ---- sync ---------------------------------------------------------------

    public static readonly SettingDefinition SyncPolicy = new(
        "sync.policy",
        "Sync policy",
        "MirrorServer keeps everything the server holds. WindowedCache keeps a " +
        "recent window and falls back to server search for older mail. " +
        "OnlineOnly stores nothing: every folder and message is read live from " +
        "the server, so no mail is written to this machine. That also means no " +
        "offline access, a round trip whenever you open a folder, and search " +
        "and conversation grouping answered by the server rather than by the " +
        "local index — which groups and ranks differently. An existing cache " +
        "is left alone rather than deleted, so switching back resumes from it.",
        SettingKind.Enum, "MirrorServer",
        [Folder, Mailbox, Account, Application],
        Choices: ["MirrorServer", "WindowedCache", "OnlineOnly"],
        Category: "Sync");

    public static readonly SettingDefinition SyncWindowMonths = new(
        "sync.window_months",
        "Window (months)",
        "How much history WindowedCache keeps. Ignored when mirroring.",
        SettingKind.Int, "0",
        [Folder, Mailbox, Account, Application],
        Risk: SettingRisk.Performance,
        Category: "Sync");

    public static readonly SettingDefinition SyncEnabled = new(
        "sync.enabled",
        "Sync this",
        "Turn off to leave a mailbox or folder registered but untouched.",
        SettingKind.Bool, "true",
        [Folder, Mailbox, Account],
        Category: "Sync");

    public static readonly SettingDefinition MaxConcurrentDownloads = new(
        "sync.max_concurrent_downloads",
        "Concurrent downloads",
        "Parallel message fetches per mailbox. Four matches the documented " +
        "per-mailbox limit; higher invites throttling, which is slower overall.",
        SettingKind.Int, "4",
        [Mailbox, Account, Application],
        Risk: SettingRisk.Performance,
        Warning: "Above 4 the service starts returning 429 and the adaptive " +
                 "limiter throttles back — usually a net loss.",
        Category: "Sync");

    public static readonly SettingDefinition AutoSyncSeconds = new(
        "sync.auto_interval_seconds",
        "Check for new mail every (seconds)",
        "How often to ask the server what changed. Zero disables automatic " +
        "checks, leaving Sync now.",
        SettingKind.Int, "120",
        [Account, Application],
        Risk: SettingRisk.Performance,
        Warning: "Graph offers no push for a desktop client — its change " +
                 "notifications need a public HTTPS endpoint — so this is a poll. " +
                 "Below about 60 seconds the service starts throttling, which " +
                 "delays mail rather than hastening it.",
        Category: "Sync");

    public static readonly SettingDefinition LiveUpdates = new(
        "sync.live_updates",
        "Live updates (no polling)",
        "Hold a connection open so the server can say the moment mail arrives, " +
        "instead of asking on a timer. Falls back to periodic checks where the " +
        "account does not support it.",
        SettingKind.Bool, "true",
        [Mailbox, Account, Application],
        Category: "Sync");

    public static readonly SettingDefinition SyncOnFocus = new(
        "sync.on_window_focus",
        "Check when the window is focused",
        "Ask the server what changed whenever you return to the app, so coming " +
        "back to it shows current mail without waiting for the next check.",
        SettingKind.Bool, "true",
        [Account, Application],
        Category: "Sync");

    public static readonly SettingDefinition PurgeLocalOnRemove = new(
        "sync.purge_local_on_remove",
        "Delete local copy when removing a mailbox",
        "Removes the encrypted database as well as the registry entry.",
        SettingKind.Bool, "true",
        [Application],
        Risk: SettingRisk.Destructive,
        Warning: "Local-only mail — anything since purged from the server — is " +
                 "lost with the database.",
        Category: "Sync");

    // ---- reading ------------------------------------------------------------

    public static readonly SettingDefinition LoadRemoteImages = new(
        "read.load_remote_images",
        "Load remote images",
        "Fetch images referenced by URL in HTML mail.",
        SettingKind.Enum, "never",
        [Folder, Mailbox, Account, Application],
        Risk: SettingRisk.Security,
        Warning: "Remote images confirm to the sender that the mail was opened, " +
                 "and leak the time and IP address. Tracking pixels rely on this.",
        Choices: ["never", "known senders", "always"],
        Category: "Reading");

    public static readonly SettingDefinition AllowScripts = new(
        "read.allow_scripts",
        "Allow scripts in HTML mail",
        "Run JavaScript found in message bodies.",
        SettingKind.Bool, "false",
        [Application],
        Risk: SettingRisk.Security,
        Warning: "Removes the main defence against hostile mail. The renderer is " +
                 "sandboxed and network-blocked, but this is still a bad idea " +
                 "outside deliberate analysis of a specific message.",
        Category: "Reading");

    public static readonly SettingDefinition ShowInlineAttachments = new(
        "read.show_inline_attachments",
        "Show inline attachments",
        "Inline parts are the images and signature graphics embedded in the " +
        "message body. They are attachments technically, but listing them hides " +
        "the file someone actually sent among a dozen logos.",
        SettingKind.Bool, "false",
        [Folder, Mailbox, Account, Application],
        Category: "Reading");

    public static readonly SettingDefinition PreviewAttachments = new(
        "read.preview_attachments",
        "Preview attachments",
        "Show images, text and PDFs in the reading pane when selected, rather " +
        "than only offering to save them.",
        SettingKind.Bool, "true",
        [Mailbox, Account, Application],
        Category: "Reading");

    public static readonly SettingDefinition PreviewSizeLimitKb = new(
        "read.preview_size_limit_kb",
        "Preview size limit (KB)",
        "Attachments larger than this are not previewed automatically; they can " +
        "still be opened on request.",
        SettingKind.Int, "8192",
        [Mailbox, Account, Application],
        Risk: SettingRisk.Performance,
        Category: "Reading");

    public static readonly SettingDefinition OpenAttachmentsExternally = new(
        "read.open_attachments_externally",
        "Allow opening attachments in other applications",
        "Adds an Open button that hands the file to whatever program Windows " +
        "associates with it.",
        SettingKind.Bool, "false",
        [Application],
        Risk: SettingRisk.Security,
        Warning: "An attachment is untrusted input from whoever sent it. Opening " +
                 "one hands it to another program with none of this app's " +
                 "sandboxing — which is how a malicious document gets executed. " +
                 "Saving and inspecting first is safer.",
        Category: "Reading");

    public static readonly SettingDefinition MarkReadDelayMs = new(
        "read.mark_read_delay_ms",
        "Mark read after (ms)",
        "How long a message must stay selected before it counts as read. " +
        "Zero marks immediately; -1 never marks automatically.",
        SettingKind.Int, "0",
        [Folder, Mailbox, Account, Application],
        Category: "Reading");

    public static readonly SettingDefinition DefaultReaderTab = new(
        "read.default_tab",
        "Default reader tab",
        "Which view opens first for a message.",
        SettingKind.Enum, "Text",
        [Folder, Mailbox, Account, Application],
        Choices: ["Text", "HTML", "Headers", "MIME", "Attachments"],
        Category: "Reading");

    // ---- display ------------------------------------------------------------

    /// <summary>
    /// The list wants a scannable, fixed-width column; the reader is read as
    /// prose and can afford a friendlier form. One setting could not serve both,
    /// so they are separate — <see cref="DateTimeFormat"/> keeps the old key and
    /// remains the fallback for anywhere neither applies.
    /// </summary>
    public static readonly SettingDefinition ThemeMode = new(
        "display.theme",
        "Theme",
        "Light, dark, or whatever Windows is set to. Both themes are checked " +
        "against WCAG AA contrast, so text stays readable either way.",
        SettingKind.Enum, "system",
        [Application],
        Choices: ["system", "light", "dark"],
        Category: "Display");

    public static readonly SettingDefinition ThemeMessageBodies = new(
        "display.theme_message_bodies",
        "Apply the theme to message bodies",
        "Restyle HTML mail to match the app. Off shows the message exactly as " +
        "it was sent, which is the honest rendering; the reading pane has a " +
        "toggle either way.",
        SettingKind.Bool, "false",
        [Folder, Mailbox, Account, Application],
        Warning: "Restyling changes how a message looks. Senders bake colours " +
                 "into images and tables, so some mail reads worse this way — " +
                 "which is why the reading pane keeps a per-message toggle.",
        Category: "Display");

    public static readonly SettingDefinition DateTimeFormat = new(
        "display.datetime_format",
        "Date and time format",
        ".NET format string used anywhere without a more specific format.",
        SettingKind.String, "yyyy-MM-dd HH:mm:ss",
        [Account, Application],
        Category: "Display");

    public static readonly SettingDefinition ListDateFormat = new(
        "display.list_datetime_format",
        "Date format — message list",
        "Used in the Received column. Sortable, fixed-width forms read best here.",
        SettingKind.String, "yyyy-MM-dd HH:mm:ss",
        [Folder, Mailbox, Account, Application],
        Category: "Display");

    public static readonly SettingDefinition ReaderDateFormat = new(
        "display.reader_datetime_format",
        "Date format — reading pane",
        "Used in the message header, where a longer form is easier to read.",
        SettingKind.String, "ddd d MMM yyyy, HH:mm:ss",
        [Folder, Mailbox, Account, Application],
        Category: "Display");

    public static readonly SettingDefinition ShowAddressesNotNames = new(
        "display.show_addresses",
        "Always show real addresses",
        "Show the actual From and To addresses rather than substituting display " +
        "names, so a forgery is visible at a glance.",
        SettingKind.Bool, "true",
        [Folder, Mailbox, Account, Application],
        Category: "Display");

    public static readonly SettingDefinition QuietFolderDays = new(
        "display.quiet_folder_days",
        "Quiet folder threshold (days)",
        "A folder with nothing newer than this is hidden when quiet folders are " +
        "filtered out.",
        SettingKind.Int, "30",
        [Account, Application],
        Category: "Display");

    public static readonly SettingDefinition RowDensity = new(
        "display.row_density",
        "Message row density",
        "How many lines each message occupies in the list.",
        SettingKind.Enum, "single",
        [Folder, Mailbox, Account, Application],
        Choices: ["single", "two-line", "three-line"],
        Category: "Display");

    public static readonly SettingDefinition ShowInFavourites = new(
        "display.favourite",
        "Show in favourites",
        "Pin this folder to the favourites tree above the folder list.",
        SettingKind.Bool, "false",
        [Folder],
        Category: "Display");

    // ---- composing ----------------------------------------------------------

    public static readonly SettingDefinition DefaultComposeFormat = new(
        "compose.default_format",
        "Default format",
        "Whether new messages start as HTML or plain text.",
        SettingKind.Enum, "html",
        [Account, Application],
        Choices: ["html", "plain"],
        Category: "Composing");

    public static readonly SettingDefinition SignatureSource = new(
        "compose.signature_source",
        "Signature",
        "Whether to append a signature to new messages. Stored locally: " +
        "Microsoft exposes no API for Outlook's roaming signatures — Graph has " +
        "none, and the EWS user-configuration route that once held them returns " +
        "empty now that roaming signatures replaced it.",
        SettingKind.Enum, "none",
        [Account, Application],
        Choices: ["none", "local"],
        Category: "Composing");

    public static readonly SettingDefinition SignatureText = new(
        "compose.signature_local",
        "Signature text",
        "Appended to new messages. Plain text is used as written; when composing " +
        "in HTML it is wrapped, so line breaks survive.",
        SettingKind.String, "",
        [Account, Application],
        Category: "Composing");

    public static readonly SettingDefinition SignatureOnReply = new(
        "compose.signature_on_reply",
        "Signature on replies and forwards",
        "Whether to append the signature when replying, not only on new mail.",
        SettingKind.Bool, "false",
        [Account, Application],
        Category: "Composing");

    public static readonly SettingDefinition ReplyQuoteStyle = new(
        "compose.reply_quote_style",
        "Quote style",
        "How the original message is included in a reply.",
        SettingKind.Enum, "below with header",
        [Account, Application],
        Choices: ["below with header", "below plain", "inline prefixed", "none"],
        Category: "Composing");

    public static readonly SettingDefinition ReplyAboveQuote = new(
        "compose.reply_above_quote",
        "Start reply above the quote",
        "Place the cursor above the quoted text rather than below it.",
        SettingKind.Bool, "true",
        [Account, Application],
        Category: "Composing");

    public static readonly SettingDefinition SendDelaySeconds = new(
        "send.delay_seconds",
        "Undo send window (seconds)",
        "Hold outgoing mail this long before sending, so it can be recalled. " +
        "Zero sends immediately.",
        SettingKind.Int, "0",
        [Account, Application],
        Category: "Sending");

    public static readonly SettingDefinition SaveToSent = new(
        "send.save_to_sent",
        "Save copies to Sent Items",
        "Ask the server to file a copy of each sent message.",
        SettingKind.Bool, "true",
        [Account, Application],
        Category: "Sending");

    public static readonly SettingDefinition RequestReadReceipts = new(
        "send.request_read_receipts",
        "Request read receipts",
        "Ask recipients' clients to confirm the message was opened.",
        SettingKind.Bool, "false",
        [Account, Application],
        Category: "Sending");

    public static readonly SettingDefinition SendReadReceipts = new(
        "send.send_read_receipts",
        "Respond to read receipt requests",
        "Whether to answer senders who ask to be told you opened their mail.",
        SettingKind.Enum, "never",
        [Account, Application],
        Risk: SettingRisk.Security,
        Warning: "Answering confirms you read the message, and the time you did. " +
                 "Senders can use this to verify an address is live.",
        Choices: ["never", "ask", "always"],
        Category: "Sending");

    public static readonly SettingDefinition MessageColumns = new(
        "display.message_columns",
        "Message list columns",
        "Ordered, comma-separated column keys, in the order they appear. Any key " +
        "left out is hidden. Available: status, from, fromname, fromaddress, to, " +
        "received, size, subject. \"from\" is the combined \"Name <address>\" form; " +
        "\"fromname\" and \"fromaddress\" are the same thing split into two " +
        "sortable columns.",
        SettingKind.String, "status,from,to,received,size,subject",
        [Folder, Mailbox, Account, Application],
        Category: "Display");

    public static readonly SettingDefinition PreserveMessageId = new(
        "compose.derive_message_id_from_sender",
        "Derive Message-ID from sender domain",
        "Build the Message-ID from the sending address's domain rather than the " +
        "local machine name.",
        SettingKind.Bool, "true",
        [Account, Application],
        Risk: SettingRisk.Security,
        Warning: "Turning this off produces a Message-ID from this machine's " +
                 "hostname, which receiving spam filters treat as a forgery signal.",
        Category: "Composing");

    // ---- storage ------------------------------------------------------------

    public static readonly SettingDefinition DataDirectory = new(
        "storage.data_directory",
        "Data directory",
        "Where the profile, mailbox databases and caches are kept. Empty means " +
        "the default: %APPDATA%\\eeeMail. Point it at another drive to keep " +
        "large mailboxes off the system disk, or at a portable path to carry a " +
        "profile between machines.",
        SettingKind.String, "",
        [Application],
        Risk: SettingRisk.Performance,
        Warning: "Takes effect on restart, and moves nothing. The databases are " +
                 "open while the app runs, so it cannot copy them safely. Close " +
                 "eeeMail, move the old folder's contents into the new one, then " +
                 "start it again — starting without moving anything creates a " +
                 "fresh, empty profile there and leaves your mail behind.",
        Category: "Storage");

    public static readonly SettingDefinition MailboxDirectory = new(
        "storage.mailbox_directory",
        "Mailbox database directory",
        "Where new mailbox databases are created. Empty means a \"mailboxes\" " +
        "folder inside the data directory. Existing mailboxes remember their " +
        "own path and are unaffected.",
        SettingKind.String, "",
        [Application],
        Category: "Storage");

    // ---- updates ------------------------------------------------------------

    public static readonly SettingDefinition CheckForUpdates = new(
        "update.check_on_launch",
        "Check for updates on launch",
        "Ask GitHub once per launch whether a newer release exists. Only the " +
        "check is automatic: nothing is downloaded or installed without asking.",
        SettingKind.Bool, "true",
        [Application],
        Category: "Updates");

    public static readonly SettingDefinition UpdateRepository = new(
        "update.repository",
        "Update source",
        "The owner/repo that releases are fetched from. Point this at a fork to " +
        "take updates from somewhere else.",
        SettingKind.String, "John-RPG/EmailForDevs",
        [Application],
        Risk: SettingRisk.Security,
        Warning: "Updates are executable code. Point this only at a repository " +
                 "you trust as much as the original, because whoever controls it " +
                 "controls what runs on this machine.",
        Category: "Updates");

    // ---- diagnostics --------------------------------------------------------

    public static readonly SettingDefinition LogLevel = new(
        "diagnostics.log_level",
        "Activity log level",
        "Lowest severity recorded in the activity log.",
        SettingKind.Enum, "Info",
        [Application],
        Choices: ["Error", "Warning", "Info", "Verbose", "Debug"],
        Category: "Diagnostics");

    public static readonly SettingDefinition LogGraphRequests = new(
        "diagnostics.log_graph_requests",
        "Log Graph requests",
        "Record every Graph URL, status, and timing.",
        SettingKind.Bool, "false",
        [Application],
        Risk: SettingRisk.Security,
        Warning: "Request URLs contain mailbox addresses and message ids. Access " +
                 "tokens are never logged.",
        Category: "Diagnostics");

    public static readonly SettingDefinition KeepRawMime = new(
        "diagnostics.keep_raw_mime",
        "Keep raw MIME",
        "Store each message's original bytes so the MIME tab shows exactly what " +
        "arrived, byte for byte.",
        SettingKind.Bool, "true",
        [Mailbox, Account, Application],
        Risk: SettingRisk.Performance,
        Warning: "Turning this off roughly halves storage but makes the MIME tab " +
                 "a reconstruction rather than the original.",
        Category: "Diagnostics");

    public static readonly IReadOnlyList<SettingDefinition> All =
    [
        SyncPolicy, SyncWindowMonths, SyncEnabled, MaxConcurrentDownloads,
        AutoSyncSeconds, LiveUpdates, SyncOnFocus, PurgeLocalOnRemove,
        LoadRemoteImages, AllowScripts, MarkReadDelayMs, DefaultReaderTab,
        ShowInlineAttachments, PreviewAttachments, PreviewSizeLimitKb,
        OpenAttachmentsExternally,
        ThemeMode, ThemeMessageBodies,
        DateTimeFormat, ListDateFormat, ReaderDateFormat, ShowAddressesNotNames,
        QuietFolderDays, RowDensity, ShowInFavourites, MessageColumns,
        DefaultComposeFormat, PreserveMessageId,
        SignatureSource, SignatureText, SignatureOnReply,
        ReplyQuoteStyle, ReplyAboveQuote,
        SendDelaySeconds, SaveToSent, RequestReadReceipts, SendReadReceipts,
        DataDirectory, MailboxDirectory,
        CheckForUpdates, UpdateRepository,
        LogLevel, LogGraphRequests, KeepRawMime,
    ];

    public static SettingDefinition? ByKey(string key) =>
        All.FirstOrDefault(s => s.Key == key);

    /// <summary>Settings that may be set at a level, grouped for display.</summary>
    public static IEnumerable<IGrouping<string, SettingDefinition>> ForScope(SettingScope scope) =>
        All.Where(s => s.AppliesTo(scope))
           .GroupBy(s => s.Category ?? "General")
           .OrderBy(g => g.Key);
}
