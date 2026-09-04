using System.Runtime.Versioning;

namespace Mail.Sync.Auth;

public enum CapabilityRisk
{
    /// <summary>Needed for the app to function; not optional.</summary>
    Essential,

    /// <summary>Read-only, narrow, and scoped to the signed-in user's own rights.</summary>
    Normal,

    /// <summary>
    /// Grants more than the feature strictly needs, or reaches beyond the user's
    /// own data. Worth a deliberate decision, not worth avoiding outright.
    /// </summary>
    Broad,

    /// <summary>
    /// Can lose data, expose it, or act on the user's behalf in ways that are
    /// hard to undo. Always opt-in, always warned, never enabled by default.
    /// </summary>
    Dangerous,
}

/// <summary>
/// A unit of functionality the user can turn on per account, and the permissions
/// it costs.
///
/// Permissions are not a single all-or-nothing bundle. Some are unavoidable if
/// the app is to do anything; others buy one convenience and are reasonably
/// declined — particularly where a scope is broader than the feature needs, or
/// where the tenant's administrator has to be involved. Modelling them as named
/// capabilities lets the account settings say what each one costs and what is
/// lost by refusing it, rather than presenting an opaque "grant access?" prompt.
///
/// Ordering is deliberate: <see cref="All"/> runs from essential to optional, so
/// the settings list reads top-down as increasing cost.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed record AccountCapability(
    string Id,
    string Name,
    string Summary,
    string WithoutIt,
    IReadOnlyList<string> Scopes,
    bool Required = false,
    bool LikelyNeedsAdmin = false,
    CapabilityRisk Risk = CapabilityRisk.Normal,
    string? Warning = null)
{
    public bool IsDangerous => Risk == CapabilityRisk.Dangerous;

    /// <summary>Scope names without the resource prefix, for display.</summary>
    public IEnumerable<string> ShortScopes =>
        Scopes.Select(s => s[(s.LastIndexOf('/') + 1)..]);

    public static readonly AccountCapability Mail = new(
        "mail",
        "Read and send mail",
        "Mirror this mailbox and send from it. This is the app.",
        "Nothing works without it.",
        [
            "https://graph.microsoft.com/Mail.ReadWrite",
            "https://graph.microsoft.com/Mail.Send",
            "https://graph.microsoft.com/User.Read",
        ],
        Required: true,
        Risk: CapabilityRisk.Essential);

    public static readonly AccountCapability SharedMail = new(
        "shared",
        "Open shared mailboxes",
        "Read and send in mailboxes someone has granted you access to. " +
        "Exchange still decides which — this only allows the attempt.",
        "Shared mailboxes cannot be opened, even ones you have rights to.",
        [
            "https://graph.microsoft.com/Mail.ReadWrite.Shared",
            "https://graph.microsoft.com/Mail.Send.Shared",
        ],
        Risk: CapabilityRisk.Normal);

    public static readonly AccountCapability MappedLookup = new(
        "autodiscover",
        "List mailboxes mapped to you",
        "Ask Exchange which mailboxes have been mapped to you, the way Outlook " +
        "does — one call, and they appear ready to add.",
        "You can still add shared mailboxes by typing their address.",
        // EWS.AccessAsUser.All is wider than this feature needs: it grants full
        // Exchange Web Services access as the user. Autodiscover has no narrower
        // scope of its own, so the breadth is stated plainly and the capability
        // is left off by default.
        ["https://outlook.office365.com/EWS.AccessAsUser.All"],
        LikelyNeedsAdmin: true,
        Risk: CapabilityRisk.Broad,
        Warning: "EWS.AccessAsUser.All grants full Exchange Web Services access as you, " +
                 "which is wider than this feature uses — Autodiscover has no narrower " +
                 "scope. Still bounded by your own permissions.");

    public static readonly AccountCapability PeopleLookup = new(
        "people",
        "Suggest mailboxes you work with",
        "Use your own contacts and recent correspondents to suggest mailboxes " +
        "worth checking.",
        "No suggestions; mapped mailboxes and typed addresses still work.",
        ["https://graph.microsoft.com/People.Read"],
        Risk: CapabilityRisk.Normal);

    public static readonly AccountCapability DirectorySearch = new(
        "directory",
        "Search the company directory",
        "Look up any mailbox in the organisation by name, for ones you have " +
        "rights to but have never used.",
        "No directory search; you can still type an address in full.",
        ["https://graph.microsoft.com/User.ReadBasic.All"],
        LikelyNeedsAdmin: true,
        Risk: CapabilityRisk.Broad,
        Warning: "Reads names and addresses for everyone in the organisation, " +
                 "including people you have never contacted. No mail content.");

    public static readonly AccountCapability Calendars = new(
        "calendars",
        "Read calendars",
        "Resolve meeting invitations and show free/busy alongside mail.",
        "Invitations render as plain messages.",
        ["https://graph.microsoft.com/Calendars.Read"]);

    public static readonly AccountCapability Contacts = new(
        "contacts",
        "Read contacts",
        "Use your address book for recipient completion and sender names.",
        "Completion falls back to addresses seen in mail.",
        ["https://graph.microsoft.com/Contacts.Read"]);

    public static readonly AccountCapability MailboxSettings = new(
        "mailboxsettings",
        "Read mailbox settings",
        "Pick up the mailbox time zone, working hours, and automatic replies.",
        "Times display in this machine's time zone.",
        ["https://graph.microsoft.com/MailboxSettings.Read"]);

    public static readonly AccountCapability ManageRules = new(
        "rules",
        "Manage server-side rules",
        "Read and write the inbox rules that run on the server, so filing " +
        "happens whether or not this app is open.",
        "Rules can be read in Outlook but not changed here.",
        ["https://graph.microsoft.com/MailboxSettings.ReadWrite"],
        Risk: CapabilityRisk.Dangerous,
        Warning: "Server-side rules act on mail with no client running. A faulty " +
                 "rule can silently file, forward, or delete mail — including mail " +
                 "this app has not yet downloaded.");

    public static readonly AccountCapability PermanentDelete = new(
        "harddelete",
        "Permanently delete mail",
        "Allow purges that bypass Deleted Items, for clearing test mail without " +
        "a second pass.",
        "Deleting moves mail to Deleted Items, where it can be recovered.",
        // No extra scope: Mail.ReadWrite already permits it. The gate exists
        // because the capability is destructive, not because Entra demands one.
        [],
        Risk: CapabilityRisk.Dangerous,
        Warning: "Purged mail is not recoverable from Deleted Items. Recovery then " +
                 "depends on the tenant's retention policy, which may be nothing.");

    public static readonly IReadOnlyList<AccountCapability> All =
    [
        Mail, SharedMail, MappedLookup, PeopleLookup, DirectorySearch,
        Calendars, Contacts, MailboxSettings, ManageRules, PermanentDelete,
    ];

    public static AccountCapability? ById(string id) =>
        All.FirstOrDefault(c => c.Id == id);

    /// <summary>
    /// Graph scopes for the enabled capabilities. Exchange-audience scopes are
    /// excluded: a token request may only name one resource, so those are
    /// acquired separately.
    /// </summary>
    public static string[] GraphScopesFor(IEnumerable<string> enabledIds)
    {
        var ids = enabledIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return
        [
            .. All.Where(c => c.Required || ids.Contains(c.Id))
                  .SelectMany(c => c.Scopes)
                  .Where(s => s.StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase))
                  .Distinct(StringComparer.OrdinalIgnoreCase),
        ];
    }

    /// <summary>True when the enabled set includes a capability on the Exchange resource.</summary>
    public static bool NeedsExchangeToken(IEnumerable<string> enabledIds)
    {
        var ids = enabledIds.ToHashSet(StringComparer.OrdinalIgnoreCase);
        return All.Any(c => ids.Contains(c.Id) &&
            c.Scopes.Any(s => s.StartsWith("https://outlook.office365.com/", StringComparison.OrdinalIgnoreCase)));
    }
}
