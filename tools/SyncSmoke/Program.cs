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

if (args.Length > 0)
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
