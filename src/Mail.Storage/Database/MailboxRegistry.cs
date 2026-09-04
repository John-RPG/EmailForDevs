using System.Security.Cryptography;
using Microsoft.Data.Sqlite;

namespace Mail.Storage.Database;

/// <summary>
/// Reads and writes the app.db mailbox registry: which mailboxes exist, which
/// account each belongs to, and the key to its database.
///
/// A shared mailbox is a mailbox row under the same account as the signed-in
/// user who reaches it, because that account's token is what opens it — lose
/// the account and the shared mailbox becomes unreachable, so the relationship
/// is ownership, not merely grouping.
/// </summary>
public static class MailboxRegistry
{
    /// <param name="Kind">'primary' | 'shared' | 'delegated'.</param>
    public sealed record MailboxEntry(
        long Id, long AccountId, string AccountUpn, string Upn, string DisplayName,
        string Kind, string DbPath, byte[] Dek, bool Enabled, bool Visible,
        string Policy, int WindowMonths)
    {
        /// <summary>
        /// Whether the owning account was consented for mailbox discovery. Only
        /// affects finding mailboxes; access to one is always Exchange's call.
        /// </summary>
        public bool DiscoveryEnabled { get; init; }
    }

    /// <summary>
    /// Every registered mailbox, ordered so each account's own mailbox leads its
    /// shared ones — the order the folder tree renders them in.
    /// </summary>
    public static List<MailboxEntry> List(SqliteConnection appDb, bool enabledOnly = false)
    {
        using var cmd = appDb.CreateCommand();
        cmd.CommandText = $"""
            SELECT m.id, m.account_id, coalesce(a.upn, a.display_name), m.upn,
                   coalesce(m.display_name, m.upn), m.kind, m.db_path, m.dek,
                   m.enabled, m.visible, coalesce(m.sync_policy,'MirrorServer'),
                   coalesce(m.sync_window_months, 0), a.discovery_enabled
            FROM mailboxes m
            JOIN accounts a ON a.id = m.account_id
            {(enabledOnly ? "WHERE m.enabled = 1" : "")}
            ORDER BY a.id, m.kind = 'primary' DESC, m.position, m.id;
            """;
        using var reader = cmd.ExecuteReader();
        var result = new List<MailboxEntry>();
        while (reader.Read())
            result.Add(new MailboxEntry(
                reader.GetInt64(0), reader.GetInt64(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetString(5), reader.GetString(6),
                (byte[])reader.GetValue(7), reader.GetInt64(8) != 0, reader.GetInt64(9) != 0,
                reader.GetString(10), reader.GetInt32(11))
            {
                DiscoveryEnabled = reader.GetInt64(12) != 0,
            });
        return result;
    }

    /// <summary>
    /// Records that an account has been consented for discovery. Set only after a
    /// sign-in actually returned the discovery scopes, so the flag reflects what
    /// was granted rather than what was asked for.
    /// </summary>
    public static void SetDiscoveryEnabled(SqliteConnection appDb, string accountUpn, bool enabled)
    {
        using var cmd = appDb.CreateCommand();
        cmd.CommandText = "UPDATE accounts SET discovery_enabled = @e WHERE upn = @u;";
        cmd.Parameters.AddWithValue("@e", enabled ? 1 : 0);
        cmd.Parameters.AddWithValue("@u", accountUpn);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Whether this account may enumerate other mailboxes.</summary>
    public static bool IsDiscoveryEnabled(SqliteConnection appDb, string accountUpn)
    {
        using var cmd = appDb.CreateCommand();
        cmd.CommandText = "SELECT discovery_enabled FROM accounts WHERE upn = @u;";
        cmd.Parameters.AddWithValue("@u", accountUpn);
        return cmd.ExecuteScalar() is long flag && flag != 0;
    }

    /// <summary>True when this account already holds a mailbox at that address.</summary>
    public static bool Exists(SqliteConnection appDb, long accountId, string upn)
    {
        using var cmd = appDb.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM mailboxes WHERE account_id = @a AND upn = @u;";
        cmd.Parameters.AddWithValue("@a", accountId);
        cmd.Parameters.AddWithValue("@u", upn);
        return cmd.ExecuteScalar() is not null;
    }

    /// <summary>
    /// Registers a shared mailbox under an existing account and returns its id.
    /// The database is created lazily on first sync; only the key is minted here.
    /// </summary>
    public static long AddShared(
        SqliteConnection appDb, long accountId, string accountUpn, string upn,
        string displayName, string mailboxDir, string policy = "MirrorServer",
        int? windowMonths = null)
    {
        // Two accounts may both reach the same shared mailbox, and each keeps its
        // own copy under its own key — so the filename carries the owning account
        // to keep them distinct on disk.
        var fileName = $"{Sanitise(accountUpn)}--{Sanitise(upn)}.db";
        var dbPath = Path.Combine(mailboxDir, fileName);
        Directory.CreateDirectory(Path.GetFullPath(mailboxDir));

        using var cmd = appDb.CreateCommand();
        cmd.CommandText = """
            INSERT INTO mailboxes(account_id, upn, display_name, kind, db_path, dek,
                                  enabled, visible, sync_policy, sync_window_months, position)
            VALUES(@a, @u, @d, 'shared', @p, @k, 1, 1, @sp, @w,
                   (SELECT coalesce(max(position), 0) + 1 FROM mailboxes WHERE account_id = @a));
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@a", accountId);
        cmd.Parameters.AddWithValue("@u", upn);
        cmd.Parameters.AddWithValue("@d", displayName);
        cmd.Parameters.AddWithValue("@p", dbPath);
        cmd.Parameters.AddWithValue("@k", RandomNumberGenerator.GetBytes(32));
        cmd.Parameters.AddWithValue("@sp", policy);
        cmd.Parameters.AddWithValue("@w", (object?)windowMonths ?? DBNull.Value);
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>
    /// Forgets a shared mailbox and deletes its database. Only ever applied to
    /// 'shared' rows: a primary mailbox is the account itself, and removing it
    /// here would leave an account with nothing to sync.
    /// </summary>
    public static bool RemoveShared(SqliteConnection appDb, long mailboxId, string repoRoot)
    {
        string? dbPath = null;
        using (var find = appDb.CreateCommand())
        {
            find.CommandText = "SELECT db_path FROM mailboxes WHERE id = @i AND kind = 'shared';";
            find.Parameters.AddWithValue("@i", mailboxId);
            dbPath = find.ExecuteScalar() as string;
        }
        if (dbPath is null) return false;

        using (var del = appDb.CreateCommand())
        {
            del.CommandText = "DELETE FROM mailboxes WHERE id = @i AND kind = 'shared';";
            del.Parameters.AddWithValue("@i", mailboxId);
            if (del.ExecuteNonQuery() == 0) return false;
        }

        var resolved = Path.IsPathRooted(dbPath) ? dbPath : Path.Combine(repoRoot, dbPath);
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            try { File.Delete(resolved + suffix); }
            catch (IOException) { /* still open; the row is gone, the file is orphaned */ }
        }
        return true;
    }

    /// <summary>Keeps an address usable as a filename without losing which mailbox it is.</summary>
    static string Sanitise(string address)
    {
        var invalid = Path.GetInvalidFileNameChars();
        return new string([.. address.Select(c => invalid.Contains(c) ? '_' : c)]);
    }
}
