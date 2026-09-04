using Microsoft.Data.Sqlite;

namespace Mail.Storage.Database;

/// <summary>
/// The profile-level app.db: identities, accounts, the mailbox registry (including
/// each mailbox's data-encryption key), global address ranking, and UI state.
/// Encrypted with the profile master key; holds no auth secrets (tokens live in
/// MSAL's DPAPI cache, passwords in Windows Credential Manager).
/// </summary>
public static class AppDatabase
{
    public static SqliteConnection Open(string path, ReadOnlySpan<byte> masterKey)
    {
        var conn = EncryptedSqlite.Open(path, masterKey);
        MigrationRunner.Apply(conn, Migrations);
        return conn;
    }

    static readonly IReadOnlyList<string> Migrations = [V1, V2];

    /// <summary>Schema version a freshly-migrated app.db reports.</summary>
    public static int SchemaVersion => Migrations.Count;

    const string V2 = """
        -- Whether this account was consented for mailbox discovery. Off by
        -- default: the extra scopes are useless on consumer accounts, and
        -- turning them on costs a fresh consent prompt.
        ALTER TABLE accounts ADD COLUMN discovery_enabled INTEGER NOT NULL DEFAULT 0;
        """;

    const string V1 = """
        CREATE TABLE identities(
            id       INTEGER PRIMARY KEY,
            name     TEXT NOT NULL,
            position INTEGER NOT NULL DEFAULT 0
        );

        CREATE TABLE accounts(
            id           INTEGER PRIMARY KEY,
            identity_id  INTEGER NOT NULL REFERENCES identities(id),
            kind         TEXT NOT NULL,             -- 'graph' | 'imap' | 'pop'
            display_name TEXT NOT NULL,
            upn          TEXT,                      -- sign-in address
            auth_ref     TEXT,                      -- MSAL account id / CredMan target
            settings     TEXT                       -- JSON: hosts, ports, options
        );

        CREATE TABLE mailboxes(
            id                 INTEGER PRIMARY KEY,
            account_id         INTEGER NOT NULL REFERENCES accounts(id),
            upn                TEXT NOT NULL,       -- mailbox SMTP address
            display_name      TEXT,
            kind               TEXT NOT NULL,       -- 'primary' | 'shared' | 'delegated'
            db_path            TEXT NOT NULL,       -- authoritative; filename is cosmetic
            dek                BLOB NOT NULL,       -- raw 32-byte SQLCipher key for its DB
            enabled            INTEGER NOT NULL DEFAULT 1,
            visible            INTEGER NOT NULL DEFAULT 1,
            sync_policy        TEXT NOT NULL DEFAULT 'MirrorServer',
            sync_window_months INTEGER,             -- for WindowedCache
            position           INTEGER NOT NULL DEFAULT 0,
            UNIQUE(account_id, upn)
        );

        CREATE TABLE address_ranking(
            email        TEXT PRIMARY KEY COLLATE NOCASE,
            display_name TEXT,
            score        REAL NOT NULL DEFAULT 0,
            last_used_at INTEGER
        );

        CREATE TABLE ui_state(
            key   TEXT PRIMARY KEY,
            value TEXT
        );
        """;
}
