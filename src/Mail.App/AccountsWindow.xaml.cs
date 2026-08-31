using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Windows;
using System.Windows.Interop;
using Mail.Storage.Database;
using Mail.Sync.Auth;
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
    }

    readonly SqliteConnection _appDb;
    readonly string _repoRoot;
    readonly string _scratchRoot;
    List<MailboxConfig> _rows = [];

    public bool ChangesApplied { get; private set; }

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
        using var cmd = _appDb.CreateCommand();
        cmd.CommandText = """
            SELECT id, upn, kind, coalesce(sync_policy,'MirrorServer'),
                   sync_window_months, enabled, db_path, dek
            FROM mailboxes ORDER BY position, id;
            """;
        using var reader = cmd.ExecuteReader();
        while (reader.Read())
        {
            var dbPath = reader.GetString(6);
            var resolved = Path.IsPathRooted(dbPath) ? dbPath : Path.Combine(_repoRoot, dbPath);
            var dek = (byte[])reader.GetValue(7);
            var months = reader.IsDBNull(4) ? "" : reader.GetInt32(4).ToString(CultureInfo.InvariantCulture);
            var policy = reader.GetString(3);
            var enabled = reader.GetInt64(5) != 0;
            _rows.Add(new MailboxConfig
            {
                Id = reader.GetInt64(0),
                Enabled = enabled,
                OriginalEnabled = enabled,
                Upn = reader.GetString(1),
                Kind = reader.GetString(2),
                Policy = policy,
                OriginalPolicy = policy,
                MonthsText = months,
                OriginalMonths = months,
                Messages = CountMessages(resolved, dek),
                DbSize = FormatSize(resolved),
                DbPath = dbPath,
                ResolvedDbPath = resolved,
                Dek = dek,
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
        catch (Exception ex)
        {
            StatusLabel.Text = $"Add account failed: {ex.Message}";
        }
    }

    void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}
