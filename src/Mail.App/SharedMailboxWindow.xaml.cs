using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mail.Storage.Database;
using Mail.Sync.Graph;
using Microsoft.Data.Sqlite;
using Microsoft.Graph;

namespace Mail.App;

/// <summary>
/// Picks shared mailboxes to mirror. Discovery proposes candidates from the
/// relevance graph and the directory, but nothing is added until the server
/// confirms the mailbox actually opens — permission belongs to Exchange, not to
/// this list, so a name appearing here is never a claim of access.
/// </summary>
public partial class SharedMailboxWindow : Window
{
    public sealed class CandidateRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;
        public string Address { get; init; } = "";
        public string DisplayName { get; init; } = "";
        public string Source { get; init; } = "";

        string _access = "not checked";
        public string Access
        {
            get => _access;
            set
            {
                _access = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Access)));
            }
        }
    }

    readonly SqliteConnection _appDb;
    readonly string _repoRoot;
    readonly Func<string, Task<GraphServiceClient>> _graphForAccount;
    readonly ObservableCollection<CandidateRow> _candidates = [];
    List<MailboxRegistry.MailboxEntry> _accounts = [];

    /// <summary>True when at least one mailbox was added, so the caller reloads.</summary>
    public bool MailboxesAdded { get; private set; }

    public SharedMailboxWindow(
        SqliteConnection appDb, string repoRoot,
        Func<string, Task<GraphServiceClient>> graphForAccount)
    {
        InitializeComponent();
        _appDb = appDb;
        _repoRoot = repoRoot;
        _graphForAccount = graphForAccount;
        CandidateGrid.ItemsSource = _candidates;
        Loaded += (_, _) => LoadAccounts();
    }

    void LoadAccounts()
    {
        // Only primary mailboxes can host shared ones: a shared mailbox is
        // reached with its owner's token and has no credentials to lend on.
        _accounts = [.. MailboxRegistry.List(_appDb).Where(m => m.Kind == "primary")];
        AccountCombo.ItemsSource = _accounts.Select(a => a.Upn).ToList();
        if (_accounts.Count > 0) AccountCombo.SelectedIndex = 0;
    }

    string? SelectedAccount => AccountCombo.SelectedItem as string;

    async void OnAccountChanged(object sender, SelectionChangedEventArgs e)
    {
        if (IsLoaded) await DiscoverAsync();
    }

    async void OnRefresh(object sender, RoutedEventArgs e) => await DiscoverAsync();

    async Task DiscoverAsync()
    {
        if (SelectedAccount is not string account) return;
        _candidates.Clear();
        StatusLabel.Text = $"Looking for mailboxes reachable from {account}…";
        try
        {
            var graph = await _graphForAccount(account);
            var discovery = new SharedMailboxDiscovery(graph);
            var found = await discovery.DiscoverAsync(account);
            foreach (var candidate in found)
                _candidates.Add(new CandidateRow
                {
                    Address = candidate.Address,
                    DisplayName = candidate.DisplayName,
                    Source = candidate.Source,
                });
            // An empty list means different things depending on why: without the
            // discovery scopes we never even asked, and saying "nothing found"
            // would be misleading.
            StatusLabel.Text = found.Count > 0
                ? $"{found.Count:N0} candidate(s). Select one to check access."
                : MailboxRegistry.IsDiscoveryEnabled(_appDb, account)
                    ? "Nothing found automatically — search the directory or type an address below."
                    : "Automatic lookup is off for this account (Accounts → Allow discovery). " +
                      "You can still add a mailbox by typing its address below.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Discovery failed: {ex.Message}";
        }
    }

    async void OnSearchKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key != Key.Enter || SelectedAccount is not string account) return;
        var term = SearchBox.Text.Trim();
        if (term.Length == 0) { await DiscoverAsync(); return; }
        if (!MailboxRegistry.IsDiscoveryEnabled(_appDb, account))
        {
            StatusLabel.Text =
                "Directory search needs discovery enabled for this account " +
                "(Accounts → Allow discovery). Type the mailbox address below instead.";
            return;
        }

        StatusLabel.Text = $"Searching the directory for \"{term}\"…";
        try
        {
            var graph = await _graphForAccount(account);
            var results = await new SharedMailboxDiscovery(graph).SearchDirectoryAsync(term, account);
            foreach (var candidate in results)
            {
                if (_candidates.Any(c => string.Equals(c.Address, candidate.Address,
                        StringComparison.OrdinalIgnoreCase))) continue;
                _candidates.Add(new CandidateRow
                {
                    Address = candidate.Address,
                    DisplayName = candidate.DisplayName,
                    Source = candidate.Source,
                });
            }
            StatusLabel.Text = results.Count == 0
                ? $"No directory matches for \"{term}\" (the tenant may not allow directory search)."
                : $"{results.Count:N0} directory match(es) added to the list.";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Directory search failed: {ex.Message}";
        }
    }

    void OnCandidateSelected(object sender, SelectionChangedEventArgs e)
    {
        if (CandidateGrid.SelectedItem is CandidateRow row)
        {
            AddressBox.Text = row.Address;
            AddButton.IsEnabled = false;   // access must be re-confirmed per address
        }
    }

    async void OnCheckAccess(object sender, RoutedEventArgs e) => await CheckAccessAsync();

    async Task CheckAccessAsync()
    {
        var address = AddressBox.Text.Trim();
        if (address.Length == 0 || SelectedAccount is not string account)
        {
            StatusLabel.Text = "Enter a mailbox address first.";
            return;
        }
        if (string.Equals(address, account, StringComparison.OrdinalIgnoreCase))
        {
            StatusLabel.Text = "That is the account's own mailbox — it is already synced.";
            return;
        }

        CheckButton.IsEnabled = false;
        StatusLabel.Text = $"Checking access to {address}…";
        try
        {
            var graph = await _graphForAccount(account);
            var result = await new SharedMailboxDiscovery(graph).TestAccessAsync(address);
            var row = _candidates.FirstOrDefault(c =>
                string.Equals(c.Address, address, StringComparison.OrdinalIgnoreCase));
            var summary = result.CanOpen
                ? $"OK — {result.TotalItems:N0} items, {result.UnreadItems:N0} unread"
                : result.Detail;
            if (row is not null) row.Access = summary;

            AddButton.IsEnabled = result.CanOpen;
            StatusLabel.Text = result.CanOpen
                ? $"{address}: inbox holds {result.TotalItems:N0} items. Add it to start mirroring."
                : $"{address}: {result.Detail}";
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Access check failed: {ex.Message}";
        }
        finally
        {
            CheckButton.IsEnabled = true;
        }
    }

    void OnAdd(object sender, RoutedEventArgs e)
    {
        var address = AddressBox.Text.Trim();
        if (SelectedAccount is not string account) return;
        var owner = _accounts.FirstOrDefault(a =>
            string.Equals(a.Upn, account, StringComparison.OrdinalIgnoreCase));
        if (owner is null) return;

        if (MailboxRegistry.Exists(_appDb, owner.AccountId, address))
        {
            StatusLabel.Text = $"{address} is already registered under {account}.";
            return;
        }

        var name = _candidates.FirstOrDefault(c =>
            string.Equals(c.Address, address, StringComparison.OrdinalIgnoreCase))?.DisplayName ?? address;
        MailboxRegistry.AddShared(
            _appDb, owner.AccountId, owner.Upn, address, name,
            Path.Combine(_repoRoot, ".scratch", "mailboxes"));

        MailboxesAdded = true;
        AddButton.IsEnabled = false;
        StatusLabel.Text = $"Added {address}. It will populate on the next sync.";
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();
}
