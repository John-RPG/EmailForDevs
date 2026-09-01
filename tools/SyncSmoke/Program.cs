// Full vertical slice: profile keys (DPAPI + recovery) → encrypted app.db →
// mailbox registry with per-mailbox DEK → MSAL auth → Graph delta sync into the
// encrypted mailbox DB → stats and a sample FTS search.
// Run from the repo root:  dotnet run --project tools/SyncSmoke [months]
using System.Diagnostics;
using System.Security.Cryptography;
using Mail.Storage;
using Mail.Storage.Database;
using Mail.Storage.Security;
using Mail.Sync.Auth;
using Mail.Sync.Graph;
using Microsoft.Data.Sqlite;
using Microsoft.Graph;
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
