using System.Security.Cryptography;
using Mail.Storage.Database;
using Microsoft.Data.Sqlite;

namespace Mail.Tests;

public sealed class MailboxRegistryTests : IDisposable
{
    readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));
    readonly SqliteConnection _appDb;

    public MailboxRegistryTests()
    {
        Directory.CreateDirectory(_dir);
        _appDb = AppDatabase.Open(
            Path.Combine(_dir, "app.db"), RandomNumberGenerator.GetBytes(32));
    }

    public void Dispose()
    {
        _appDb.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    long AddAccount(string upn)
    {
        using var cmd = _appDb.CreateCommand();
        cmd.CommandText = """
            INSERT OR IGNORE INTO identities(id, name) VALUES(1, 'Default');
            INSERT INTO accounts(identity_id, kind, display_name, upn)
            VALUES(1, 'graph', @u, @u);
            SELECT last_insert_rowid();
            """;
        cmd.Parameters.AddWithValue("@u", upn);
        var accountId = Convert.ToInt64(cmd.ExecuteScalar());

        using var mailbox = _appDb.CreateCommand();
        mailbox.CommandText = """
            INSERT INTO mailboxes(account_id, upn, display_name, kind, db_path, dek)
            VALUES(@a, @u, @u, 'primary', @p, @k);
            """;
        mailbox.Parameters.AddWithValue("@a", accountId);
        mailbox.Parameters.AddWithValue("@u", upn);
        mailbox.Parameters.AddWithValue("@p", Path.Combine(_dir, upn + ".db"));
        mailbox.Parameters.AddWithValue("@k", RandomNumberGenerator.GetBytes(32));
        mailbox.ExecuteNonQuery();
        return accountId;
    }

    [Fact]
    public void Shared_mailbox_is_listed_under_its_owning_account()
    {
        var accountId = AddAccount("john@example.com");
        MailboxRegistry.AddShared(
            _appDb, accountId, "john@example.com", "support@example.com",
            "Support", Path.Combine(_dir, "mailboxes"));

        var all = MailboxRegistry.List(_appDb);
        Assert.Equal(2, all.Count);
        // The account's own mailbox leads; shared mailboxes follow it.
        Assert.Equal("primary", all[0].Kind);
        Assert.Equal("shared", all[1].Kind);
        Assert.All(all, m => Assert.Equal("john@example.com", m.AccountUpn));
        Assert.Equal("Support", all[1].DisplayName);
    }

    [Fact]
    public void Shared_mailbox_gets_its_own_key_and_file()
    {
        var accountId = AddAccount("john@example.com");
        MailboxRegistry.AddShared(
            _appDb, accountId, "john@example.com", "support@example.com",
            "Support", Path.Combine(_dir, "mailboxes"));

        var entries = MailboxRegistry.List(_appDb);
        var primary = entries.Single(m => m.Kind == "primary");
        var shared = entries.Single(m => m.Kind == "shared");

        Assert.Equal(32, shared.Dek.Length);
        Assert.NotEqual(primary.Dek, shared.Dek);
        Assert.NotEqual(primary.DbPath, shared.DbPath);
    }

    [Fact]
    public void Two_accounts_reaching_one_shared_mailbox_keep_separate_databases()
    {
        // Each account holds its own copy under its own key, so the file name
        // must distinguish them or the second would open the first's database
        // with the wrong key.
        var first = AddAccount("john@example.com");
        var second = AddAccount("jane@example.com");
        MailboxRegistry.AddShared(_appDb, first, "john@example.com",
            "support@example.com", "Support", Path.Combine(_dir, "mailboxes"));
        MailboxRegistry.AddShared(_appDb, second, "jane@example.com",
            "support@example.com", "Support", Path.Combine(_dir, "mailboxes"));

        var shared = MailboxRegistry.List(_appDb).Where(m => m.Kind == "shared").ToList();
        Assert.Equal(2, shared.Count);
        Assert.NotEqual(shared[0].DbPath, shared[1].DbPath);
    }

    [Fact]
    public void Exists_is_scoped_to_the_account()
    {
        var first = AddAccount("john@example.com");
        var second = AddAccount("jane@example.com");
        MailboxRegistry.AddShared(_appDb, first, "john@example.com",
            "support@example.com", "Support", Path.Combine(_dir, "mailboxes"));

        Assert.True(MailboxRegistry.Exists(_appDb, first, "support@example.com"));
        Assert.False(MailboxRegistry.Exists(_appDb, second, "support@example.com"));
    }

    [Fact]
    public void RemoveShared_deletes_the_row_and_its_database()
    {
        var accountId = AddAccount("john@example.com");
        var mailboxDir = Path.Combine(_dir, "mailboxes");
        var id = MailboxRegistry.AddShared(
            _appDb, accountId, "john@example.com", "support@example.com",
            "Support", mailboxDir);

        var entry = MailboxRegistry.List(_appDb).Single(m => m.Id == id);
        using (var db = MailboxDatabase.Open(entry.DbPath, entry.Dek)) { }
        SqliteConnection.ClearAllPools();
        Assert.True(File.Exists(entry.DbPath));

        Assert.True(MailboxRegistry.RemoveShared(_appDb, id, _dir));
        Assert.DoesNotContain(MailboxRegistry.List(_appDb), m => m.Id == id);
        Assert.False(File.Exists(entry.DbPath));
    }

    [Fact]
    public void RemoveShared_refuses_to_delete_a_primary_mailbox()
    {
        AddAccount("john@example.com");
        var primary = MailboxRegistry.List(_appDb).Single();

        // A primary mailbox is the account itself; removing it here would leave
        // an account with nothing to sync and no way to re-add it.
        Assert.False(MailboxRegistry.RemoveShared(_appDb, primary.Id, _dir));
        Assert.Single(MailboxRegistry.List(_appDb));
    }
    [Fact]
    public void A_pending_capability_is_remembered_but_not_usable()
    {
        // The reported bug: in a tenant where consent goes to an administrator,
        // the request returns nothing, which used to be recorded as a refusal.
        // The toggles reset, and the whole selection had to be made again after
        // approval arrived.
        AddAccount("john@example.com");
        MailboxRegistry.SetCapabilityPending(_appDb, "john@example.com", "shared-mail");

        // Remembered, so the choice survives and the UI can keep it switched on.
        Assert.Contains("shared-mail",
            MailboxRegistry.GetPendingCapabilities(_appDb, "john@example.com"));

        // But not usable: treating it as granted would make the app request
        // scopes it does not hold on every launch, prompting a browser each time.
        Assert.DoesNotContain("shared-mail",
            MailboxRegistry.GetCapabilities(_appDb, "john@example.com"));
        Assert.False(MailboxRegistry.HasCapability(_appDb, "john@example.com", "shared-mail"));
    }

    [Fact]
    public void Approval_turns_a_pending_capability_into_a_granted_one()
    {
        AddAccount("john@example.com");
        MailboxRegistry.SetCapabilityPending(_appDb, "john@example.com", "shared-mail");
        MailboxRegistry.SetCapability(_appDb, "john@example.com", "shared-mail", true);

        Assert.Contains("shared-mail",
            MailboxRegistry.GetCapabilities(_appDb, "john@example.com"));
        // No longer pending: a capability must not be in both states at once, or
        // the UI would show "granted" and "awaiting approval" together.
        Assert.Empty(MailboxRegistry.GetPendingCapabilities(_appDb, "john@example.com"));
    }

    [Fact]
    public void Refusing_a_capability_clears_it_from_both_states()
    {
        AddAccount("john@example.com");
        MailboxRegistry.SetCapabilityPending(_appDb, "john@example.com", "shared-mail");
        MailboxRegistry.SetCapability(_appDb, "john@example.com", "shared-mail", false);

        Assert.Empty(MailboxRegistry.GetCapabilities(_appDb, "john@example.com"));
        Assert.Empty(MailboxRegistry.GetPendingCapabilities(_appDb, "john@example.com"));
    }

    [Fact]
    public void A_granted_capability_can_be_demoted_back_to_pending()
    {
        // What the launch-time check does when the scopes turn out to be absent:
        // the feature must stop working, but the request is still the user's.
        AddAccount("john@example.com");
        MailboxRegistry.SetCapability(_appDb, "john@example.com", "shared-mail", true);
        MailboxRegistry.SetCapabilityPending(_appDb, "john@example.com", "shared-mail");

        Assert.Empty(MailboxRegistry.GetCapabilities(_appDb, "john@example.com"));
        Assert.Contains("shared-mail",
            MailboxRegistry.GetPendingCapabilities(_appDb, "john@example.com"));
    }
}