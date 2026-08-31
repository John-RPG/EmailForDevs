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

var months = args.Length > 0 && int.TryParse(args[0], out var m) ? m : 1;
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

// --- sync --------------------------------------------------------------------
var graph = new GraphServiceClient(
    new BaseBearerTokenAuthenticationProvider(new GraphTokenProvider(auth, signIn.Account)));
var since = DateTimeOffset.UtcNow.AddMonths(-months);
Console.WriteLine($"Syncing (initial window: last {months} month(s), incremental afterwards)…");
var stopwatch = Stopwatch.StartNew();
var sync = new GraphMailboxSync(graph, mailboxDb, Console.WriteLine);
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
