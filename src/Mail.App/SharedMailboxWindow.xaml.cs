using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Mail.Storage.Database;
using Mail.Sync.Auth;
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

        /// <summary>
        /// null until known: true when Exchange mapped it or a check succeeded,
        /// false when a check was refused. Unknown is not the same as "no".
        /// </summary>
        public bool? CanOpen { get; set; }

        /// <summary>
        /// What a screen reader announces for the row. Without this the default
        /// ToString() reports the class name, so every row in the list sounds
        /// identical — and automation cannot tell them apart either.
        /// </summary>
        public override string ToString() =>
            $"{DisplayName}, {Address}, {Access}";
    }

    readonly SqliteConnection _appDb;
    readonly string _repoRoot;
    readonly Func<string, Task<GraphServiceClient>> _graphForAccount;
    readonly Func<string, Task<IReadOnlyList<AutodiscoverMailboxes.AlternateMailbox>>>? _mapped;

    /// <summary>Account the caller was working with, preselected on open.</summary>
    readonly string? _initialAccount;
    readonly ObservableCollection<CandidateRow> _candidates = [];
    List<MailboxRegistry.MailboxEntry> _accounts = [];

    /// <summary>True when at least one mailbox was added, so the caller reloads.</summary>
    public bool MailboxesAdded { get; private set; }

    public SharedMailboxWindow(
        SqliteConnection appDb, string repoRoot,
        Func<string, Task<GraphServiceClient>> graphForAccount,
        Func<string, Task<IReadOnlyList<AutodiscoverMailboxes.AlternateMailbox>>>? mappedMailboxes = null,
        string? initialAccount = null)
    {
        InitializeComponent();
        _appDb = appDb;
        _repoRoot = repoRoot;
        _graphForAccount = graphForAccount;
        _mapped = mappedMailboxes;
        _initialAccount = initialAccount;
        CandidateGrid.ItemsSource = _candidates;
        Loaded += async (_, _) =>
        {
            LoadAccounts();
            // SelectionChanged does not fire when the index is set before the
            // window is interactive, so discovery is kicked off explicitly.
            await DiscoverAsync();
        };
    }

    void LoadAccounts()
    {
        // Only primary mailboxes can host shared ones: a shared mailbox is
        // reached with its owner's token and has no credentials to lend on.
        _accounts = [.. MailboxRegistry.List(_appDb).Where(m => m.Kind == "primary")];
        var names = _accounts.Select(a => a.Upn).ToList();
        AccountCombo.ItemsSource = names;
        if (names.Count == 0) return;

        // Open on the account the caller was looking at. Defaulting to the first
        // one silently changes the subject: the user picked an account in the
        // settings tree, and the picker must not quietly answer about a
        // different one.
        var index = _initialAccount is null
            ? 0
            : names.FindIndex(n => string.Equals(n, _initialAccount, StringComparison.OrdinalIgnoreCase));
        AccountCombo.SelectedIndex = index >= 0 ? index : 0;
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
            // Ask Exchange what is actually mapped to this account before
            // falling back to guesswork.
            var mapped = _mapped is null ? [] : await _mapped(account);
            var graph = await _graphForAccount(account);
            var discovery = new SharedMailboxDiscovery(graph);
            // Suggestions are a separate capability from Exchange's mapped list,
            // so a user who granted one and refused the other gets exactly that.
            var suggest = MailboxRegistry.HasCapability(
                _appDb, account, AccountCapability.PeopleLookup.Id);
            var found = await discovery.DiscoverAsync(account, mapped, includeSuggestions: suggest);
            foreach (var candidate in found)
                _candidates.Add(new CandidateRow
                {
                    Address = candidate.Address,
                    DisplayName = candidate.DisplayName,
                    Source = candidate.Source,
                    // Exchange already vouched for a mapped mailbox, so it needs
                    // no further check before being added.
                    Access = candidate.IsMapped ? "mapped by Exchange" : "not checked",
                    CanOpen = candidate.IsMapped ? true : null,
                });
            // Say which of these Exchange actually vouched for. An empty list
            // also means different things: without the discovery scopes we never
            // asked at all, and "nothing found" would be misleading.
            var mappedCount = found.Count(c => c.IsMapped);
            StatusLabel.Text = found.Count > 0
                ? mappedCount > 0
                    ? $"{mappedCount:N0} mailbox(es) mapped to this account by Exchange" +
                      (found.Count > mappedCount
                          ? $", plus {found.Count - mappedCount:N0} suggestion(s) to check."
                          : ". Select one and add it.")
                    : $"{found.Count:N0} suggestion(s) — none mapped, so check access before adding."
                : MailboxRegistry.HasCapability(_appDb, account, AccountCapability.MappedLookup.Id)
                    ? "Nothing found automatically — search the directory or type an address below."
                    : "Mailbox lookup is off for this account (Accounts → Capabilities). " +
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
        if (!MailboxRegistry.HasCapability(_appDb, account, AccountCapability.DirectorySearch.Id))
        {
            StatusLabel.Text =
                "Directory search is off for this account (Accounts → Capabilities → " +
                "Search the company directory). Type the mailbox address below instead.";
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
            // Exchange's own mapping is proof enough; anything else needs a check.
            AddButton.IsEnabled = row.CanOpen == true;
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
            if (row is not null)
            {
                row.CanOpen = result.CanOpen;
                row.Access = result.CanOpen
                    ? $"Yes — {result.TotalItems:N0} items, {result.UnreadItems:N0} unread"
                    : result.Detail;
            }

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
