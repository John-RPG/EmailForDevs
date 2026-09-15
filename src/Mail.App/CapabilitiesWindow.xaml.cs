using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Mail.Storage.Database;
using Mail.Sync.Auth;
using Microsoft.Data.Sqlite;

namespace Mail.App;

/// <summary>
/// Per-account capability settings: what the app may do, what each permission
/// costs, and what is lost by refusing it.
///
/// Everything is shown. These are delegated permissions, so none of them can
/// exceed what the signed-in user could already do themselves — hiding one
/// would obscure a decision without removing the underlying ability. Risk is
/// communicated instead of concealed: broad scopes say why they are broad, and
/// destructive capabilities require a typed confirmation before they arm.
/// </summary>
public partial class CapabilitiesWindow : Window
{
    public sealed class CapabilityRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public required AccountCapability Capability { get; init; }
        public string Id => Capability.Id;
        public string Name => Capability.Name;
        public string Summary => Capability.Summary;
        public string WithoutText => $"Without it: {Capability.WithoutIt}";
        public string? Warning => Capability.Warning;
        public bool CanToggle => !Capability.Required;

        bool _enabled;
        public bool Enabled
        {
            get => _enabled;
            set
            {
                _enabled = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Enabled)));
            }
        }

        /// <summary>Whether the tenant has actually granted this, as opposed to it being asked for.</summary>
        public bool Granted { get; set; }

        /// <summary>
        /// Asked for, but the grant has not come back — typically waiting on an
        /// administrator. Kept apart from Granted so the feature stays off while
        /// the user's choice stays on.
        /// </summary>
        public bool Pending { get; set; }

        public string ScopeText => Capability.Scopes.Count == 0
            ? "no additional permission — gated because it is destructive"
            : string.Join("  ", Capability.ShortScopes);

        public Visibility ScopeVisibility => Visibility.Visible;
        public Visibility WarningVisibility =>
            string.IsNullOrEmpty(Warning) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility AdminVisibility =>
            Capability.LikelyNeedsAdmin ? Visibility.Visible : Visibility.Collapsed;
        public Visibility GrantedVisibility =>
            Granted ? Visibility.Visible : Visibility.Collapsed;
        public Visibility PendingVisibility =>
            Pending && !Granted ? Visibility.Visible : Visibility.Collapsed;
        public Visibility RiskVisibility =>
            Capability.Risk is CapabilityRisk.Normal ? Visibility.Collapsed : Visibility.Visible;

        public string RiskLabel => Capability.Risk switch
        {
            CapabilityRisk.Essential => "required",
            CapabilityRisk.Broad => "broad",
            CapabilityRisk.Dangerous => "dangerous",
            _ => "",
        };

        /// <summary>
        /// Accessible name: the capability, whether it is on, and what it costs.
        /// A class reports only its type name otherwise.
        /// </summary>
        public override string ToString() =>
            $"{Name}, {(Enabled ? "enabled" : "disabled")}" +
            (Capability.Risk == CapabilityRisk.Normal ? "" : $", {RiskLabel}");

        public Brush RiskBrush => Capability.Risk switch
        {
            CapabilityRisk.Essential => MainWindow.Themed("Text.Secondary", Brushes.DimGray),
            CapabilityRisk.Broad => MainWindow.Themed("Risk.Performance", Brushes.DarkOrange),
            CapabilityRisk.Dangerous => MainWindow.Themed("Risk.Destructive", Brushes.Firebrick),
            _ => Brushes.Transparent,
        };
    }

    readonly SqliteConnection _appDb;
    readonly string? _initialAccount;
    readonly ObservableCollection<CapabilityRow> _rows = [];
    List<MailboxRegistry.MailboxEntry> _accounts = [];

    /// <summary>Requests sign-in with a capability set; returns what was granted.</summary>
    public Func<string, IReadOnlyList<string>, Task<IReadOnlyList<string>>>? ApplyCapabilities { get; set; }

    public bool ChangesApplied { get; private set; }

    /// <param name="initialAccount">
    /// Account to open on. Defaulting to the first silently changes the subject:
    /// the caller picked an account, and answering about a different one is
    /// worse than showing nothing.
    /// </param>
    public CapabilitiesWindow(SqliteConnection appDb, string? initialAccount = null)
    {
        InitializeComponent();
        _appDb = appDb;
        _initialAccount = initialAccount;
        CapabilityList.ItemsSource = _rows;
        Loaded += (_, _) => LoadAccounts();
    }

    void LoadAccounts()
    {
        _accounts = [.. MailboxRegistry.List(_appDb).Where(m => m.Kind == "primary")];
        var names = _accounts.Select(a => a.Upn).ToList();
        AccountCombo.ItemsSource = names;
        if (names.Count == 0) return;

        var index = _initialAccount is null
            ? 0
            : names.FindIndex(n => string.Equals(n, _initialAccount, StringComparison.OrdinalIgnoreCase));
        AccountCombo.SelectedIndex = index >= 0 ? index : 0;
        LoadRows();
    }

    string? SelectedAccount => AccountCombo.SelectedItem as string;

    void OnAccountChanged(object sender, SelectionChangedEventArgs e) => LoadRows();

    void LoadRows()
    {
        _rows.Clear();
        if (SelectedAccount is not string account) return;

        var granted = MailboxRegistry.GetCapabilities(_appDb, account);
        // Requests still awaiting approval come back switched on, so a selection
        // survives closing the app while an administrator considers it.
        var pending = MailboxRegistry.GetPendingCapabilities(_appDb, account);
        foreach (var capability in AccountCapability.All)
        {
            var isGranted = capability.Required || granted.Contains(capability.Id);
            var isPending = !isGranted && pending.Contains(capability.Id);
            _rows.Add(new CapabilityRow
            {
                Capability = capability,
                Enabled = isGranted || isPending,
                Granted = isGranted,
                Pending = isPending,
            });
        }

        var optional = _rows.Count(r => r.CanToggle);
        GrantedLabel.Text = $"{granted.Count:N0} of {optional:N0} optional capabilities granted." +
            (pending.Count > 0 ? $" {pending.Count:N0} awaiting approval." : "");
    }

    /// <summary>
    /// Destructive capabilities need more than a click: the checkbox reverts
    /// unless the warning is acknowledged deliberately.
    /// </summary>
    void OnToggled(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox box || box.Tag is not string id) return;
        var row = _rows.FirstOrDefault(r => r.Id == id);
        if (row is null) return;

        if (row.Enabled && row.Capability.IsDangerous)
        {
            var confirm = MessageBox.Show(
                this,
                $"{row.Name}\n\n{row.Warning}\n\nEnable it anyway?",
                "eeeMail — dangerous capability",
                MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No);
            if (confirm != MessageBoxResult.Yes)
            {
                row.Enabled = false;
                return;
            }
        }

        // A capability needing no extra scope (only a local gate) takes effect
        // immediately; the rest wait for the sign-in that grants them.
        if (row.Capability.Scopes.Count == 0 && SelectedAccount is string account)
        {
            MailboxRegistry.SetCapability(_appDb, account, row.Id, row.Enabled);
            row.Granted = row.Enabled;
            ChangesApplied = true;
            StatusLabel.Text = $"{row.Name}: {(row.Enabled ? "enabled" : "disabled")}.";
        }
        else
        {
            StatusLabel.Text = "Click \"Apply and sign in\" to request the changed permissions.";
        }
    }

    async void OnApply(object sender, RoutedEventArgs e)
    {
        if (SelectedAccount is not string account || ApplyCapabilities is null) return;

        var wanted = _rows.Where(r => r.Enabled).Select(r => r.Id).ToList();
        StatusLabel.Text = $"Signing in to {account}…";
        try
        {
            var granted = await ApplyCapabilities(account, wanted);

            // Record what was granted, not what was asked for: a tenant can
            // approve part of a request, and claiming otherwise would leave
            // features that silently do nothing.
            //
            // But a request that was not granted is not the same as one that was
            // refused. Where the tenant sends consent to an administrator, the
            // browser says "request sent" and closes, and nothing comes back —
            // so anything still switched on is kept as pending rather than
            // switched off. Otherwise approval arrives and the user finds every
            // toggle reset, with no sign the first attempt did anything.
            foreach (var row in _rows.Where(r => r.CanToggle))
            {
                var isGranted = granted.Contains(row.Id, StringComparer.OrdinalIgnoreCase);
                if (isGranted)
                {
                    MailboxRegistry.SetCapability(_appDb, account, row.Id, true);
                    row.Granted = true;
                    row.Pending = false;
                    row.Enabled = true;
                }
                else if (row.Enabled)
                {
                    MailboxRegistry.SetCapabilityPending(_appDb, account, row.Id);
                    row.Granted = false;
                    row.Pending = true;
                    // Left on: this is what the user asked for, and it is what
                    // the next Apply re-requests once approval lands.
                }
                else
                {
                    MailboxRegistry.SetCapability(_appDb, account, row.Id, false);
                    row.Granted = false;
                    row.Pending = false;
                }
            }

            var refused = wanted.Where(w =>
                !granted.Contains(w, StringComparer.OrdinalIgnoreCase)).ToList();
            ChangesApplied = true;
            StatusLabel.Text = refused.Count == 0
                ? $"All requested capabilities granted for {account}."
                : $"Granted {granted.Count:N0}. Awaiting approval: " +
                  string.Join(", ", refused.Select(r => AccountCapability.ById(r)?.Name ?? r)) +
                  ". These stay switched on — once an administrator approves the " +
                  "request, press \"Apply and sign in\" again to pick them up. " +
                  "You do not have to choose them again.";
            LoadRows();
        }
        catch (Exception ex)
        {
            StatusLabel.Text = $"Sign-in failed: {ex.Message}";
        }
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();
}
