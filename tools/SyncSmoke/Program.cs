// Full vertical slice: profile keys (DPAPI + recovery) → encrypted app.db →
// mailbox registry with per-mailbox DEK → MSAL auth → Graph delta sync into the
// encrypted mailbox DB → stats and a sample FTS search.
// Run from the repo root:  dotnet run --project tools/SyncSmoke [months]
using System.Diagnostics;
using System.Security.Cryptography;
using Mail.Storage;
using Mail.Storage.Database;
using Mail.Storage.Settings;
using Mail.Storage.Security;
using Mail.Sync.Auth;
using Mail.Sync.Graph;
using Microsoft.Data.Sqlite;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions.Authentication;

// First arg: sync window in months, 0 = everything (full mirror). "config"
// anywhere in the args: update the registry + reset checkpoints, then exit
// (lets the app do the actual download with its progress UI).
var months = args.Length > 0 && int.TryParse(args[0], out var m) ? m : 1;
var configOnly = args.Contains("config", StringComparer.OrdinalIgnoreCase);
var root = Path.Combine(Directory.GetCurrentDirectory(), ".scratch");
var profileDir = Path.Combine(root, "profile");

// --- profile master key (created once; recovery code printed once) ----------
var keyStore = new ProfileKeyStore(profileDir);
byte[] masterKey;
if (keyStore.Exists)
{
    masterKey = keyStore.Unlock();
}
else
{
    var created = keyStore.Create();
    masterKey = created.MasterKey;
    Console.WriteLine("Created dev profile.");
    Console.WriteLine($"RECOVERY CODE (shown once, dev profile): {created.RecoveryCode}");
    Console.WriteLine();
}

using var appDb = AppDatabase.Open(Path.Combine(profileDir, "app.db"), masterKey);

if (args.Contains("junk", StringComparer.OrdinalIgnoreCase))
{
    // Lists recent items directly from named folders. $search skips Junk on
    // Outlook.com, so enumerate the folder instead of trusting a search.
    var jAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    foreach (var acct in await jAuth.GetAccountsAsync())
    {
        var tok = await jAuth.AcquireSilentAsync(acct);
        if (tok is null) { Console.WriteLine($"{acct.Username}: needs sign-in"); continue; }
        var g = new GraphServiceClient(
            new BaseBearerTokenAuthenticationProvider(new GraphTokenProvider(jAuth, tok)));
        Console.WriteLine($"--- {acct.Username} ---");
        foreach (var folder in new[] { "junkemail", "inbox", "deleteditems" })
        {
            try
            {
                var page = await g.Me.MailFolders[folder].Messages.GetAsync(rc =>
                {
                    rc.QueryParameters.Select = ["subject", "receivedDateTime", "from"];
                    rc.QueryParameters.Top = 8;
                    rc.QueryParameters.Orderby = ["receivedDateTime desc"];
                });
                Console.WriteLine($"  [{folder}] {page?.Value?.Count ?? 0} recent:");
                foreach (var msg in page?.Value ?? [])
                {
                    var sender = msg.From?.EmailAddress?.Address ?? "(none)";
                    Console.WriteLine($"    {msg.ReceivedDateTime:u}  {sender,-32}  {msg.Subject}");
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"  [{folder}] failed: {ex.Message}");
            }
        }
    }
    return;
}

if (args.Contains("mapped", StringComparer.OrdinalIgnoreCase))
{
    // Exercises the real AutodiscoverMailboxes parser (not the probe's regex),
    // so the shipping code path is what gets verified.
    var mapAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    using var mapHttp = new HttpClient();
    var finder = new AutodiscoverMailboxes(mapHttp);
    foreach (var acct in await mapAuth.GetAccountsAsync())
    {
        Console.WriteLine($"=== {acct.Username} ===");
        AuthenticationResult? tok = null;
        try { tok = await mapAuth.AcquireSilentAsync(acct, GraphAuthenticator.ExchangeScopes); }
        catch (Exception ex) { Console.WriteLine($"  no Exchange token: {ex.Message.Split('.')[0]}"); }
        if (tok is null) { Console.WriteLine("  (skipped)"); continue; }

        var mapped = await finder.GetAlternateMailboxesAsync(acct.Username, tok.AccessToken);
        Console.WriteLine($"  {mapped.Count:N0} mapped mailbox(es):");
        foreach (var box in mapped)
            Console.WriteLine($"    [{box.Type,-8}] {box.SmtpAddress,-40} {box.DisplayName}");
    }
    return;
}

if (args.Contains("autodiscover", StringComparer.OrdinalIgnoreCase))
{
    // Outlook does not probe mailboxes to find out what it can open: it asks
    // Autodiscover, which returns an AlternateMailbox list built from the
    // directory's automapping links. One authenticated call, no access-denied
    // noise in the audit log. This checks whether the same route works for us.
    var adAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    using var http = new HttpClient();
    foreach (var acct in await adAuth.GetAccountsAsync())
    {
        Console.WriteLine($"=== {acct.Username} ===");
        // Autodiscover is an Outlook/EWS resource, not Graph: it needs a token
        // for outlook.office365.com, so ask for the EWS scope specifically.
        AuthenticationResult? tok = null;
        string[] ewsScopes = ["https://outlook.office365.com/EWS.AccessAsUser.All"];
        try { tok = await adAuth.AcquireSilentAsync(acct, ewsScopes); }
        catch (Exception ex) { Console.WriteLine($"  token failed: {ex.Message}"); }
        if (tok is null)
        {
            Console.WriteLine("  no EWS token (scope not consented) — trying Graph token instead");
            tok = await adAuth.AcquireSilentAsync(acct);
        }
        if (tok is null) { Console.WriteLine("  needs interactive sign-in"); continue; }

        var soap = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
                           xmlns:a="http://schemas.microsoft.com/exchange/2010/Autodiscover"
                           xmlns:wsa="http://www.w3.org/2005/08/addressing"
                           xmlns:xsi="http://www.w3.org/2001/XMLSchema-instance">
              <soap:Header>
                <a:RequestedServerVersion>Exchange2013</a:RequestedServerVersion>
                <wsa:Action>http://schemas.microsoft.com/exchange/2010/Autodiscover/Autodiscover/GetUserSettings</wsa:Action>
                <wsa:To>https://outlook.office365.com/autodiscover/autodiscover.svc</wsa:To>
              </soap:Header>
              <soap:Body>
                <a:GetUserSettingsRequestMessage>
                  <a:Request>
                    <a:Users><a:User><a:Mailbox>{acct.Username}</a:Mailbox></a:User></a:Users>
                    <a:RequestedSettings>
                      <a:Setting>UserDisplayName</a:Setting>
                      <a:Setting>AlternateMailboxes</a:Setting>
                    </a:RequestedSettings>
                  </a:Request>
                </a:GetUserSettingsRequestMessage>
              </soap:Body>
            </soap:Envelope>
            """;

        using var req = new HttpRequestMessage(HttpMethod.Post,
            "https://outlook.office365.com/autodiscover/autodiscover.svc");
        req.Headers.Authorization = new("Bearer", tok.AccessToken);
        req.Content = new StringContent(soap, System.Text.Encoding.UTF8, "text/xml");
        try
        {
            var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"  HTTP {(int)resp.StatusCode} {resp.StatusCode}");
            if (body.Contains("AlternateMailbox", StringComparison.OrdinalIgnoreCase))
            {
                foreach (System.Text.RegularExpressions.Match alt in
                         System.Text.RegularExpressions.Regex.Matches(body,
                             @"<AlternateMailbox>(.*?)</AlternateMailbox>",
                             System.Text.RegularExpressions.RegexOptions.Singleline))
                    Console.WriteLine("    " + alt.Groups[1].Value
                        .Replace("\n", " ").Replace("\r", " "));
            }
            else
            {
                Console.WriteLine("    (no AlternateMailboxes in response)");
                Console.WriteLine("    " + body[..Math.Min(600, body.Length)]);
            }
        }
        catch (Exception ex) { Console.WriteLine($"  request failed: {ex.Message}"); }
    }
    return;
}

if (args.Contains("scopecheck", StringComparer.OrdinalIgnoreCase))
{
    // Compares the scopes the app now asks for against what the cached token
    // actually holds. A mismatch means MSAL cannot answer silently, which is
    // what forces a browser prompt on every launch.
    var scAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    foreach (var acct in await scAuth.GetAccountsAsync())
    {
        Console.WriteLine($"=== {acct.Username} ===");
        var caps = MailboxRegistry.GetCapabilities(appDb, acct.Username);
        var wanted = AccountCapability.GraphScopesFor(caps);
        Console.WriteLine($"  capabilities recorded : [{string.Join(",", caps.OrderBy(c => c))}]");
        Console.WriteLine($"  app will request      : {string.Join(" ", wanted.Select(w => w[(w.LastIndexOf('/') + 1)..]))}");

        var silent = await scAuth.AcquireSilentAsync(acct, wanted);
        if (silent is null)
        {
            Console.WriteLine("  SILENT FAILS -> would open a browser");
            var basic = await scAuth.AcquireSilentAsync(acct, GraphAuthenticator.MailScopes);
            Console.WriteLine(basic is null
                ? "  (even the base mail scopes fail silently)"
                : $"  but base mail scopes succeed, holding: {string.Join(" ", basic.Scopes.Select(x => x[(x.LastIndexOf('/') + 1)..]))}");
        }
        else
        {
            Console.WriteLine($"  silent OK, token holds: {string.Join(" ", silent.Scopes.Select(x => x[(x.LastIndexOf('/') + 1)..]))}");
        }
    }
    return;
}

if (args.Contains("provewiring", StringComparer.OrdinalIgnoreCase))
{
    // Proves the app reads settings rather than the legacy columns, by resolving
    // exactly what the sync loop resolves for each mailbox.
    var store = new SettingsStore(appDb);
    Console.WriteLine("Effective sync settings per mailbox (as the app resolves them):");
    foreach (var entry in MailboxRegistry.List(appDb, enabledOnly: true))
    {
        SettingTarget[] chain =
        [
            SettingTarget.Mailbox(entry.Id),
            SettingTarget.Account(entry.AccountId),
            SettingTarget.Application,
        ];
        var policy = store.Resolve(SettingsCatalog.SyncPolicy, chain);
        var window = store.Resolve(SettingsCatalog.SyncWindowMonths, chain);
        var conc = store.Resolve(SettingsCatalog.MaxConcurrentDownloads, chain);
        var enabled = store.Resolve(SettingsCatalog.SyncEnabled, chain);
        var listFmt = store.Resolve(SettingsCatalog.ListDateFormat, chain);

        Console.WriteLine($"  {entry.Upn}");
        Console.WriteLine($"      policy      {policy.Value,-14} (from {policy.Source})");
        Console.WriteLine($"      window      {window.Value,-14} (from {window.Source})");
        Console.WriteLine($"      concurrency {conc.Value,-14} (from {conc.Source})");
        Console.WriteLine($"      enabled     {enabled.Value,-14} (from {enabled.Source})");
        Console.WriteLine($"      list dates  {listFmt.Value,-14} (from {listFmt.Source})");
        Console.WriteLine($"      legacy columns said: policy={entry.Policy} window={entry.WindowMonths}");
    }

    Console.WriteLine();
    Console.WriteLine("Favourites now stored as folder settings:");
    using (var fav = appDb.CreateCommand())
    {
        fav.CommandText = """
            SELECT target FROM settings
            WHERE key = 'display.favourite' AND value = 'true' ORDER BY target;
            """;
        using var reader = fav.ExecuteReader();
        var any = false;
        while (reader.Read()) { any = true; Console.WriteLine($"      folder {reader.GetString(0)}"); }
        if (!any) Console.WriteLine("      (none)");
    }

    using (var legacy = appDb.CreateCommand())
    {
        legacy.CommandText = "SELECT count(*) FROM ui_state WHERE key = 'favourites';";
        Console.WriteLine($"  legacy ui_state blob rows remaining: {legacy.ExecuteScalar()}");
    }
    return;
}

if (args.Contains("drafts", StringComparer.OrdinalIgnoreCase))
{
    var store = new DraftStore(appDb);
    var saved = store.List();
    Console.WriteLine($"{saved.Count:N0} saved draft(s):");
    foreach (var d in saved)
    {
        Console.WriteLine($"  [{d.Id}] {d.Display}");
        Console.WriteLine($"      from={d.From} rich={d.IsRich} updated={d.UpdatedAt.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
        var body = (d.HtmlBody ?? d.Body).Replace("\r", " ").Replace("\n", " ");
        Console.WriteLine($"      body: {body[..Math.Min(90, body.Length)]}");
        if (d.Attachments.Count > 0)
            Console.WriteLine($"      attachments: {string.Join(", ", d.Attachments.Select(a => a.FileName))}");
    }
    return;
}

if (args.Contains("settings", StringComparer.OrdinalIgnoreCase))
{
    // Walks the real store against the live profile, so inheritance is checked
    // on actual account/mailbox ids rather than only in unit tests.
    var store = new SettingsStore(appDb);
    var entries = MailboxRegistry.List(appDb);
    var work = entries.FirstOrDefault(e => e.Upn.Contains("ExampleCorp", StringComparison.OrdinalIgnoreCase))
               ?? entries.First();

    var accountTarget = SettingTarget.Account(work.AccountId);
    var mailboxTarget = SettingTarget.Mailbox(work.Id);
    SettingTarget[] chain = [mailboxTarget, accountTarget, SettingTarget.Application];

    void Show(string label)
    {
        var r = store.Resolve(SettingsCatalog.LoadRemoteImages, chain);
        Console.WriteLine($"  {label,-34} value={r.Value,-14} from={r.Source,-11} default={r.IsDefault}");
    }

    Console.WriteLine($"=== {work.Upn} (mailbox {work.Id}, account {work.AccountId}) ===");
    Show("initial");

    store.Set(SettingsCatalog.LoadRemoteImages.Key, SettingTarget.Application, "always");
    Show("after application=always");

    store.Set(SettingsCatalog.LoadRemoteImages.Key, accountTarget, "known senders");
    Show("after account=known senders");

    store.Set(SettingsCatalog.LoadRemoteImages.Key, mailboxTarget, "never");
    Show("after mailbox=never");

    store.Clear(SettingsCatalog.LoadRemoteImages.Key, mailboxTarget);
    Show("after clearing mailbox");

    store.Clear(SettingsCatalog.LoadRemoteImages.Key, accountTarget);
    Show("after clearing account");

    store.Clear(SettingsCatalog.LoadRemoteImages.Key, SettingTarget.Application);
    Show("after clearing application");

    Console.WriteLine();
    Console.WriteLine("Overrides carried over by the V4 migration:");
    foreach (var entry in entries)
    {
        var overrides = store.Overrides(SettingTarget.Mailbox(entry.Id));
        if (overrides.Count == 0) continue;
        Console.WriteLine($"  {entry.Upn}");
        foreach (var (key, value) in overrides)
            Console.WriteLine($"      {key} = {value}");
    }
    return;
}

if (args.Contains("registry", StringComparer.OrdinalIgnoreCase))
{
    foreach (var entry in MailboxRegistry.List(appDb))
        Console.WriteLine(
            $"  [{entry.Kind,-7}] {entry.Upn,-34} account={entry.AccountUpn,-30} " +
            $"caps=[{string.Join(",", entry.Capabilities.OrderBy(c => c))}]  {entry.DbPath}");
    return;
}

if (args.Contains("shared", StringComparer.OrdinalIgnoreCase))
{
    // Graph has no "list the mailboxes I can open" endpoint, so discovery has to
    // triangulate. Try each candidate route and report exactly what each one
    // yields, so the UI picker can be built on whichever actually works here.
    var sharedAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    foreach (var acct in await sharedAuth.GetAccountsAsync())
    {
        var tok = await sharedAuth.AcquireSilentAsync(acct);
        if (tok is null) { Console.WriteLine($"{acct.Username}: needs interactive sign-in"); continue; }
        Console.WriteLine($"=== {acct.Username} ===");
        Console.WriteLine($"    granted scopes: {string.Join(" ", tok.Scopes)}");
        var g = new GraphServiceClient(
            new BaseBearerTokenAuthenticationProvider(new GraphTokenProvider(sharedAuth, tok)));

        // 1. Group mailboxes (Microsoft 365 groups the user belongs to).
        try
        {
            var groups = await g.Me.MemberOf.GetAsync(rc => rc.QueryParameters.Top = 50);
            var mailEnabled = (groups?.Value ?? []).OfType<Group>()
                .Where(x => x.MailEnabled == true && x.Mail is not null).ToList();
            Console.WriteLine($"  [memberOf] {mailEnabled.Count} mail-enabled group(s)");
            foreach (var x in mailEnabled.Take(20))
                Console.WriteLine($"      {x.Mail}  ({x.DisplayName})");
        }
        catch (Exception ex) { Console.WriteLine($"  [memberOf] failed: {Short(ex)}"); }

        // 2. Directory users with a mailbox — a shared mailbox is a user object
        //    with no licence. Only useful if the tenant allows directory reads.
        try
        {
            var users = await g.Users.GetAsync(rc =>
            {
                rc.QueryParameters.Select = ["displayName", "mail", "userPrincipalName", "userType"];
                rc.QueryParameters.Filter = "mail ne null";
                rc.QueryParameters.Top = 25;
                rc.Headers.Add("ConsistencyLevel", "eventual");
            });
            Console.WriteLine($"  [users] {(users?.Value?.Count ?? 0)} directory user(s) with mail (first 25)");
            foreach (var u in (users?.Value ?? []).Take(25))
                Console.WriteLine($"      {u.Mail}  ({u.DisplayName}) type={u.UserType}");
        }
        catch (Exception ex) { Console.WriteLine($"  [users] failed: {Short(ex)}"); }

        // 3. Mailbox settings / delegate hints. Automapped mailboxes are the ones
        //    Outlook opens without being asked, so if any route lists them this
        //    is where it shows up.
        try
        {
            var people = await g.Me.People.GetAsync(rc =>
            {
                rc.QueryParameters.Top = 25;
                rc.QueryParameters.Select = ["displayName", "scoredEmailAddresses", "personType"];
            });
            var mailboxes = (people?.Value ?? [])
                .Where(x => x.PersonType?.Subclass is "OrganizationUser" or "SharedMailbox" or "Group")
                .ToList();
            Console.WriteLine($"  [people] {mailboxes.Count} org/shared entries of {(people?.Value?.Count ?? 0)}");
            foreach (var x in mailboxes.Take(25))
                Console.WriteLine($"      {x.ScoredEmailAddresses?.FirstOrDefault()?.Address}  ({x.DisplayName}) sub={x.PersonType?.Subclass}");
        }
        catch (Exception ex) { Console.WriteLine($"  [people] failed: {Short(ex)}"); }

        // 4. findRooms/findMeetingTimes are calendar-only; the closest mail
        //    equivalent is the mailbox settings resource, which at least proves
        //    whether we can read another mailbox's configuration.
        try
        {
            var settings = await g.Me.MailboxSettings.GetAsync();
            Console.WriteLine($"  [settings] tz={settings?.TimeZone} lang={settings?.Language?.Locale}");
        }
        catch (Exception ex) { Console.WriteLine($"  [settings] failed: {Short(ex)}"); }

        // 5. Direct access test against any explicitly named candidates: the
        //    only conclusive check, since permission is per-mailbox.
        foreach (var candidate in args.SkipWhile(a =>
                     !a.Equals("shared", StringComparison.OrdinalIgnoreCase)).Skip(1))
        {
            try
            {
                var inbox = await g.Users[candidate].MailFolders["inbox"].GetAsync();
                Console.WriteLine($"  [open] {candidate}: OK — inbox {inbox?.TotalItemCount:N0} items, {inbox?.UnreadItemCount:N0} unread");
            }
            catch (Exception ex) { Console.WriteLine($"  [open] {candidate}: {Short(ex)}"); }
        }
    }
    return;

    static string Short(Exception ex) =>
        ex is ODataError o ? $"{o.ResponseStatusCode} {o.Error?.Code}: {o.Error?.Message}" : ex.Message;
}

if (args.Contains("probe", StringComparer.OrdinalIgnoreCase))
{
    // Asks Graph directly (bypassing our sync) where the test messages are,
    // so we can tell a delivery problem from a sync problem.
    var probeAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    var probeAccounts = await probeAuth.GetAccountsAsync();
    foreach (var acct in probeAccounts)
    {
        var tok = await probeAuth.AcquireSilentAsync(acct);
        if (tok is null) { Console.WriteLine($"{acct.Username}: needs interactive sign-in"); continue; }
        var g = new GraphServiceClient(
            new BaseBearerTokenAuthenticationProvider(new GraphTokenProvider(probeAuth, tok)));
        Console.WriteLine($"--- {acct.Username} ---");
        try
        {
            var found = await g.Me.Messages.GetAsync(rc =>
            {
                rc.QueryParameters.Search = "\"eeeMail\"";
                rc.QueryParameters.Select = ["subject", "receivedDateTime", "parentFolderId", "isDraft"];
                rc.QueryParameters.Top = 10;
            });
            if (found?.Value is null || found.Value.Count == 0)
                Console.WriteLine("  (no matching messages on the server)");
            foreach (var msg in found?.Value ?? [])
            {
                var folderName = msg.ParentFolderId;
                try
                {
                    var f = await g.Me.MailFolders[msg.ParentFolderId].GetAsync();
                    folderName = f?.DisplayName ?? msg.ParentFolderId;
                }
                catch { }
                Console.WriteLine($"  [{folderName}] {msg.ReceivedDateTime:u}  {msg.Subject}  draft={msg.IsDraft}");
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  probe failed: {ex.Message}");
        }
    }
    return;
}

if (args.Contains("timing", StringComparer.OrdinalIgnoreCase))
{
    // Reproduces the app's startup work with timings, against the real DBs.
    var sw = System.Diagnostics.Stopwatch.StartNew();
    using var list = appDb.CreateCommand();
    list.CommandText = "SELECT upn, db_path, dek FROM mailboxes WHERE enabled = 1 ORDER BY id;";
    var boxes = new List<(string Upn, string Path, byte[] Dek)>();
    using (var rows3 = list.ExecuteReader())
        while (rows3.Read())
            boxes.Add((rows3.GetString(0), Path.GetFullPath(rows3.GetString(1)), (byte[])rows3.GetValue(2)));
    Console.WriteLine($"registry read            : {sw.ElapsedMilliseconds,6:N0} ms");

    foreach (var box in boxes)
    {
        sw.Restart();
        using var db = MailboxDatabase.Open(box.Path, box.Dek);
        Console.WriteLine($"{box.Upn}");
        Console.WriteLine($"  open + migrate         : {sw.ElapsedMilliseconds,6:N0} ms");

        sw.Restart();
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT id, parent_id, name, special_use, server_id, total_count, unread_count FROM folders;";
            using var r = c.ExecuteReader();
            var n = 0; while (r.Read()) n++;
            Console.WriteLine($"  folders query ({n,3})    : {sw.ElapsedMilliseconds,6:N0} ms");
        }

        sw.Restart();
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT folder_id, count(*), coalesce(sum(1 - is_read), 0) FROM messages GROUP BY folder_id;";
            using var r = c.ExecuteReader();
            while (r.Read()) { }
            Console.WriteLine($"  local counts (GROUP BY): {sw.ElapsedMilliseconds,6:N0} ms   <-- per tree refresh");
        }

        sw.Restart();
        using (var c = db.CreateCommand())
        {
            c.CommandText = "SELECT count(*) FROM messages;";
            c.ExecuteScalar();
            Console.WriteLine($"  count(*) messages      : {sw.ElapsedMilliseconds,6:N0} ms");
        }
    }
    return;
}

if (args.Contains("stats", StringComparer.OrdinalIgnoreCase))
{
    using var list = appDb.CreateCommand();
    list.CommandText = "SELECT upn, db_path, dek FROM mailboxes ORDER BY id;";
    using var rows = list.ExecuteReader();
    while (rows.Read())
    {
        var statUpn = rows.GetString(0);
        var statPath = rows.GetString(1);
        if (!Path.IsPathRooted(statPath))
            statPath = Path.GetFullPath(statPath);
        using var statDb = MailboxDatabase.Open(statPath, (byte[])rows.GetValue(2));
        Console.WriteLine($"— {statUpn} —");
        Console.WriteLine($"Messages       : {Scalar(statDb, "SELECT count(*) FROM messages;"):N0}");
        Console.WriteLine($"Unique blobs   : {Scalar(statDb, "SELECT count(*) FROM blobs;"):N0}");
        Console.WriteLine($"Raw bytes      : {Convert.ToInt64(Scalar(statDb, "SELECT coalesce(sum(raw_size),0) FROM bodies;")),15:N0}");
        Console.WriteLine($"Stored bytes   : {Convert.ToInt64(Scalar(statDb, "SELECT coalesce(sum(length(content)),0) FROM blobs;")),15:N0}");
        Console.WriteLine($"DB file        : {new FileInfo(statPath).Length,15:N0}");
    }
    return;
}

// --- sign in (silent via cached token when possible) -------------------------
var auth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
AuthenticationResult? signIn = null;
var accounts = await auth.GetAccountsAsync();
if (accounts.Count > 0)
    signIn = await auth.AcquireSilentAsync(accounts[0]);
signIn ??= await auth.SignInInteractiveAsync();
var upn = signIn.Account.Username;
Console.WriteLine($"Signed in as {upn}");

// --- mailbox registry row + DEK ----------------------------------------------
var dek = EnsureMailboxRegistered(appDb, upn, out var mailboxDbPath);
using var mailboxDb = MailboxDatabase.Open(mailboxDbPath, dek);

if (args.Contains("findrecent", StringComparer.OrdinalIgnoreCase))
{
    using var find = mailboxDb.CreateCommand();
    find.CommandText = """
        SELECT m.id, m.subject, m.internet_message_id,
               datetime(m.received_at, 'unixepoch', 'localtime'), f.name
        FROM messages m JOIN folders f ON f.id = m.folder_id
        WHERE m.subject LIKE '%eeeMail send test%' OR m.subject LIKE '%eeeMail reply test%'
        ORDER BY m.received_at DESC LIMIT 10;
        """;
    using var rows2 = find.ExecuteReader();
    var any = false;
    while (rows2.Read())
    {
        any = true;
        Console.WriteLine($"  [{rows2.GetString(4)}] {rows2.GetString(3)}  {rows2.GetString(1)}");
        Console.WriteLine($"      message-id: {(rows2.IsDBNull(2) ? "(none)" : rows2.GetString(2))}  local id: {rows2.GetInt64(0)}");
    }
    if (!any) Console.WriteLine("  (no test messages found in this mailbox yet)");
    return;
}

if (args.Contains("sendtest", StringComparer.OrdinalIgnoreCase))
{
    // Sends a threaded pair between the two configured accounts using the same
    // ReplyBuilder + raw-MIME path the UI uses, so problems surface here first.
    var recipient = args.SkipWhile(a => !a.Equals("sendtest", StringComparison.OrdinalIgnoreCase))
        .Skip(1).FirstOrDefault();
    if (recipient is null)
    {
        Console.WriteLine("Usage: sendtest <recipient-address>");
        return;
    }
    var stamp = DateTime.Now.ToString("HH:mm:ss");
    var draft = new Mail.Core.Compose.Draft(
        From: upn,
        To: [recipient],
        Cc: [],
        Bcc: [],
        Subject: $"eeeMail send test {stamp}",
        Body: $"Sent by eeeMail at {stamp}.\n\nThis exercises the raw-MIME send path.");
    var outgoing = Mail.Core.Compose.ReplyBuilder.ToMimeMessage(draft);

    using var outBuffer = new MemoryStream();
    outgoing.WriteTo(outBuffer);
    Console.WriteLine("--- outgoing MIME (headers) ---");
    var text = System.Text.Encoding.ASCII.GetString(outBuffer.ToArray());
    foreach (var line in text.Split('\n').TakeWhile(l => l.Trim().Length > 0))
        Console.WriteLine("  " + line.TrimEnd());

    var base64 = Convert.ToBase64String(outBuffer.ToArray());
    using var http = new HttpClient();
    using var request = new HttpRequestMessage(HttpMethod.Post, "https://graph.microsoft.com/v1.0/me/sendMail")
    {
        Content = new StringContent(base64, System.Text.Encoding.ASCII, "text/plain"),
    };
    request.Headers.Authorization =
        new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", signIn.AccessToken);
    var response = await http.SendAsync(request);
    Console.WriteLine();
    Console.WriteLine($"Graph responded: {(int)response.StatusCode} {response.StatusCode}");
    if (!response.IsSuccessStatusCode)
        Console.WriteLine(await response.Content.ReadAsStringAsync());
    else
        Console.WriteLine($"Sent from {upn} to {recipient} with Message-ID {outgoing.MessageId}");
    return;
}

if (args.Contains("ingestbench", StringComparer.OrdinalIgnoreCase))
{
    // Measures the write side alone: parse + compress + hash + insert, using
    // messages already stored (so no network is involved).
    using var src = MailboxDatabase.Open(mailboxDbPath, dek);
    var raws = new List<byte[]>();
    using (var pick = src.CreateCommand())
    {
        pick.CommandText = "SELECT id FROM messages ORDER BY id DESC LIMIT 200;";
        var ids = new List<long>();
        using (var r = pick.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt64(0));
        foreach (var id in ids)
        {
            try { raws.Add(MailboxStore.GetRawMessage(src, id)); } catch { }
        }
    }
    Console.WriteLine($"Loaded {raws.Count} sample messages " +
        $"({raws.Sum(r => (long)r.Length) / 1024.0 / 1024.0:N1} MB raw)");

    var scratchPath = Path.Combine(root, "ingestbench.db");
    if (File.Exists(scratchPath)) File.Delete(scratchPath);
    using var dst = MailboxDatabase.Open(scratchPath, dek);
    using (var mk = dst.CreateCommand())
    {
        mk.CommandText = "INSERT INTO folders(id, name, special_use) VALUES(1,'Bench','inbox');";
        mk.ExecuteNonQuery();
    }

    var swParse = System.Diagnostics.Stopwatch.StartNew();
    var parsed = raws.Select(r => Mail.Core.Ingest.MimeMessageParser.Parse(r)).ToList();
    swParse.Stop();
    Console.WriteLine($"parse only      : {raws.Count / swParse.Elapsed.TotalSeconds,8:N1} msg/s");

    var swIngest = System.Diagnostics.Stopwatch.StartNew();
    for (var i = 0; i < raws.Count; i++)
        MailboxStore.IngestMessage(dst, 1, raws[i], parsed[i], DateTimeOffset.Now, $"bench-{i}");
    swIngest.Stop();
    Console.WriteLine($"ingest (writer) : {raws.Count / swIngest.Elapsed.TotalSeconds,8:N1} msg/s   <-- writer ceiling");
    Console.WriteLine($"                  {swIngest.Elapsed.TotalMilliseconds / raws.Count,8:N1} ms per message");
    dst.Dispose();
    if (File.Exists(scratchPath)) File.Delete(scratchPath);
    return;
}

if (args.Contains("sendtyped", StringComparer.OrdinalIgnoreCase))
{
    // Control test: send via the typed Graph API (service builds the MIME).
    // If this arrives and raw MIME does not, the problem is our MIME, not delivery.
    var recip = args.SkipWhile(a => !a.Equals("sendtyped", StringComparison.OrdinalIgnoreCase))
        .Skip(1).FirstOrDefault() ?? "";
    var g2 = new GraphServiceClient(
        new BaseBearerTokenAuthenticationProvider(new GraphTokenProvider(auth, signIn)));
    var stamp2 = DateTime.Now.ToString("HH:mm:ss");
    await g2.Me.SendMail.PostAsync(new Microsoft.Graph.Me.SendMail.SendMailPostRequestBody
    {
        Message = new Microsoft.Graph.Models.Message
        {
            Subject = $"eeeMail typed test {stamp2}",
            Body = new Microsoft.Graph.Models.ItemBody
            {
                ContentType = Microsoft.Graph.Models.BodyType.Text,
                Content = $"Typed-API control message sent at {stamp2}.",
            },
            ToRecipients =
            [
                new Microsoft.Graph.Models.Recipient
                {
                    EmailAddress = new Microsoft.Graph.Models.EmailAddress { Address = recip },
                },
            ],
        },
        SaveToSentItems = true,
    });
    Console.WriteLine($"Typed send accepted: {upn} -> {recip} (subject: eeeMail typed test {stamp2})");
    return;
}

if (args.Contains("bench", StringComparer.OrdinalIgnoreCase))
{
    await RunBenchmarkAsync(auth, signIn);
    return;
}

if (args.Length > 0 && int.TryParse(args[0], out _))
{
    using (var update = appDb.CreateCommand())
    {
        update.CommandText =
            "UPDATE mailboxes SET sync_policy = @p, sync_window_months = @m WHERE upn = @u;";
        update.Parameters.AddWithValue("@p", months == 0 ? "MirrorServer" : "WindowedCache");
        update.Parameters.AddWithValue("@m", months == 0 ? DBNull.Value : months);
        update.Parameters.AddWithValue("@u", upn);
        update.ExecuteNonQuery();
    }
    using (var reset = mailboxDb.CreateCommand())
    {
        // Cleared checkpoints force a fresh initial pass with the new window;
        // already-stored messages are recognized by server id, not re-downloaded.
        reset.CommandText = "DELETE FROM sync_state;";
        reset.ExecuteNonQuery();
    }
    Console.WriteLine(months == 0
        ? "Policy set: MirrorServer (everything). Checkpoints reset."
        : $"Policy set: WindowedCache, last {months} month(s). Checkpoints reset.");
}
if (configOnly)
{
    Console.WriteLine("Config-only run: no sync performed. Launch the app (or rerun without 'config') to download.");
    return;
}

// --- sync --------------------------------------------------------------------
var graph = new GraphServiceClient(
    new BaseBearerTokenAuthenticationProvider(new GraphTokenProvider(auth, signIn)));
var since = months > 0 ? DateTimeOffset.UtcNow.AddMonths(-months) : (DateTimeOffset?)null;
Console.WriteLine(months > 0
    ? $"Syncing (initial window: last {months} month(s), incremental afterwards)…"
    : "Syncing (full mirror — everything, incremental afterwards)…");
var stopwatch = Stopwatch.StartNew();
var sync = new GraphMailboxSync(graph, () => MailboxDatabase.Open(mailboxDbPath, dek), p =>
{
    if (p.Phase == GraphMailboxSync.SyncPhase.FolderDone && p.FolderDownloaded > 0)
        Console.WriteLine($"  {p.FolderName}: +{p.FolderDownloaded:N0}" +
            $" (overall {p.OverallDownloaded:N0}/{p.OverallTarget?.ToString("N0") ?? "?"})");
});
var stats = await sync.SyncAsync(since);
stopwatch.Stop();

// --- results -----------------------------------------------------------------
Console.WriteLine();
Console.WriteLine($"Done in {stopwatch.Elapsed.TotalSeconds:F1}s — " +
    $"{stats.Folders} folders, +{stats.Added} added, ~{stats.Updated} updated, " +
    $"-{stats.Removed} removed" + (stats.Failed > 0 ? $", {stats.Failed} failed" : ""));
Console.WriteLine($"Messages in DB : {Scalar(mailboxDb, "SELECT count(*) FROM messages;")}");
Console.WriteLine($"Unique blobs   : {Scalar(mailboxDb, "SELECT count(*) FROM blobs;")}");
Console.WriteLine($"Raw bytes      : {Scalar(mailboxDb, "SELECT coalesce(sum(raw_size),0) FROM bodies;"),13:N0}");
Console.WriteLine($"Stored bytes   : {Scalar(mailboxDb, "SELECT coalesce(sum(length(content)),0) FROM blobs;"),13:N0}");
Console.WriteLine($"DB file        : {new FileInfo(mailboxDbPath).Length,13:N0} bytes");
Console.WriteLine();
Console.WriteLine("Newest messages:");
using (var cmd = mailboxDb.CreateCommand())
{
    cmd.CommandText = """
        SELECT datetime(received_at, 'unixepoch'), subject FROM messages
        ORDER BY received_at DESC LIMIT 5;
        """;
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
        Console.WriteLine($"  {reader.GetString(0)}  {(reader.IsDBNull(1) ? "(no subject)" : reader.GetString(1))}");
}

return;

// Pure network benchmark: downloads the same message set at several concurrency
// levels, discarding bytes. Touches no DB or delta state. Repeats C=1 at the end
// to expose server-side drift during the run.
static async Task RunBenchmarkAsync(GraphAuthenticator auth, AuthenticationResult signIn)
{
    var graph = new GraphServiceClient(
        new BaseBearerTokenAuthenticationProvider(new GraphTokenProvider(auth, signIn)));
    Console.WriteLine("Listing sample messages from Inbox…");
    var ids = new List<string>();
    var page = await graph.Me.MailFolders["inbox"].Messages.GetAsync(rc =>
    {
        rc.QueryParameters.Top = 120;
        rc.QueryParameters.Select = ["id"];
        rc.QueryParameters.Orderby = ["receivedDateTime desc"];
    });
    while (page is not null && ids.Count < 120)
    {
        foreach (var m in page.Value ?? [])
            if (m.Id is not null)
                ids.Add(m.Id);
        if (page.OdataNextLink is null || ids.Count >= 120) break;
        page = await graph.Me.MailFolders["inbox"].Messages
            .WithUrl(page.OdataNextLink).GetAsync();
    }
    ids = [.. ids.Take(120)];
    Console.WriteLine($"Benchmarking {ids.Count} $value downloads per concurrency level…");
    Console.WriteLine();
    foreach (var concurrency in new[] { 1, 2, 3, 4, 6, 1 })
    {
        await Task.Delay(3000);
        long bytes = 0;
        var errors = 0;
        var semaphore = new SemaphoreSlim(concurrency);
        var stopwatch = Stopwatch.StartNew();
        await Task.WhenAll(ids.Select(async id =>
        {
            await semaphore.WaitAsync();
            try
            {
                await using var stream = await graph.Me.Messages[id].Content.GetAsync();
                if (stream is not null)
                {
                    using var buffer = new MemoryStream();
                    await stream.CopyToAsync(buffer);
                    Interlocked.Add(ref bytes, buffer.Length);
                }
            }
            catch
            {
                Interlocked.Increment(ref errors);
            }
            finally
            {
                semaphore.Release();
            }
        }));
        stopwatch.Stop();
        var seconds = stopwatch.Elapsed.TotalSeconds;
        Console.WriteLine(
            $"  C={concurrency}: {ids.Count / seconds,6:N2} msg/s  {bytes / seconds / 1_000_000,7:N2} MB/s  " +
            $"{seconds,6:N1}s total  errors={errors}");
    }
}

static object? Scalar(SqliteConnection conn, string sql)
{
    using var cmd = conn.CreateCommand();
    cmd.CommandText = sql;
    return cmd.ExecuteScalar();
}

static byte[] EnsureMailboxRegistered(SqliteConnection appDb, string upn, out string dbPath)
{
    using (var find = appDb.CreateCommand())
    {
        find.CommandText = "SELECT dek, db_path FROM mailboxes WHERE upn = @u;";
        find.Parameters.AddWithValue("@u", upn);
        using var reader = find.ExecuteReader();
        if (reader.Read())
        {
            dbPath = reader.GetString(1);
            return (byte[])reader.GetValue(0);
        }
    }
    var dek = RandomNumberGenerator.GetBytes(32);
    dbPath = Path.Combine(".scratch", "mailboxes", upn + ".db");
    Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(dbPath))!);
    using var cmd = appDb.CreateCommand();
    cmd.CommandText = """
        INSERT OR IGNORE INTO identities(id, name) VALUES(1, 'Default');
        INSERT INTO accounts(identity_id, kind, display_name, upn) VALUES(1, 'graph', @u, @u);
        INSERT INTO mailboxes(account_id, upn, display_name, kind, db_path, dek, sync_policy, sync_window_months)
        VALUES(last_insert_rowid(), @u, @u, 'primary', @p, @k, 'WindowedCache', 1);
        """;
    cmd.Parameters.AddWithValue("@u", upn);
    cmd.Parameters.AddWithValue("@p", dbPath);
    cmd.Parameters.AddWithValue("@k", dek);
    cmd.ExecuteNonQuery();
    return dek;
}
