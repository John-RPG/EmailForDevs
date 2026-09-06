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
        /// Optional capabilities granted for the owning account. These affect
        /// what can be *found*; access to any mailbox is always Exchange's call.
        /// </summary>
        public IReadOnlySet<string> Capabilities { get; init; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase);
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
                   coalesce(m.sync_window_months, 0),
                   (SELECT group_concat(capability) FROM account_capabilities
                    WHERE account_id = a.id)
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
                Capabilities = reader.IsDBNull(12)
                    ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(
                        reader.GetString(12).Split(',', StringSplitOptions.RemoveEmptyEntries),
                        StringComparer.OrdinalIgnoreCase),
            });
        return result;
    }

    /// <summary>
    /// The optional capabilities granted for an account. Recorded only after a
    /// sign-in actually returned the scopes, so this reflects what was granted
    /// rather than what was asked for.
    /// </summary>
    public static HashSet<string> GetCapabilities(SqliteConnection appDb, string accountUpn)
    {
        using var cmd = appDb.CreateCommand();
        cmd.CommandText = """
            SELECT c.capability FROM account_capabilities c
            JOIN accounts a ON a.id = c.account_id
            WHERE a.upn = @u;
            """;
        cmd.Parameters.AddWithValue("@u", accountUpn);
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result.Add(reader.GetString(0));
        return result;
    }

    /// <summary>Records one capability as granted, or removes it when refused.</summary>
    public static void SetCapability(
        SqliteConnection appDb, string accountUpn, string capability, bool granted)
    {
        using var cmd = appDb.CreateCommand();
        cmd.CommandText = granted
            ? """
              INSERT OR REPLACE INTO account_capabilities(account_id, capability, granted_at)
              SELECT id, @c, unixepoch() FROM accounts WHERE upn = @u;
              """
            : """
              DELETE FROM account_capabilities
              WHERE capability = @c
                AND account_id = (SELECT id FROM accounts WHERE upn = @u);
              """;
        cmd.Parameters.AddWithValue("@c", capability);
        cmd.Parameters.AddWithValue("@u", accountUpn);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Whether one capability is granted for this account.</summary>
    public static bool HasCapability(SqliteConnection appDb, string accountUpn, string capability) =>
        GetCapabilities(appDb, accountUpn).Contains(capability);

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
    /// Registers a newly signed-in account together with its own mailbox. The
    /// database is created lazily on first sync; only the key is minted here.
    /// </summary>
    public static long AddAccountWithMailbox(
        SqliteConnection appDb, string upn, string mailboxDir)
    {
        Directory.CreateDirectory(Path.GetFullPath(mailboxDir));
        var dbPath = Path.Combine(mailboxDir, Sanitise(upn) + ".db");

        using var cmd = appDb.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO identities(id, name) VALUES(1, 'Default');
            INSERT INTO accounts(identity_id, kind, display_name, upn)
            VALUES(1, 'graph', @u, @u);
            INSERT INTO mailboxes(account_id, upn, display_name, kind, db_path, dek,
                                  enabled, visible, sync_policy)
            VALUES(last_insert_rowid(), @u, @u, 'primary', @p, @k, 1, 1, 'MirrorServer');
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@u", upn);
        cmd.Parameters.AddWithValue("@p", dbPath);
        cmd.Parameters.AddWithValue("@k", RandomNumberGenerator.GetBytes(32));
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
