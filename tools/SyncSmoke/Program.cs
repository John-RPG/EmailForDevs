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
// A dev tool, so it keeps its store in the checkout rather than in the user's
// application data — but through the same resolver the app uses, so both agree
// on one location. EEEMAIL_DATA_DIR overrides it for both.
var root = Mail.Core.Storage.DataLocation.ResolveWithoutCreating(AppContext.BaseDirectory);
if (Environment.GetEnvironmentVariable(Mail.Core.Storage.DataLocation.EnvironmentVariable) is null)
    root = Path.Combine(Directory.GetCurrentDirectory(), ".scratch");
Directory.CreateDirectory(root);
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

if (args.Length > 0 && args[0].Equals("sharedstate", StringComparison.OrdinalIgnoreCase))
{
    foreach (var entry in MailboxRegistry.List(appDb).Where(m => m.Kind == "shared"))
    {
        var path = Path.IsPathRooted(entry.DbPath) ? entry.DbPath : Path.GetFullPath(entry.DbPath);
        Console.WriteLine($"=== {entry.Upn} (under {entry.AccountUpn}) ===");
        if (!File.Exists(path)) { Console.WriteLine("  no database file yet"); continue; }
        Console.WriteLine($"  file: {new FileInfo(path).Length:N0} bytes");
        try
        {
            using var db = MailboxDatabase.Open(path, entry.Dek);
            using var f = db.CreateCommand();
            f.CommandText = "SELECT count(*) FROM folders;";
            Console.WriteLine($"  folders : {f.ExecuteScalar()}");
            using var msgs = db.CreateCommand();
            msgs.CommandText = "SELECT count(*) FROM messages;";
            Console.WriteLine($"  messages: {msgs.ExecuteScalar()}");
            using var list = db.CreateCommand();
            list.CommandText = """
                SELECT f.name, f.total_count,
                       (SELECT count(*) FROM messages WHERE folder_id = f.id)
                FROM folders f ORDER BY f.name LIMIT 15;
                """;
            using var reader = list.ExecuteReader();
            while (reader.Read())
                Console.WriteLine($"      {reader.GetString(0),-28} server={reader.GetInt64(1),7:N0} local={reader.GetInt64(2),7:N0}");
        }
        catch (Exception ex) { Console.WriteLine($"  open failed: {ex.Message}"); }
    }
    return;
}

if (args.Length > 0 && args[0].Equals("idprobe", StringComparison.OrdinalIgnoreCase))
{
    // Which addressing forms does Graph accept for a shared mailbox? /users/me
    // is not one of them — /me is a separate endpoint — and a shared mailbox's
    // SMTP address may differ from its userPrincipalName.
    var ipAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    var ipAcct = (await ipAuth.GetAccountsAsync())
        .FirstOrDefault(a => a.Username.Contains("ExampleCorp", StringComparison.OrdinalIgnoreCase));
    if (ipAcct is null) { Console.WriteLine("work account not signed in"); return; }
    var ipTok = await ipAuth.AcquireSilentAsync(ipAcct);
    if (ipTok is null) { Console.WriteLine("no token"); return; }
    var g = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(
        new GraphTokenProvider(ipAuth, ipTok)));

    async Task Try(string label, Func<Task> call)
    {
        try { await call(); Console.WriteLine($"  {label,-46} OK"); }
        catch (ODataError ex) { Console.WriteLine($"  {label,-46} {ex.ResponseStatusCode} {ex.Error?.Code}: {ex.Error?.Message}"); }
        catch (Exception ex) { Console.WriteLine($"  {label,-46} {ex.GetType().Name}"); }
    }

    Console.WriteLine("Own mailbox:");
    await Try("graph.Me.MailFolders", async () => await g.Me.MailFolders.GetAsync(rc => rc.QueryParameters.Top = 1));
    await Try("graph.Users[\"me\"].MailFolders", async () => await g.Users["me"].MailFolders.GetAsync(rc => rc.QueryParameters.Top = 1));
    await Try($"graph.Users[\"{ipAcct.Username}\"].MailFolders", async () => await g.Users[ipAcct.Username].MailFolders.GetAsync(rc => rc.QueryParameters.Top = 1));

    Console.WriteLine("Shared mailbox (shared@example.com):");
    await Try("graph.Users[smtp].MailFolders", async () => await g.Users["shared@example.com"].MailFolders.GetAsync(rc => rc.QueryParameters.Top = 1));
    await Try("graph.Users[smtp].MailFolders[inbox]", async () => await g.Users["shared@example.com"].MailFolders["inbox"].GetAsync());
    await Try("graph.Users[smtp].Messages", async () => await g.Users["shared@example.com"].Messages.GetAsync(rc => rc.QueryParameters.Top = 1));
    return;
}

if (args.Length > 0 && args[0].Equals("order", StringComparison.OrdinalIgnoreCase))
{
    var rest = args.Skip(1).ToList();
    if (rest.Count == 2 && int.TryParse(rest[1], out var delta))
    {
        var ordered = MailboxRegistry.ListAccounts(appDb);
        var move = ordered.FirstOrDefault(a =>
            a.Upn.Contains(rest[0], StringComparison.OrdinalIgnoreCase));
        if (move.Id == 0) { Console.WriteLine($"no account matching '{rest[0]}'"); return; }
        var ok = MailboxRegistry.MoveAccount(appDb, move.Id, delta);
        Console.WriteLine(ok ? $"moved {move.Upn} by {delta}" : "at the end already");
    }
    Console.WriteLine("Account order:");
    foreach (var a in MailboxRegistry.ListAccounts(appDb))
        Console.WriteLine($"  {a.Id,3}  {a.Upn}");
    return;
}

if (args.Length > 0 && args[0].Equals("findattach", StringComparison.OrdinalIgnoreCase))
{
    foreach (var entry in MailboxRegistry.List(appDb, enabledOnly: true))
    {
        var path = Path.IsPathRooted(entry.DbPath) ? entry.DbPath : Path.GetFullPath(entry.DbPath);
        if (!File.Exists(path)) continue;
        using var db = MailboxDatabase.Open(path, entry.Dek);
        using var cmd = db.CreateCommand();
        cmd.CommandText = """
            SELECT m.id, m.subject, f.name,
                   sum(CASE WHEN a.content_id IS NULL THEN 1 ELSE 0 END) AS files,
                   sum(CASE WHEN a.content_id IS NOT NULL THEN 1 ELSE 0 END) AS inline
            FROM attachments a
            JOIN messages m ON m.id = a.message_id
            JOIN folders f ON f.id = m.folder_id
            GROUP BY m.id
            HAVING files > 0 AND inline > 0
            ORDER BY inline DESC LIMIT 5;
            """;
        using var reader = cmd.ExecuteReader();
        var any = false;
        while (reader.Read())
        {
            if (!any) { Console.WriteLine($"=== {entry.Upn} ==="); any = true; }
            Console.WriteLine($"  msg {reader.GetInt64(0)} [{reader.GetString(2)}] files={reader.GetInt64(3)} inline={reader.GetInt64(4)}");
            Console.WriteLine($"      {reader.GetString(1)}");
        }
    }
    return;
}

if (args.Length > 0 && args[0].Equals("sizeprobe", StringComparison.OrdinalIgnoreCase))
{
    // Folder byte size and mailbox quota are not in Graph — they live in
    // Exchange. EWS exposes folder size as an extended property
    // (PR_MESSAGE_SIZE_EXTENDED, 0x0E08) and quota via GetUserConfiguration or
    // the mailbox's ServerVersionInfo. Check what we can actually read with the
    // EWS token we already hold.
    var szAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    using var szHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    var who = args.Length > 1 ? args[1] : "user@example.com";

    var acct = (await szAuth.GetAccountsAsync())
        .FirstOrDefault(a => a.Username.Equals(who, StringComparison.OrdinalIgnoreCase));
    if (acct is null) { Console.WriteLine($"{who} not signed in"); return; }
    var tok = await szAuth.AcquireSilentAsync(acct, GraphAuthenticator.ExchangeScopes);
    if (tok is null) { Console.WriteLine("no EWS token"); return; }

    const string ews = "https://outlook.office365.com/EWS/Exchange.asmx";

    async Task<string?> Post(string body)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, ews);
        req.Headers.Authorization = new("Bearer", tok.AccessToken);
        req.Content = new StringContent(body, System.Text.Encoding.UTF8, "text/xml");
        var resp = await szHttp.SendAsync(req);
        var text = await resp.Content.ReadAsStringAsync();
        Console.WriteLine($"  HTTP {(int)resp.StatusCode}");
        return resp.IsSuccessStatusCode ? text : text[..Math.Min(400, text.Length)];
    }

    // 1. Folder sizes via the extended property.
    Console.WriteLine("--- folder sizes (PR_MESSAGE_SIZE_EXTENDED) ---");
    var folders = await Post("""
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
                       xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
                       xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages">
          <soap:Header><t:RequestServerVersion Version="Exchange2013"/></soap:Header>
          <soap:Body>
            <m:FindFolder Traversal="Deep">
              <m:FolderShape>
                <t:BaseShape>IdOnly</t:BaseShape>
                <t:AdditionalProperties>
                  <t:FieldURI FieldURI="folder:DisplayName"/>
                  <t:FieldURI FieldURI="folder:TotalCount"/>
                  <t:ExtendedFieldURI PropertyTag="0x0E08" PropertyType="Long"/>
                </t:AdditionalProperties>
              </m:FolderShape>
              <m:ParentFolderIds><t:DistinguishedFolderId Id="msgfolderroot"/></m:ParentFolderIds>
            </m:FindFolder>
          </soap:Body>
        </soap:Envelope>
        """);

    if (folders is not null)
    {
        var names = System.Text.RegularExpressions.Regex.Matches(folders, @"<t:DisplayName>(.*?)</t:DisplayName>");
        var sizes = System.Text.RegularExpressions.Regex.Matches(folders, @"<t:Value>(\d+)</t:Value>");
        Console.WriteLine($"  folders returned: {names.Count}, size values: {sizes.Count}");
        for (var i = 0; i < Math.Min(10, names.Count); i++)
        {
            var size = i < sizes.Count ? long.Parse(sizes[i].Groups[1].Value) : -1;
            Console.WriteLine($"      {names[i].Groups[1].Value,-28} {(size < 0 ? "?" : $"{size / 1024.0 / 1024.0:N1} MB")}");
        }
    }

    // 2. Mailbox quota.
    Console.WriteLine("--- mailbox quota (GetMailTips / ServerVersionInfo) ---");
    var quota = await Post($"""
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
                       xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
                       xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages">
          <soap:Header><t:RequestServerVersion Version="Exchange2013"/></soap:Header>
          <soap:Body>
            <m:GetMailTips>
              <m:SendingAs><t:EmailAddress>{who}</t:EmailAddress></m:SendingAs>
              <m:Recipients><t:Mailbox><t:EmailAddress>{who}</t:EmailAddress></t:Mailbox></m:Recipients>
              <m:MailTipsRequested>MailboxFullStatus</m:MailTipsRequested>
            </m:GetMailTips>
          </soap:Body>
        </soap:Envelope>
        """);
    if (quota is not null)
    {
        var full = System.Text.RegularExpressions.Regex.Match(quota, @"<t:MailboxFull>(.*?)</t:MailboxFull>");
        Console.WriteLine($"  mailbox full: {(full.Success ? full.Groups[1].Value : "(not reported)")}");
    }

    // 3. Quota through the root folder's extended properties, which is where
    //    Outlook reads it: 0x341C is the quota, 0x0E08 the size in use.
    Console.WriteLine("--- quota via root folder extended properties ---");
    var root2 = await Post("""
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
                       xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
                       xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages">
          <soap:Header><t:RequestServerVersion Version="Exchange2013"/></soap:Header>
          <soap:Body>
            <m:GetFolder>
              <m:FolderShape>
                <t:BaseShape>IdOnly</t:BaseShape>
                <t:AdditionalProperties>
                  <t:ExtendedFieldURI PropertyTag="0x0E08" PropertyType="Long"/>
                  <t:ExtendedFieldURI PropertyTag="0x341C" PropertyType="Integer"/>
                  <t:ExtendedFieldURI PropertyTag="0x6639" PropertyType="Integer"/>
                </t:AdditionalProperties>
              </m:FolderShape>
              <m:FolderIds><t:DistinguishedFolderId Id="root"/></m:FolderIds>
            </m:GetFolder>
          </soap:Body>
        </soap:Envelope>
        """);
    if (root2 is not null)
    {
        foreach (System.Text.RegularExpressions.Match m2 in System.Text.RegularExpressions.Regex.Matches(
                     root2, @"PropertyTag=""(0x[0-9A-Fa-f]+)""[^>]*/>\s*<t:Value>(\d+)</t:Value>"))
            Console.WriteLine($"      {m2.Groups[1].Value} = {m2.Groups[2].Value}");
        if (!root2.Contains("<t:Value>")) Console.WriteLine("      (no values returned)");
    }
    return;
}

if (args.Length > 0 && args[0].Equals("theme", StringComparison.OrdinalIgnoreCase))
{
    var store = new SettingsStore(appDb);
    if (args.Length > 1)
    {
        store.Set(SettingsCatalog.ThemeMode.Key, SettingTarget.Application, args[1]);
        Console.WriteLine($"theme set to {args[1]}");
    }
    Console.WriteLine($"theme is now: {store.GetString(SettingsCatalog.ThemeMode, SettingTarget.Application)}");
    return;
}

if (args.Length > 0 && args[0].Equals("set", StringComparison.OrdinalIgnoreCase))
{
    // set <key> [value] — application scope. Reads back when no value is given.
    // Generic so verifying a new setting does not need a new command each time.
    if (args.Length < 2) { Console.WriteLine("usage: set <key> [value]"); return; }
    var store = new SettingsStore(appDb);
    var def = SettingsCatalog.All.FirstOrDefault(d => d.Key.Equals(args[1], StringComparison.OrdinalIgnoreCase));
    if (def is null)
    {
        Console.WriteLine($"unknown key '{args[1]}'. Known keys:");
        foreach (var d in SettingsCatalog.All.OrderBy(d => d.Key)) Console.WriteLine($"  {d.Key}");
        return;
    }
    if (args.Length > 2)
    {
        if (!def.IsValid(args[2], out var why)) { Console.WriteLine($"rejected: {why}"); return; }
        store.Set(def.Key, SettingTarget.Application, args[2]);
    }
    Console.WriteLine($"{def.Key} = {store.GetString(def, SettingTarget.Application)}");
    return;
}

if (args.Length > 0 && args[0].Equals("htmlprobe", StringComparison.OrdinalIgnoreCase))
{
    // How do real messages declare their background? That decides whether a
    // zero-specificity default can ever apply, or whether restyling has to
    // override what the sender set.
    var entry = MailboxRegistry.List(appDb, enabledOnly: true)
        .First(m => m.Upn.Contains("ExampleCorp", StringComparison.OrdinalIgnoreCase));
    var path = Path.IsPathRooted(entry.DbPath) ? entry.DbPath : Path.GetFullPath(entry.DbPath);
    using var db = MailboxDatabase.Open(path, entry.Dek);

    using var cmd = db.CreateCommand();
    cmd.CommandText = "SELECT id FROM messages ORDER BY received_at DESC LIMIT 40;";
    var ids = new List<long>();
    using (var r = cmd.ExecuteReader()) while (r.Read()) ids.Add(r.GetInt64(0));

    int bodyBg = 0, tableBg = 0, inlineStyle = 0, styleBlock = 0, none = 0, examined = 0;
    foreach (var id in ids)
    {
        var raw = MailboxStore.GetRawMessage(db, id);
        if (raw is null) continue;
        var mime = MimeKit.MimeMessage.Load(new MemoryStream(raw));
        var html = mime.HtmlBody;
        if (html is null) continue;
        examined++;
        var lower = html.ToLowerInvariant();
        var hasBodyBg = System.Text.RegularExpressions.Regex.IsMatch(lower,
            @"<body[^>]*(bgcolor|background-color|background\s*:)");
        var hasTableBg = System.Text.RegularExpressions.Regex.IsMatch(lower,
            @"<table[^>]*(bgcolor|background-color)");
        if (hasBodyBg) bodyBg++;
        if (hasTableBg) tableBg++;
        if (lower.Contains("style=\"")) inlineStyle++;
        if (lower.Contains("<style")) styleBlock++;
        if (!hasBodyBg && !hasTableBg) none++;
    }
    Console.WriteLine($"examined {examined} HTML message(s):");
    Console.WriteLine($"  <body> sets a background : {bodyBg}");
    Console.WriteLine($"  <table> sets a background: {tableBg}");
    Console.WriteLine($"  uses inline style=       : {inlineStyle}");
    Console.WriteLine($"  has a <style> block      : {styleBlock}");
    Console.WriteLine($"  declares no background   : {none}");
    return;
}

if (args.Length > 0 && args[0].Equals("sigprobe", StringComparison.OrdinalIgnoreCase))
{
    // Graph has no signature API. EWS GetUserConfiguration used to hold OWA
    // signatures, but roaming signatures moved them elsewhere. Check what is
    // actually reachable with the Exchange token we hold, rather than assuming
    // either way.
    var sigAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    using var sigHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    var who = args.Length > 1 ? args[1] : "user@example.com";

    var acct = (await sigAuth.GetAccountsAsync())
        .FirstOrDefault(a => a.Username.Equals(who, StringComparison.OrdinalIgnoreCase));
    if (acct is null) { Console.WriteLine($"{who} not signed in"); return; }
    var tok = await sigAuth.AcquireSilentAsync(acct, GraphAuthenticator.ExchangeScopes);
    if (tok is null) { Console.WriteLine("no EWS token"); return; }

    const string ews = "https://outlook.office365.com/EWS/Exchange.asmx";
    async Task Try(string label, string configName)
    {
        var envelope = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
                           xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
                           xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages">
              <soap:Header><t:RequestServerVersion Version="Exchange2013"/></soap:Header>
              <soap:Body>
                <m:GetUserConfiguration>
                  <m:UserConfigurationName Name="{configName}">
                    <t:DistinguishedFolderId Id="root"/>
                  </m:UserConfigurationName>
                  <m:UserConfigurationProperties>All</m:UserConfigurationProperties>
                </m:GetUserConfiguration>
              </soap:Body>
            </soap:Envelope>
            """;
        using var req = new HttpRequestMessage(HttpMethod.Post, ews);
        req.Headers.Authorization = new("Bearer", tok.AccessToken);
        req.Content = new StringContent(envelope, System.Text.Encoding.UTF8, "text/xml");
        var resp = await sigHttp.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        var code = System.Text.RegularExpressions.Regex.Match(body, @"<m:ResponseCode>(.*?)</m:ResponseCode>");
        var hasData = body.Contains("<t:XmlData>") || body.Contains("<t:BinaryData>");
        Console.WriteLine($"  {label,-34} HTTP {(int)resp.StatusCode}  {(code.Success ? code.Groups[1].Value : "?")}" +
                          (hasData ? "  [has data]" : ""));
        if (hasData)
        {
            // Show what the config actually holds — a signature would appear as
            // HTML or as a named dictionary entry.
            foreach (System.Text.RegularExpressions.Match entry in
                     System.Text.RegularExpressions.Regex.Matches(body,
                         @"<t:DictionaryEntry>.*?<t:Value>(.*?)</t:Value>",
                         System.Text.RegularExpressions.RegexOptions.Singleline))
            {
                var v = entry.Groups[1].Value;
                if (v.Contains("signature", StringComparison.OrdinalIgnoreCase) ||
                    v.Contains("<", StringComparison.Ordinal))
                    Console.WriteLine($"      {v[..Math.Min(120, v.Length)]}");
            }
            var keys = System.Text.RegularExpressions.Regex.Matches(body, @"<t:String>(\w*[Ss]ignature\w*)</t:String>");
            foreach (System.Text.RegularExpressions.Match k in keys)
                Console.WriteLine($"      key: {k.Groups[1].Value}");
            if (keys.Count == 0) Console.WriteLine("      (no signature-named keys)");
        }
    }

    Console.WriteLine($"=== {who} ===");
    await Try("OWA.UserOptions", "OWA.UserOptions");
    await Try("Signatures", "Signatures");
    await Try("Roaming signatures", "OWA.AutoSignature");
    return;
}

if (args.Length > 0 && args[0].Equals("roamprobe", StringComparison.OrdinalIgnoreCase))
{
    // Does the EWS route reach the user's REAL signatures, or only the single
    // legacy OWA one? Two separate questions, tested separately:
    //   - what OWA.UserOptions actually contains, in full
    //   - whether the roaming signature items are reachable at all
    // Roaming signatures are documented as ordinary message items in hidden
    // subfolders of ApplicationDataRoot, not as FAI configuration, so
    // GetUserConfiguration is the wrong verb for them by construction.
    var rAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    using var rHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };
    var rWho = args.Length > 1 ? args[1] : "user@example.com";

    var rAll = (await rAuth.GetAccountsAsync()).ToList();
    var rAcct = rAll.FirstOrDefault(a => a.Username.Equals(rWho, StringComparison.OrdinalIgnoreCase));
    if (rAcct is null)
    {
        Console.WriteLine($"{rWho} not signed in. Signed-in accounts:");
        foreach (var a in rAll) Console.WriteLine($"    {a.Username}");
        return;
    }
    var rTok = await rAuth.AcquireSilentAsync(rAcct, GraphAuthenticator.ExchangeScopes);
    if (rTok is null) { Console.WriteLine("no EWS token"); return; }

    const string rEws = "https://outlook.office365.com/EWS/Exchange.asmx";
    async Task<string> Soap(string body)
    {
        var envelope = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
                           xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
                           xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages">
              <soap:Header><t:RequestServerVersion Version="Exchange2013_SP1"/></soap:Header>
              <soap:Body>{body}</soap:Body>
            </soap:Envelope>
            """;
        using var req = new HttpRequestMessage(HttpMethod.Post, rEws);
        req.Headers.Authorization = new("Bearer", rTok.AccessToken);
        req.Content = new StringContent(envelope, System.Text.Encoding.UTF8, "text/xml");
        var resp = await rHttp.SendAsync(req);
        return await resp.Content.ReadAsStringAsync();
    }

    Console.WriteLine($"=== {rWho} ===");

    // ---- 1. the legacy signature, in full --------------------------------
    Console.WriteLine();
    Console.WriteLine("[1] OWA.UserOptions contents");
    var opts = await Soap("""
        <m:GetUserConfiguration>
          <m:UserConfigurationName Name="OWA.UserOptions">
            <t:DistinguishedFolderId Id="root"/>
          </m:UserConfigurationName>
          <m:UserConfigurationProperties>All</m:UserConfigurationProperties>
        </m:GetUserConfiguration>
        """);
    foreach (System.Text.RegularExpressions.Match e in
             System.Text.RegularExpressions.Regex.Matches(opts,
                 @"<t:DictionaryEntry>\s*<t:DictionaryKey>.*?<t:Value>(.*?)</t:Value>.*?</t:DictionaryKey>\s*<t:DictionaryValue>.*?<t:Value>(.*?)</t:Value>",
                 System.Text.RegularExpressions.RegexOptions.Singleline))
    {
        var key = e.Groups[1].Value;
        var val = e.Groups[2].Value;
        if (!key.Contains("ignature", StringComparison.Ordinal)) continue;
        var flat = System.Net.WebUtility.HtmlDecode(val).ReplaceLineEndings(" ");
        Console.WriteLine($"    {key} = {(flat.Length > 300 ? flat[..300] + " …" : flat)}");
        Console.WriteLine($"      ({flat.Length} chars)");
    }

    // ---- 2. what lives under ApplicationDataRoot -------------------------
    // Roaming signatures are stored per-signature in subfolders here. If they
    // are reachable at all, the folders show up in this traversal.
    Console.WriteLine();
    Console.WriteLine("[2] hidden folders under the mailbox root");
    var folders = await Soap("""
        <m:FindFolder Traversal="Deep">
          <m:FolderShape>
            <t:BaseShape>Default</t:BaseShape>
            <t:AdditionalProperties>
              <t:FieldURI FieldURI="folder:FolderClass"/>
            </t:AdditionalProperties>
          </m:FolderShape>
          <m:ParentFolderIds><t:DistinguishedFolderId Id="root"/></m:ParentFolderIds>
        </m:FindFolder>
        """);
    var names = System.Text.RegularExpressions.Regex.Matches(folders, @"<t:DisplayName>(.*?)</t:DisplayName>");
    var interesting = 0;
    foreach (System.Text.RegularExpressions.Match n in names)
    {
        var name = n.Groups[1].Value;
        if (name.Contains("ignature", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("ApplicationDataRoot", StringComparison.OrdinalIgnoreCase) ||
            name.Contains("49499048", StringComparison.Ordinal))
        {
            Console.WriteLine($"    {name}");
            interesting++;
        }
    }
    Console.WriteLine($"    ({names.Count} folders total, {interesting} signature-related)");
    var fault = System.Text.RegularExpressions.Regex.Match(folders, @"<(?:m|s):ResponseCode>(.*?)</");
    if (fault.Success && fault.Groups[1].Value != "NoError")
        Console.WriteLine($"    FindFolder responded: {fault.Groups[1].Value}");

    // ---- 3b. read what is inside roaming_signature_list ------------------
    // The folder turned up in the traversal, so ask for its items. If the
    // names and bodies come back, roaming signatures ARE reachable over EWS
    // and every claim that they are not is simply wrong.
    Console.WriteLine();
    Console.WriteLine("[3b] items inside roaming_signature_list");
    var listFolder = await Soap("""
        <m:FindFolder Traversal="Deep">
          <m:FolderShape><t:BaseShape>IdOnly</t:BaseShape>
            <t:AdditionalProperties>
              <t:FieldURI FieldURI="folder:DisplayName"/>
              <t:FieldURI FieldURI="folder:TotalCount"/>
            </t:AdditionalProperties>
          </m:FolderShape>
          <m:Restriction>
            <t:IsEqualTo>
              <t:FieldURI FieldURI="folder:DisplayName"/>
              <t:FieldURIOrConstant><t:Constant Value="roaming_signature_list"/></t:FieldURIOrConstant>
            </t:IsEqualTo>
          </m:Restriction>
          <m:ParentFolderIds><t:DistinguishedFolderId Id="root"/></m:ParentFolderIds>
        </m:FindFolder>
        """);
    var fid = System.Text.RegularExpressions.Regex.Match(listFolder, @"<t:FolderId Id=""([^""]+)""");
    if (!fid.Success) { Console.WriteLine("    could not resolve the folder id"); return; }
    Console.WriteLine($"    folder id resolved ({fid.Groups[1].Value.Length} chars)");
    var total = System.Text.RegularExpressions.Regex.Match(listFolder, @"<t:TotalCount>(\d+)</t:TotalCount>");
    if (total.Success) Console.WriteLine($"    TotalCount: {total.Groups[1].Value}");

    // Associated items first: roaming signatures are commonly stored as FAI
    // within the folder rather than as ordinary messages.
    foreach (var traversal in new[] { "Associated", "Shallow" })
    {
        var items = await Soap($"""
            <m:FindItem Traversal="{traversal}">
              <m:ItemShape>
                <t:BaseShape>AllProperties</t:BaseShape>
              </m:ItemShape>
              <m:ParentFolderIds><t:FolderId Id="{fid.Groups[1].Value}"/></m:ParentFolderIds>
            </m:FindItem>
            """);
        var code = System.Text.RegularExpressions.Regex.Match(items, @"<(?:m|s):ResponseCode>(.*?)</");
        var subjects = System.Text.RegularExpressions.Regex.Matches(items, @"<t:Subject>(.*?)</t:Subject>");
        var classes = System.Text.RegularExpressions.Regex.Matches(items, @"<t:ItemClass>(.*?)</t:ItemClass>");
        Console.WriteLine($"    {traversal,-11} {(code.Success ? code.Groups[1].Value : "?")}  " +
                          $"{subjects.Count} item(s)");
        for (var i = 0; i < subjects.Count; i++)
            Console.WriteLine($"        \"{subjects[i].Groups[1].Value}\"" +
                              (i < classes.Count ? $"   [{classes[i].Groups[1].Value}]" : ""));
        if (subjects.Count == 0 && code.Success && code.Groups[1].Value != "NoError")
            Console.WriteLine($"        (nothing returned)");
    }

    // ---- 3. the documented roaming signature container -------------------
    Console.WriteLine();
    Console.WriteLine("[3] ApplicationDataRoot\\49499048-…  (documented roaming location)");
    var appData = await Soap("""
        <m:FindFolder Traversal="Deep">
          <m:FolderShape><t:BaseShape>IdOnly</t:BaseShape>
            <t:AdditionalProperties><t:FieldURI FieldURI="folder:DisplayName"/></t:AdditionalProperties>
          </m:FolderShape>
          <m:Restriction>
            <t:Contains ContainmentMode="Substring" ContainmentComparison="IgnoreCase">
              <t:FieldURI FieldURI="folder:DisplayName"/>
              <t:Constant Value="49499048"/>
            </t:Contains>
          </m:Restriction>
          <m:ParentFolderIds><t:DistinguishedFolderId Id="msgfolderroot"/></m:ParentFolderIds>
        </m:FindFolder>
        """);
    var hit = System.Text.RegularExpressions.Regex.Match(appData, @"<(?:m|s):ResponseCode>(.*?)</");
    Console.WriteLine($"    response: {(hit.Success ? hit.Groups[1].Value : "?")}");
    foreach (System.Text.RegularExpressions.Match n in
             System.Text.RegularExpressions.Regex.Matches(appData, @"<t:DisplayName>(.*?)</t:DisplayName>"))
        Console.WriteLine($"    found: {n.Groups[1].Value}");

    return;
}

if (args.Length > 0 && args[0].Equals("setsig", StringComparison.OrdinalIgnoreCase))
{
    var store = new SettingsStore(appDb);
    store.Set(SettingsCatalog.SignatureSource.Key, SettingTarget.Application, "local");
    var sigText = "John Hadlow" + "\n" + "eeeMail - a mail client for people who read headers";
    store.Set(SettingsCatalog.SignatureText.Key, SettingTarget.Application, sigText);
    Console.WriteLine("signature configured:");
    Console.WriteLine($"  source: {store.GetString(SettingsCatalog.SignatureSource, SettingTarget.Application)}");
    Console.WriteLine($"  text  : {store.GetString(SettingsCatalog.SignatureText, SettingTarget.Application).Replace("\n", " / ")}");
    return;
}

if (args.Length > 0 && args[0].Equals("paths", StringComparison.OrdinalIgnoreCase))
{
    // What the registry holds, and whether each file is actually there once
    // resolved the way the app resolves it.
    Console.WriteLine($"root (SyncSmoke)  : {root}");
    Console.WriteLine($"EEEMAIL_DATA_DIR  : {Environment.GetEnvironmentVariable("EEEMAIL_DATA_DIR") ?? "(unset)"}");
    Console.WriteLine($"app default       : {Mail.Core.Storage.DataLocation.ResolveWithoutCreating(AppContext.BaseDirectory)}");
    Console.WriteLine();

    using var cmd = appDb.CreateCommand();
    cmd.CommandText = "SELECT upn, db_path, kind FROM mailboxes ORDER BY id;";
    using var reader = cmd.ExecuteReader();
    while (reader.Read())
    {
        var mbUpn = reader.GetString(0);
        var stored = reader.IsDBNull(1) ? "(null)" : reader.GetString(1);
        var kind = reader.GetString(2);
        var rooted = Path.IsPathRooted(stored);
        var resolved = rooted ? stored : Path.Combine(root, stored);
        Console.WriteLine($"{mbUpn} [{kind}]");
        Console.WriteLine($"    stored   : {stored}");
        Console.WriteLine($"    rooted   : {rooted}");
        Console.WriteLine($"    resolved : {resolved}");
        Console.WriteLine($"    exists   : {File.Exists(resolved)}");
    }
    return;
}

if (args.Length > 0 && args[0].Equals("fixpaths", StringComparison.OrdinalIgnoreCase))
{
    // Rewrites stale relative db_path rows to absolute paths. Dry run unless
    // "apply" is passed, because this edits the registry every mailbox depends
    // on and a wrong guess would detach a mailbox from its data.
    var apply = args.Length > 1 && args[1].Equals("apply", StringComparison.OrdinalIgnoreCase);
    Console.WriteLine(apply ? "APPLYING" : "DRY RUN — pass 'apply' to write");
    Console.WriteLine();

    var updates = new List<(long Id, string Upn, string From, string To)>();
    using (var cmd = appDb.CreateCommand())
    {
        cmd.CommandText = "SELECT id, upn, db_path FROM mailboxes ORDER BY id;";
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var id = reader.GetInt64(0);
            var mbUpn = reader.GetString(1);
            var stored = reader.IsDBNull(2) ? "" : reader.GetString(2);
            if (stored.Length == 0 || Path.IsPathRooted(stored)) continue;

            // Where the file actually is. The stored path is relative to the
            // repository root, which is the data directory's parent when the
            // data directory is the .scratch folder inside it.
            var candidates = new[]
            {
                Path.GetFullPath(Path.Combine(root, stored)),
                Path.GetFullPath(Path.Combine(Directory.GetParent(root)?.FullName ?? root, stored)),
                Path.GetFullPath(Path.Combine(root, Path.GetFileName(stored))),
                Path.GetFullPath(Path.Combine(root, "mailboxes", Path.GetFileName(stored))),
            };
            var found = candidates.FirstOrDefault(File.Exists);
            Console.WriteLine($"{mbUpn}");
            Console.WriteLine($"    stored : {stored}");
            if (found is null)
            {
                Console.WriteLine("    -> no file found at any candidate path; left alone");
                continue;
            }
            Console.WriteLine($"    -> {found}");
            updates.Add((id, mbUpn, stored, found));
        }
    }

    if (!apply) { Console.WriteLine($"\n{updates.Count} row(s) would be rewritten."); return; }
    foreach (var u in updates)
    {
        using var cmd = appDb.CreateCommand();
        cmd.CommandText = "UPDATE mailboxes SET db_path = @p WHERE id = @i;";
        cmd.Parameters.AddWithValue("@p", u.To);
        cmd.Parameters.AddWithValue("@i", u.Id);
        cmd.ExecuteNonQuery();
        Console.WriteLine($"updated {u.Upn}");
    }
    Console.WriteLine($"\n{updates.Count} row(s) rewritten.");
    return;
}

if (args.Length > 0 && args[0].Equals("caps", StringComparison.OrdinalIgnoreCase))
{
    // Read-only view of granted vs pending, for checking the two are not
    // conflated after a migration.
    foreach (var entry in MailboxRegistry.List(appDb).Where(m => m.Kind == "primary"))
    {
        var granted = MailboxRegistry.GetCapabilities(appDb, entry.Upn);
        var pending = MailboxRegistry.GetPendingCapabilities(appDb, entry.Upn);
        Console.WriteLine($"=== {entry.Upn} ===");
        Console.WriteLine($"  granted: {(granted.Count == 0 ? "(none)" : string.Join(", ", granted.OrderBy(x => x)))}");
        Console.WriteLine($"  pending: {(pending.Count == 0 ? "(none)" : string.Join(", ", pending.OrderBy(x => x)))}");
        var both = granted.Intersect(pending, StringComparer.OrdinalIgnoreCase).ToList();
        if (both.Count > 0) Console.WriteLine($"  BOTH (bug): {string.Join(", ", both)}");
    }
    return;
}

if (args.Length > 0 && args[0].Equals("grantcap", StringComparison.OrdinalIgnoreCase))
{
    // Records a capability as granted, but only after confirming the scopes are
    // actually held — the whole point of the flag is that it reflects what the
    // tenant granted, not what was asked for.
    var rest = args.SkipWhile(a => !a.Equals("grantcap", StringComparison.OrdinalIgnoreCase)).Skip(1).ToList();
    if (rest.Count < 2) { Console.WriteLine("Usage: grantcap <account-upn> <capability-id>"); return; }
    var who = rest[0];
    var capId = rest[1];
    var capability = AccountCapability.ById(capId);
    if (capability is null) { Console.WriteLine($"unknown capability '{capId}'"); return; }

    var gAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    var gAcct = (await gAuth.GetAccountsAsync())
        .FirstOrDefault(a => a.Username.Equals(who, StringComparison.OrdinalIgnoreCase));
    if (gAcct is null) { Console.WriteLine($"{who} is not signed in"); return; }

    var exchange = capability.Scopes.Any(x => x.Contains("outlook.office365.com"));
    var scopes = exchange ? GraphAuthenticator.ExchangeScopes : [.. capability.Scopes];
    var held = await gAuth.AcquireSilentAsync(gAcct, scopes);
    if (held is null)
    {
        Console.WriteLine($"  {who}: the scopes for \"{capability.Name}\" are NOT held — not recording.");
        return;
    }

    MailboxRegistry.SetCapability(appDb, who, capId, true);
    Console.WriteLine($"  {who}: \"{capability.Name}\" recorded as granted.");
    Console.WriteLine($"    token holds: {string.Join(" ", held.Scopes.Select(x => x[(x.LastIndexOf('/') + 1)..]))}");
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

if (args.Contains("doorbell", StringComparer.OrdinalIgnoreCase))
{
    // End-to-end proof: hold the long poll open on the work account, send a
    // message to it from another account, and see whether the server tells us
    // before the window would have expired.
    var dbAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    using var dbHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
    var target = "user@example.com";

    var acct = (await dbAuth.GetAccountsAsync())
        .FirstOrDefault(a => a.Username.Equals(target, StringComparison.OrdinalIgnoreCase));
    if (acct is null) { Console.WriteLine("work account not signed in"); return; }
    var tok = await dbAuth.AcquireSilentAsync(acct, GraphAuthenticator.ExchangeScopes);
    if (tok is null) { Console.WriteLine("no Exchange token"); return; }

    var stream = new MailboxEventStream(dbHttp);
    var sub = await stream.SubscribeAsync(target, tok.AccessToken, signedInAs: target);
    if (sub is null) { Console.WriteLine("subscribe failed"); return; }
    Console.WriteLine($"Subscribed to {target}. Holding the connection open…");

    // Send from a personal account while the poll is held.
    var senderAcct = (await dbAuth.GetAccountsAsync())
        .FirstOrDefault(a => a.Username.Contains("hotmail", StringComparison.OrdinalIgnoreCase));
    if (senderAcct is not null)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            try
            {
                var sendTok = await dbAuth.AcquireSilentAsync(senderAcct);
                if (sendTok is null) { Console.WriteLine("  (sender has no token)"); return; }
                var g = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(
                    new GraphTokenProvider(dbAuth, sendTok)));
                var draft = new Mail.Core.Compose.Draft(
                    senderAcct.Username, [target], [], [],
                    $"eeeMail doorbell test {DateTime.Now:HH:mm:ss}",
                    "Sent to prove the live-update long poll fires.");
                var mime = Mail.Core.Compose.ReplyBuilder.ToMimeMessage(draft);
                using var ms = new MemoryStream();
                await mime.WriteToAsync(ms);
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    "https://graph.microsoft.com/v1.0/me/sendMail");
                req.Headers.Authorization = new("Bearer", sendTok.AccessToken);
                req.Content = new StringContent(
                    Convert.ToBase64String(ms.ToArray()),
                    System.Text.Encoding.UTF8, "text/plain");
                using var send = new HttpClient();
                var resp = await send.SendAsync(req);
                Console.WriteLine($"  [sender] sent from {senderAcct.Username}: HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex) { Console.WriteLine($"  [sender] failed: {ex.Message}"); }
        });
    }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await stream.WaitForEventsAsync(sub, tok.AccessToken, connectionMinutes: 5,
        onEvents: evts => Console.WriteLine(
            $"  *** {sw.Elapsed.TotalSeconds:N1}s: {string.Join(", ", evts)} (delivered live)"));
    sw.Stop();
    Console.WriteLine($"Returned after {sw.Elapsed.TotalSeconds:N1}s");
    Console.WriteLine($"  events: {(result.Events.Count == 0 ? "(none)" : string.Join(", ", result.Events))}");
    Console.WriteLine($"  subscription lapsed: {result.SubscriptionLapsed}");
    Console.WriteLine(result.Events.Count > 0
        ? "  => the doorbell rang before the window closed: this is push, not polling."
        : "  => quiet window (no change detected).");
    return;
}

if (args.Contains("streamprobe", StringComparer.OrdinalIgnoreCase))
{
    // EWS streaming notifications are a long poll: Subscribe once, then
    // GetStreamingEvents holds the connection open (up to 30 minutes) and
    // writes events down it as they happen, returning when the window ends so
    // the client re-issues it. That is push-shaped without needing a public
    // endpoint — and it uses the EWS token we already hold for Autodiscover,
    // so it costs no new permission.
    var stAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

    foreach (var acct in await stAuth.GetAccountsAsync())
    {
        Console.WriteLine($"=== {acct.Username} ===");
        AuthenticationResult? tok = null;
        try { tok = await stAuth.AcquireSilentAsync(acct, GraphAuthenticator.ExchangeScopes); }
        catch (Exception ex) { Console.WriteLine($"  token failed: {ex.Message.Split('.')[0]}"); }
        if (tok is null) { Console.WriteLine("  no EWS token (scope not consented here)"); continue; }

        const string ews = "https://outlook.office365.com/EWS/Exchange.asmx";

        // 1. Subscribe to the inbox.
        var subscribe = """
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
                           xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
                           xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages">
              <soap:Header>
                <t:RequestServerVersion Version="Exchange2013"/>
              </soap:Header>
              <soap:Body>
                <m:Subscribe>
                  <m:StreamingSubscriptionRequest>
                    <t:FolderIds><t:DistinguishedFolderId Id="inbox"/></t:FolderIds>
                    <t:EventTypes>
                      <t:EventType>NewMailEvent</t:EventType>
                      <t:EventType>CreatedEvent</t:EventType>
                      <t:EventType>ModifiedEvent</t:EventType>
                      <t:EventType>DeletedEvent</t:EventType>
                    </t:EventTypes>
                  </m:StreamingSubscriptionRequest>
                </m:Subscribe>
              </soap:Body>
            </soap:Envelope>
            """;

        string? subscriptionId = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, ews);
            req.Headers.Authorization = new("Bearer", tok.AccessToken);
            req.Content = new StringContent(subscribe, System.Text.Encoding.UTF8, "text/xml");
            var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"  Subscribe: HTTP {(int)resp.StatusCode}");

            var sub = System.Text.RegularExpressions.Regex.Match(body, @"<[mt]:SubscriptionId>(.*?)</[mt]:SubscriptionId>");
            if (sub.Success) subscriptionId = sub.Groups[1].Value;
            else
            {
                var err = System.Text.RegularExpressions.Regex.Match(body, @"<faultstring[^>]*>(.*?)</faultstring>");
                Console.WriteLine($"    no subscription. Response:");
                Console.WriteLine(body.Length > 1600 ? body[..1600] : body);
                continue;
            }
            Console.WriteLine($"    subscription id: {subscriptionId[..Math.Min(28, subscriptionId.Length)]}…");
        }
        catch (Exception ex) { Console.WriteLine($"  Subscribe failed: {ex.Message}"); continue; }

        // 2. Hold the long poll open briefly to prove it stays connected.
        var stream = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
                           xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
                           xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages">
              <soap:Header><t:RequestServerVersion Version="Exchange2013"/></soap:Header>
              <soap:Body>
                <m:GetStreamingEvents>
                  <m:SubscriptionIds><t:SubscriptionId>{subscriptionId}</t:SubscriptionId></m:SubscriptionIds>
                  <m:ConnectionTimeout>1</m:ConnectionTimeout>
                </m:GetStreamingEvents>
              </soap:Body>
            </soap:Envelope>
            """;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var req = new HttpRequestMessage(HttpMethod.Post, ews);
            req.Headers.Authorization = new("Bearer", tok.AccessToken);
            req.Content = new StringContent(stream, System.Text.Encoding.UTF8, "text/xml");
            Console.WriteLine("  GetStreamingEvents: holding open (1 minute window)…");
            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var body = await resp.Content.ReadAsStringAsync();
            sw.Stop();
            Console.WriteLine($"    returned after {sw.Elapsed.TotalSeconds:N1}s, HTTP {(int)resp.StatusCode}");
            var status = System.Text.RegularExpressions.Regex.Match(body, @"<t:ConnectionStatus>(.*?)</t:ConnectionStatus>");
            Console.WriteLine($"    connection status: {(status.Success ? status.Groups[1].Value : "(none)")}");
            foreach (System.Text.RegularExpressions.Match ev in
                     System.Text.RegularExpressions.Regex.Matches(body, @"<t:(\w+Event)>"))
                Console.WriteLine($"    event: {ev.Groups[1].Value}");
        }
        catch (Exception ex) { Console.WriteLine($"  GetStreamingEvents failed: {ex.GetType().Name}: {ex.Message}"); }
    }
    return;
}

if (args.Contains("idleprobe", StringComparer.OrdinalIgnoreCase))
{
    // Can we hold an IMAP IDLE connection using the OAuth token we already have?
    // Graph has no push option for a desktop client (webhooks need a public
    // HTTPS endpoint), so IDLE is the only real-time route — but only if
    // Outlook still serves IMAP to these accounts over XOAUTH2.
    var idleAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    string[] imapScopes = ["https://outlook.office.com/IMAP.AccessAsUser.All"];

    foreach (var acct in await idleAuth.GetAccountsAsync())
    {
        Console.WriteLine($"=== {acct.Username} ===");
        AuthenticationResult? tok = null;
        try { tok = await idleAuth.AcquireSilentAsync(acct, imapScopes); }
        catch (Exception ex) { Console.WriteLine($"  no IMAP token: {ex.Message.Split('.')[0]}"); }
        if (tok is null)
        {
            Console.WriteLine("  IMAP scope not consented — would need it added to the registration.");
            continue;
        }

        using var client = new MailKit.Net.Imap.ImapClient();
        try
        {
            await client.ConnectAsync("outlook.office365.com", 993,
                MailKit.Security.SecureSocketOptions.SslOnConnect);
            await client.AuthenticateAsync(
                new MailKit.Security.SaslMechanismOAuth2(acct.Username, tok.AccessToken));
            Console.WriteLine($"  connected. IDLE supported: {client.Capabilities.HasFlag(MailKit.Net.Imap.ImapCapabilities.Idle)}");

            var inbox = client.Inbox;
            await inbox.OpenAsync(MailKit.FolderAccess.ReadOnly);
            Console.WriteLine($"  inbox: {inbox.Count:N0} messages, {inbox.Unread:N0} unread");

            if (client.Capabilities.HasFlag(MailKit.Net.Imap.ImapCapabilities.Idle))
            {
                Console.WriteLine("  holding IDLE for 20s to prove the connection stays up…");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var fired = false;
                inbox.CountChanged += (_, _) => { fired = true; Console.WriteLine("  *** IDLE fired: message count changed"); };
                try { await client.IdleAsync(timeout.Token); }
                catch (OperationCanceledException) { }
                Console.WriteLine($"  IDLE held cleanly (event fired during window: {fired})");
            }
            await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  IMAP failed: {ex.GetType().Name}: {ex.Message}");
        }
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

if (args.Contains("doorbell", StringComparer.OrdinalIgnoreCase))
{
    // End-to-end proof: hold the long poll open on the work account, send a
    // message to it from another account, and see whether the server tells us
    // before the window would have expired.
    var dbAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    using var dbHttp = new HttpClient { Timeout = TimeSpan.FromMinutes(6) };
    var target = "user@example.com";

    var acct = (await dbAuth.GetAccountsAsync())
        .FirstOrDefault(a => a.Username.Equals(target, StringComparison.OrdinalIgnoreCase));
    if (acct is null) { Console.WriteLine("work account not signed in"); return; }
    var tok = await dbAuth.AcquireSilentAsync(acct, GraphAuthenticator.ExchangeScopes);
    if (tok is null) { Console.WriteLine("no Exchange token"); return; }

    var stream = new MailboxEventStream(dbHttp);
    var sub = await stream.SubscribeAsync(target, tok.AccessToken, signedInAs: target);
    if (sub is null) { Console.WriteLine("subscribe failed"); return; }
    Console.WriteLine($"Subscribed to {target}. Holding the connection open…");

    // Send from a personal account while the poll is held.
    var senderAcct = (await dbAuth.GetAccountsAsync())
        .FirstOrDefault(a => a.Username.Contains("hotmail", StringComparison.OrdinalIgnoreCase));
    if (senderAcct is not null)
    {
        _ = Task.Run(async () =>
        {
            await Task.Delay(TimeSpan.FromSeconds(6));
            try
            {
                var sendTok = await dbAuth.AcquireSilentAsync(senderAcct);
                if (sendTok is null) { Console.WriteLine("  (sender has no token)"); return; }
                var g = new GraphServiceClient(new BaseBearerTokenAuthenticationProvider(
                    new GraphTokenProvider(dbAuth, sendTok)));
                var draft = new Mail.Core.Compose.Draft(
                    senderAcct.Username, [target], [], [],
                    $"eeeMail doorbell test {DateTime.Now:HH:mm:ss}",
                    "Sent to prove the live-update long poll fires.");
                var mime = Mail.Core.Compose.ReplyBuilder.ToMimeMessage(draft);
                using var ms = new MemoryStream();
                await mime.WriteToAsync(ms);
                using var req = new HttpRequestMessage(HttpMethod.Post,
                    "https://graph.microsoft.com/v1.0/me/sendMail");
                req.Headers.Authorization = new("Bearer", sendTok.AccessToken);
                req.Content = new StringContent(
                    Convert.ToBase64String(ms.ToArray()),
                    System.Text.Encoding.UTF8, "text/plain");
                using var send = new HttpClient();
                var resp = await send.SendAsync(req);
                Console.WriteLine($"  [sender] sent from {senderAcct.Username}: HTTP {(int)resp.StatusCode}");
            }
            catch (Exception ex) { Console.WriteLine($"  [sender] failed: {ex.Message}"); }
        });
    }

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var result = await stream.WaitForEventsAsync(sub, tok.AccessToken, connectionMinutes: 5,
        onEvents: evts => Console.WriteLine(
            $"  *** {sw.Elapsed.TotalSeconds:N1}s: {string.Join(", ", evts)} (delivered live)"));
    sw.Stop();
    Console.WriteLine($"Returned after {sw.Elapsed.TotalSeconds:N1}s");
    Console.WriteLine($"  events: {(result.Events.Count == 0 ? "(none)" : string.Join(", ", result.Events))}");
    Console.WriteLine($"  subscription lapsed: {result.SubscriptionLapsed}");
    Console.WriteLine(result.Events.Count > 0
        ? "  => the doorbell rang before the window closed: this is push, not polling."
        : "  => quiet window (no change detected).");
    return;
}

if (args.Contains("streamprobe", StringComparer.OrdinalIgnoreCase))
{
    // EWS streaming notifications are a long poll: Subscribe once, then
    // GetStreamingEvents holds the connection open (up to 30 minutes) and
    // writes events down it as they happen, returning when the window ends so
    // the client re-issues it. That is push-shaped without needing a public
    // endpoint — and it uses the EWS token we already hold for Autodiscover,
    // so it costs no new permission.
    var stAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    using var http = new HttpClient { Timeout = TimeSpan.FromMinutes(2) };

    foreach (var acct in await stAuth.GetAccountsAsync())
    {
        Console.WriteLine($"=== {acct.Username} ===");
        AuthenticationResult? tok = null;
        try { tok = await stAuth.AcquireSilentAsync(acct, GraphAuthenticator.ExchangeScopes); }
        catch (Exception ex) { Console.WriteLine($"  token failed: {ex.Message.Split('.')[0]}"); }
        if (tok is null) { Console.WriteLine("  no EWS token (scope not consented here)"); continue; }

        const string ews = "https://outlook.office365.com/EWS/Exchange.asmx";

        // 1. Subscribe to the inbox.
        var subscribe = """
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
                           xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
                           xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages">
              <soap:Header>
                <t:RequestServerVersion Version="Exchange2013"/>
              </soap:Header>
              <soap:Body>
                <m:Subscribe>
                  <m:StreamingSubscriptionRequest>
                    <t:FolderIds><t:DistinguishedFolderId Id="inbox"/></t:FolderIds>
                    <t:EventTypes>
                      <t:EventType>NewMailEvent</t:EventType>
                      <t:EventType>CreatedEvent</t:EventType>
                      <t:EventType>ModifiedEvent</t:EventType>
                      <t:EventType>DeletedEvent</t:EventType>
                    </t:EventTypes>
                  </m:StreamingSubscriptionRequest>
                </m:Subscribe>
              </soap:Body>
            </soap:Envelope>
            """;

        string? subscriptionId = null;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, ews);
            req.Headers.Authorization = new("Bearer", tok.AccessToken);
            req.Content = new StringContent(subscribe, System.Text.Encoding.UTF8, "text/xml");
            var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            Console.WriteLine($"  Subscribe: HTTP {(int)resp.StatusCode}");

            var sub = System.Text.RegularExpressions.Regex.Match(body, @"<[mt]:SubscriptionId>(.*?)</[mt]:SubscriptionId>");
            if (sub.Success) subscriptionId = sub.Groups[1].Value;
            else
            {
                var err = System.Text.RegularExpressions.Regex.Match(body, @"<faultstring[^>]*>(.*?)</faultstring>");
                Console.WriteLine($"    no subscription. Response:");
                Console.WriteLine(body.Length > 1600 ? body[..1600] : body);
                continue;
            }
            Console.WriteLine($"    subscription id: {subscriptionId[..Math.Min(28, subscriptionId.Length)]}…");
        }
        catch (Exception ex) { Console.WriteLine($"  Subscribe failed: {ex.Message}"); continue; }

        // 2. Hold the long poll open briefly to prove it stays connected.
        var stream = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
                           xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
                           xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages">
              <soap:Header><t:RequestServerVersion Version="Exchange2013"/></soap:Header>
              <soap:Body>
                <m:GetStreamingEvents>
                  <m:SubscriptionIds><t:SubscriptionId>{subscriptionId}</t:SubscriptionId></m:SubscriptionIds>
                  <m:ConnectionTimeout>1</m:ConnectionTimeout>
                </m:GetStreamingEvents>
              </soap:Body>
            </soap:Envelope>
            """;
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            using var req = new HttpRequestMessage(HttpMethod.Post, ews);
            req.Headers.Authorization = new("Bearer", tok.AccessToken);
            req.Content = new StringContent(stream, System.Text.Encoding.UTF8, "text/xml");
            Console.WriteLine("  GetStreamingEvents: holding open (1 minute window)…");
            var resp = await http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead);
            var body = await resp.Content.ReadAsStringAsync();
            sw.Stop();
            Console.WriteLine($"    returned after {sw.Elapsed.TotalSeconds:N1}s, HTTP {(int)resp.StatusCode}");
            var status = System.Text.RegularExpressions.Regex.Match(body, @"<t:ConnectionStatus>(.*?)</t:ConnectionStatus>");
            Console.WriteLine($"    connection status: {(status.Success ? status.Groups[1].Value : "(none)")}");
            foreach (System.Text.RegularExpressions.Match ev in
                     System.Text.RegularExpressions.Regex.Matches(body, @"<t:(\w+Event)>"))
                Console.WriteLine($"    event: {ev.Groups[1].Value}");
        }
        catch (Exception ex) { Console.WriteLine($"  GetStreamingEvents failed: {ex.GetType().Name}: {ex.Message}"); }
    }
    return;
}

if (args.Contains("idleprobe", StringComparer.OrdinalIgnoreCase))
{
    // Can we hold an IMAP IDLE connection using the OAuth token we already have?
    // Graph has no push option for a desktop client (webhooks need a public
    // HTTPS endpoint), so IDLE is the only real-time route — but only if
    // Outlook still serves IMAP to these accounts over XOAUTH2.
    var idleAuth = new GraphAuthenticator(Path.Combine(root, "msal.cache"));
    string[] imapScopes = ["https://outlook.office.com/IMAP.AccessAsUser.All"];

    foreach (var acct in await idleAuth.GetAccountsAsync())
    {
        Console.WriteLine($"=== {acct.Username} ===");
        AuthenticationResult? tok = null;
        try { tok = await idleAuth.AcquireSilentAsync(acct, imapScopes); }
        catch (Exception ex) { Console.WriteLine($"  no IMAP token: {ex.Message.Split('.')[0]}"); }
        if (tok is null)
        {
            Console.WriteLine("  IMAP scope not consented — would need it added to the registration.");
            continue;
        }

        using var client = new MailKit.Net.Imap.ImapClient();
        try
        {
            await client.ConnectAsync("outlook.office365.com", 993,
                MailKit.Security.SecureSocketOptions.SslOnConnect);
            await client.AuthenticateAsync(
                new MailKit.Security.SaslMechanismOAuth2(acct.Username, tok.AccessToken));
            Console.WriteLine($"  connected. IDLE supported: {client.Capabilities.HasFlag(MailKit.Net.Imap.ImapCapabilities.Idle)}");

            var inbox = client.Inbox;
            await inbox.OpenAsync(MailKit.FolderAccess.ReadOnly);
            Console.WriteLine($"  inbox: {inbox.Count:N0} messages, {inbox.Unread:N0} unread");

            if (client.Capabilities.HasFlag(MailKit.Net.Imap.ImapCapabilities.Idle))
            {
                Console.WriteLine("  holding IDLE for 20s to prove the connection stays up…");
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
                var fired = false;
                inbox.CountChanged += (_, _) => { fired = true; Console.WriteLine("  *** IDLE fired: message count changed"); };
                try { await client.IdleAsync(timeout.Token); }
                catch (OperationCanceledException) { }
                Console.WriteLine($"  IDLE held cleanly (event fired during window: {fired})");
            }
            await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  IMAP failed: {ex.GetType().Name}: {ex.Message}");
        }
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
var dek = EnsureMailboxRegistered(appDb, upn, root, out var mailboxDbPath);
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
}, mailboxAddress: upn);
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

static byte[] EnsureMailboxRegistered(SqliteConnection appDb, string upn, string root, out string dbPath)
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
    dbPath = Path.Combine(root, "mailboxes", upn + ".db");
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
