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
        "recent window and falls back to server search for older mail.",
        SettingKind.Enum, "MirrorServer",
        [Folder, Mailbox, Account, Application],
        Choices: ["MirrorServer", "WindowedCache"],
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

    public static readonly SettingDefinition DateTimeFormat = new(
        "display.datetime_format",
        "Date and time format",
        ".NET format string used in the message list and reader.",
        SettingKind.String, "yyyy-MM-dd HH:mm:ss",
        [Account, Application],
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
        SyncPolicy, SyncWindowMonths, SyncEnabled, MaxConcurrentDownloads, PurgeLocalOnRemove,
        LoadRemoteImages, AllowScripts, MarkReadDelayMs, DefaultReaderTab,
        DateTimeFormat, ShowAddressesNotNames, QuietFolderDays, RowDensity, ShowInFavourites,
        DefaultComposeFormat, PreserveMessageId,
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
