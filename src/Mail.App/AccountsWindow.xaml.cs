using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using Mail.Storage.Database;
using Mail.Sync.Auth;
using Microsoft.Graph;
using Microsoft.Identity.Client;
using Microsoft.Data.Sqlite;

namespace Mail.App;

/// <summary>
/// Accounts and sync configuration: per-mailbox policy/window/enabled state,
/// storage stats, sync-state reset, and interactive account sign-in. Edits are
/// applied to app.db on Save; widening a mailbox's scope resets its delta
/// checkpoints so the next sync backfills the newly-included history.
/// </summary>
public partial class AccountsWindow : Window
{
    public sealed class MailboxConfig
    {
        public long Id { get; init; }
        public bool Enabled { get; set; }
        public string Upn { get; init; } = "";
        public string Kind { get; init; } = "";
        public string Policy { get; set; } = "MirrorServer";
        public string MonthsText { get; set; } = "";
        public string Messages { get; init; } = "";
        public string DbSize { get; init; } = "";
        public string DbPath { get; init; } = "";

        public bool OriginalEnabled { get; init; }
        public string OriginalPolicy { get; init; } = "";
        public string OriginalMonths { get; init; } = "";
        public byte[] Dek { get; init; } = [];
        public string ResolvedDbPath { get; init; } = "";

        /// <summary>Blank for shared mailboxes: discovery belongs to the account.</summary>
        public string Discovery { get; init; } = "";
    }

    readonly SqliteConnection _appDb;
    readonly string _repoRoot;
    readonly string _scratchRoot;
    List<MailboxConfig> _rows = [];

    public bool ChangesApplied { get; private set; }

    /// <summary>
    /// Supplies a Graph client for a given signed-in account. Set by the main
    /// window, which owns the token cache; without it the shared-mailbox picker
    /// has no way to authenticate.
    /// </summary>
    public Func<string, Task<GraphServiceClient>>? GraphForAccount { get; set; }

    /// <summary>
    /// Re-signs an account in, requesting the discovery scopes. Returns whether
    /// they were actually granted.
    /// </summary>
    public Func<string, Task<bool>>? SignInWithDiscovery { get; set; }

    public AccountsWindow(SqliteConnection appDb, string scratchRoot)
    {
        InitializeComponent();
        _appDb = appDb;
        _scratchRoot = scratchRoot;
        _repoRoot = Path.GetDirectoryName(scratchRoot)!;
        Loaded += (_, _) => LoadRows();
    }

    void LoadRows()
    {
        _rows = [];
        foreach (var entry in MailboxRegistry.List(_appDb))
        {
            var resolved = Path.IsPathRooted(entry.DbPath)
                ? entry.DbPath
                : Path.Combine(_repoRoot, entry.DbPath);
            var months = entry.WindowMonths > 0
                ? entry.WindowMonths.ToString(CultureInfo.InvariantCulture)
                : "";
            _rows.Add(new MailboxConfig
            {
                Id = entry.Id,
                Enabled = entry.Enabled,
                OriginalEnabled = entry.Enabled,
                Upn = entry.Upn,
                Kind = entry.Kind,
                Policy = entry.Policy,
                OriginalPolicy = entry.Policy,
                MonthsText = months,
                OriginalMonths = months,
                Messages = CountMessages(resolved, entry.Dek),
                DbSize = FormatSize(resolved),
                DbPath = entry.DbPath,
                ResolvedDbPath = resolved,
                Dek = entry.Dek,
                Discovery = entry.Kind != "primary" ? ""
                    : entry.DiscoveryEnabled ? "on" : "off",
            });
        }
        MailboxGrid.ItemsSource = _rows;
        StatusLabel.Text = $"{_rows.Count:N0} mailbox(es). Total storage: {TotalSize()}.";
    }

    static string CountMessages(string dbPath, byte[] dek)
    {
        if (!File.Exists(dbPath)) return "—";
        try
        {
            using var db = MailboxDatabase.Open(dbPath, dek);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT count(*) FROM messages;";
            return Convert.ToInt64(cmd.ExecuteScalar()).ToString("N0");
        }
        catch
        {
            return "?";
        }
    }

    static string FormatSize(string dbPath)
    {
        if (!File.Exists(dbPath)) return "—";
        var bytes = new FileInfo(dbPath).Length;
        return bytes >= 1L << 30 ? $"{bytes / (double)(1L << 30):N2} GB"
             : bytes >= 1L << 20 ? $"{bytes / (double)(1L << 20):N1} MB"
             : $"{bytes / 1024.0:N0} KB";
    }

    string TotalSize()
    {
        var total = _rows
            .Where(r => File.Exists(r.ResolvedDbPath))
            .Sum(r => new FileInfo(r.ResolvedDbPath).Length);
        return total >= 1L << 30 ? $"{total / (double)(1L << 30):N2} GB"
             : $"{total / (double)(1L << 20):N1} MB";
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        MailboxGrid.CommitEdit();
        var resets = new List<string>();
        foreach (var row in _rows)
        {
            int? months = int.TryParse(row.MonthsText, out var m) && m > 0 ? m : null;
            if (row.Policy == "WindowedCache" && months is null)
            {
                MessageBox.Show(this,
                    $"{row.Upn}: WindowedCache needs a month count.",
                    "Accounts", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            using (var update = _appDb.CreateCommand())
            {
                update.CommandText = """
                    UPDATE mailboxes SET enabled = @e, sync_policy = @p, sync_window_months = @m
                    WHERE id = @id;
                    """;
                update.Parameters.AddWithValue("@e", row.Enabled ? 1 : 0);
                update.Parameters.AddWithValue("@p", row.Policy);
                update.Parameters.AddWithValue("@m",
                    row.Policy == "MirrorServer" ? DBNull.Value : months!.Value);
                update.Parameters.AddWithValue("@id", row.Id);
                update.ExecuteNonQuery();
            }

            // Widening scope needs a fresh initial pass to pull the newly-included
            // history; narrowing or unchanged scope keeps its checkpoints.
            var wasMirror = row.OriginalPolicy == "MirrorServer";
            var nowMirror = row.Policy == "MirrorServer";
            var oldMonths = int.TryParse(row.OriginalMonths, out var om) ? om : 0;
            var widened = (!wasMirror && nowMirror) ||
                          (!wasMirror && !nowMirror && months > oldMonths);
            if (widened && ResetSyncState(row))
                resets.Add(row.Upn);
        }
        ChangesApplied = true;
        StatusLabel.Text = resets.Count > 0
            ? $"Saved. Backfill queued for: {string.Join(", ", resets)}."
            : "Saved.";
        LoadRows();
    }

    static bool ResetSyncState(MailboxConfig row)
    {
        if (!File.Exists(row.ResolvedDbPath)) return false;
        try
        {
            using var db = MailboxDatabase.Open(row.ResolvedDbPath, row.Dek);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "DELETE FROM sync_state;";
            cmd.ExecuteNonQuery();
            return true;
        }
        catch
        {
            return false;
        }
    }

    void OnResetSync(object sender, RoutedEventArgs e)
    {
        if (MailboxGrid.SelectedItem is not MailboxConfig row)
        {
            StatusLabel.Text = "Select a mailbox first.";
            return;
        }
        if (MessageBox.Show(this,
                $"Clear delta checkpoints for {row.Upn}?\n\n" +
                "The next sync re-lists every folder. Stored messages are recognised " +
                "by id and are not downloaded again.",
                "Reset sync state", MessageBoxButton.OKCancel, MessageBoxImage.Question)
            != MessageBoxResult.OK)
            return;
        StatusLabel.Text = ResetSyncState(row)
            ? $"Sync state cleared for {row.Upn}."
            : $"Could not clear sync state for {row.Upn}.";
        ChangesApplied = true;
    }

    async void OnAddAccount(object sender, RoutedEventArgs e)
    {
        try
        {
            StatusLabel.Text = "Signing in…";
            var auth = new GraphAuthenticator(
                Path.Combine(_scratchRoot, "msal.cache"),
                () => new WindowInteropHelper(this).Handle);
            var token = await auth.SignInInteractiveAsync();
            var upn = token.Account.Username;

            using (var exists = _appDb.CreateCommand())
            {
                exists.CommandText = "SELECT count(*) FROM mailboxes WHERE upn = @u;";
                exists.Parameters.AddWithValue("@u", upn);
                if (Convert.ToInt64(exists.ExecuteScalar()) > 0)
                {
                    StatusLabel.Text = $"{upn} is already configured.";
                    return;
                }
            }

            var dek = RandomNumberGenerator.GetBytes(32);
            var relative = Path.Combine(".scratch", "mailboxes", upn + ".db");
            Directory.CreateDirectory(Path.Combine(_scratchRoot, "mailboxes"));
            using (var insert = _appDb.CreateCommand())
            {
                insert.CommandText = """
                    INSERT OR IGNORE INTO identities(id, name) VALUES(1, 'Default');
                    INSERT INTO accounts(identity_id, kind, display_name, upn)
                    VALUES(1, 'graph', @u, @u);
                    INSERT INTO mailboxes(account_id, upn, display_name, kind, db_path, dek,
                                          sync_policy, sync_window_months)
                    VALUES(last_insert_rowid(), @u, @u, 'primary', @p, @k, 'WindowedCache', 1);
                    """;
                insert.Parameters.AddWithValue("@u", upn);
                insert.Parameters.AddWithValue("@p", relative);
                insert.Parameters.AddWithValue("@k", dek);
                insert.ExecuteNonQuery();
            }
            ChangesApplied = true;
            LoadRows();
            StatusLabel.Text = $"Added {upn} (WindowedCache, 1 month — adjust above, then Save).";
        }
        catch (MsalServiceException ex) when (IsConsentRequired(ex))
        {
            // The tenant requires an administrator to approve this app before
            // anyone in it can sign in. Nothing is wrong with the sign-in
            // itself, so say so plainly and let the user retry once approved
            // rather than leaving them staring at an unchanged list.
            StatusLabel.Text = "Waiting for admin approval - approve, then click Add account again.";
            MessageBox.Show(
                this,
                "This organisation requires administrator approval before eeeMail can " +
                "access its mail.\n\n" +
                "An administrator must approve the request (the sign-in page links to " +
                "it), after which clicking Add account again will complete setup.\n\n" +
                $"Reported by Microsoft: {ex.Message}",
                "eeeMail - approval required",
                MessageBoxButton.OK,
                MessageBoxImage.Information);
        }
        catch (MsalClientException ex) when (ex.ErrorCode == "authentication_canceled")
        {
            StatusLabel.Text = "Sign-in cancelled.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Add account failed: {ex.Message}";
            MessageBox.Show(this, ex.ToString(), "eeeMail - add account failed",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    /// <summary>
    /// True when Entra is asking for tenant admin consent rather than reporting
    /// a real failure. AADSTS65001 is "user or admin has not consented";
    /// AADSTS90094 is "admin consent required"; the interaction_required family
    /// covers the same case surfaced through the broker.
    /// </summary>
    static bool IsConsentRequired(MsalServiceException ex) =>
        ex.Message.Contains("AADSTS65001", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("AADSTS90094", StringComparison.OrdinalIgnoreCase) ||
        ex.Message.Contains("admin", StringComparison.OrdinalIgnoreCase) &&
            ex.Message.Contains("consent", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Opens the shared-mailbox picker. It needs a Graph client per account,
    /// which the main window owns (it holds the token cache and the window
    /// handle for interactive sign-in), so that is passed in as a factory.
    /// </summary>
    void OnAddShared(object sender, RoutedEventArgs e)
    {
        if (GraphForAccount is null)
        {
            StatusLabel.Text = "Shared mailboxes need an authenticated account; add an account first.";
            return;
        }
        var picker = new SharedMailboxWindow(_appDb, _repoRoot, GraphForAccount) { Owner = this };
        picker.ShowDialog();
        if (picker.MailboxesAdded)
        {
            ChangesApplied = true;
            LoadRows();
        }
    }

    void OnRemoveShared(object sender, RoutedEventArgs e)
    {
        if (MailboxGrid.SelectedItem is not MailboxConfig row)
        {
            StatusLabel.Text = "Select a shared mailbox to remove.";
            return;
        }
        if (row.Kind != "shared")
        {
            StatusLabel.Text = "Only shared mailboxes can be removed here — a primary mailbox is its account.";
            return;
        }
        // Deleting the database discards mail that only exists locally if the
        // server copy has since been purged, so make that explicit.
        var confirm = MessageBox.Show(
            this,
            $"Remove {row.Upn} and delete its local database ({row.DbSize})?\n\n" +
            "The mailbox on the server is not touched; only this machine's copy is deleted.",
            "eeeMail - remove shared mailbox",
            MessageBoxButton.OKCancel, MessageBoxImage.Warning);
        if (confirm != MessageBoxResult.OK) return;

        if (MailboxRegistry.RemoveShared(_appDb, row.Id, _repoRoot))
        {
            ChangesApplied = true;
            StatusLabel.Text = $"Removed {row.Upn}.";
            LoadRows();
        }
        else
        {
            StatusLabel.Text = $"Could not remove {row.Upn}.";
        }
    }

    /// <summary>
    /// Turns mailbox discovery on for an account, which needs consent for two
    /// extra read scopes. Kept opt-in and per-account: consumer accounts have no
    /// directory to search, so for them the prompt would buy nothing.
    /// </summary>
    async void OnEnableDiscovery(object sender, RoutedEventArgs e)
    {
        if (MailboxGrid.SelectedItem is not MailboxConfig row)
        {
            StatusLabel.Text = "Select an account to enable discovery for.";
            return;
        }
        if (row.Kind != "primary")
        {
            StatusLabel.Text = "Discovery is granted to an account, not to a shared mailbox.";
            return;
        }
        if (SignInWithDiscovery is null) return;

        var confirm = MessageBox.Show(
            this,
            $"Sign in to {row.Upn} again to allow eeeMail to look up mailboxes?\n\n" +
            "This grants two read-only directory permissions (People.Read and " +
            "User.ReadBasic.All) so shared mailboxes can be listed and searched " +
            "by name.\n\n" +
            "It does not grant access to anyone's mail: opening a mailbox still " +
            "depends on the permissions you hold in Exchange.",
            "eeeMail - allow mailbox discovery",
            MessageBoxButton.OKCancel, MessageBoxImage.Question);
        if (confirm != MessageBoxResult.OK) return;

        StatusLabel.Text = $"Signing in to {row.Upn}…";
        try
        {
            var granted = await SignInWithDiscovery(row.Upn);
            // Trust what came back, not what was asked for: a tenant can consent
            // to part of a request, and claiming discovery works when it does not
            // would just produce empty lists with no explanation.
            if (granted)
            {
                MailboxRegistry.SetDiscoveryEnabled(_appDb, row.Upn, true);
                ChangesApplied = true;
                StatusLabel.Text = $"Discovery enabled for {row.Upn}.";
                LoadRows();
            }
            else
            {
                StatusLabel.Text =
                    $"{row.Upn}: the directory permissions were not granted — " +
                    "an administrator may need to approve them. You can still add " +
                    "shared mailboxes by typing their address.";
            }
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Sign-in failed: {ex.Message}";
        }
    }

    void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
