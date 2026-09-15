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

    static readonly IReadOnlyList<string> Migrations = [V1, V2, V3, V4, V5, V6, V7, V8, V9];

    const string V9 = """
        -- A capability can be asked for without being granted yet: in a tenant
        -- that requires administrator approval, consent goes to the admin and
        -- the app learns nothing at the time. Previously that was indistinguishable
        -- from a refusal, so the toggles snapped back off and the user had to set
        -- them all again after approval arrived.
        --
        -- requested_at records what was asked for; granted_at stays the record of
        -- what actually came back. A row with a request and no grant is pending.
        ALTER TABLE account_capabilities ADD COLUMN requested_at INTEGER NOT NULL DEFAULT 0;

        -- Existing rows were only ever written on a real grant, so they are not
        -- pending; mark them as requested at the same moment for consistency.
        UPDATE account_capabilities SET requested_at = granted_at;

        -- granted_at becomes nullable in meaning rather than in type: 0 records
        -- "asked for, not granted". No existing row carries 0, so nothing moves.
        """;

    const string V8 = """
        -- Accounts are ordered as units, so the folder tree can be arranged the
        -- way the user wants rather than by the order they happened to sign in.
        ALTER TABLE accounts ADD COLUMN position INTEGER NOT NULL DEFAULT 0;
        UPDATE accounts SET position = id;
        """;

    const string V7 = """
        -- Unsent messages. In app.db rather than a mailbox database because a
        -- draft is not yet mail: it has no server id, the account it will be
        -- sent from can still change, and it must outlive a mailbox being
        -- removed. Attachments are inline JSON — a draft is short-lived, and the
        -- content-addressed blob store is for messages that actually exist.
        CREATE TABLE drafts(
            id                INTEGER PRIMARY KEY,
            from_address      TEXT NOT NULL,
            to_addresses      TEXT NOT NULL DEFAULT '',
            cc_addresses      TEXT NOT NULL DEFAULT '',
            bcc_addresses     TEXT NOT NULL DEFAULT '',
            subject           TEXT NOT NULL DEFAULT '',
            body              TEXT NOT NULL DEFAULT '',
            html_body         TEXT,
            is_rich           INTEGER NOT NULL DEFAULT 0,
            in_reply_to       TEXT,
            references_header TEXT,
            attachments       TEXT NOT NULL DEFAULT '[]',
            updated_at        INTEGER NOT NULL
        );
        CREATE INDEX ix_drafts_updated ON drafts(updated_at DESC);
        """;

    const string V6 = """
        -- Integer settings could be stored unparseable before validation was
        -- added on write (a UI binding wrote back mid-edit and produced values
        -- like "ser6"). Such a row reads as the default anyway, so deleting it
        -- changes no behaviour — it only stops the settings screen showing a
        -- value that is not in force.
        DELETE FROM settings
        WHERE key IN ('sync.window_months', 'sync.max_concurrent_downloads',
                      'read.mark_read_delay_ms', 'display.quiet_folder_days',
                      'send.delay_seconds')
          AND CAST(value AS INTEGER) = 0
          AND value NOT IN ('0', '-0');
        """;

    const string V5 = """
        -- V3 carried the old discovery_enabled flag over as granted capabilities,
        -- but that flag recorded what had been *asked for*, not what the tenant
        -- actually granted. Where the scopes were never granted, the app then
        -- requested them on every launch, MSAL could not answer from cache, and
        -- the user got a browser prompt each time they opened the app.
        --
        -- Drop those two, so they are re-derived from a real sign-in. Nothing is
        -- lost: an account that genuinely holds the scopes records them again the
        -- next time capabilities are applied, and until then the features simply
        -- report themselves as off.
        DELETE FROM account_capabilities WHERE capability IN ('people', 'directory');
        """;

    const string V4 = """
        -- One table for every setting at every level. Only overrides are stored:
        -- a level with no row inherits, which is what keeps "inherited" distinct
        -- from "set to the same value the parent happens to have".
        CREATE TABLE settings(
            key        TEXT NOT NULL,
            scope      INTEGER NOT NULL,   -- SettingScope: 0 folder … 3 application
            target     TEXT NOT NULL DEFAULT '',  -- id at that scope; '' for application
            value      TEXT NOT NULL,
            updated_at INTEGER NOT NULL,
            PRIMARY KEY(key, scope, target)
        );

        -- Fold the existing per-mailbox columns in, so nothing configured is lost.
        -- Only real overrides move: a mailbox already on the default contributes
        -- no row, leaving it free to follow the default if that later changes.
        INSERT INTO settings(key, scope, target, value, updated_at)
        SELECT 'sync.policy', 1, CAST(id AS TEXT), sync_policy, unixepoch()
        FROM mailboxes WHERE sync_policy IS NOT NULL AND sync_policy <> 'MirrorServer';

        INSERT INTO settings(key, scope, target, value, updated_at)
        SELECT 'sync.window_months', 1, CAST(id AS TEXT),
               CAST(sync_window_months AS TEXT), unixepoch()
        FROM mailboxes WHERE sync_window_months IS NOT NULL AND sync_window_months > 0;

        INSERT INTO settings(key, scope, target, value, updated_at)
        SELECT 'sync.enabled', 1, CAST(id AS TEXT), 'false', unixepoch()
        FROM mailboxes WHERE enabled = 0;
        """;

    const string V3 = """
        -- Which optional capabilities the user granted for this account, one row
        -- each. A set rather than the old single flag: permissions differ in
        -- breadth and in whether an administrator must approve them, so they are
        -- accepted or refused individually.
        CREATE TABLE account_capabilities(
            account_id INTEGER NOT NULL REFERENCES accounts(id),
            capability TEXT NOT NULL,
            granted_at INTEGER NOT NULL,
            PRIMARY KEY(account_id, capability)
        );

        -- Carry the old flag over: it stood for the two Graph lookup scopes.
        INSERT INTO account_capabilities(account_id, capability, granted_at)
        SELECT id, 'people', unixepoch() FROM accounts WHERE discovery_enabled = 1
        UNION ALL
        SELECT id, 'directory', unixepoch() FROM accounts WHERE discovery_enabled = 1;
        """;

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
