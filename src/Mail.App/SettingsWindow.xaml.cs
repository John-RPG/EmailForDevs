using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using Mail.Storage.Database;
using Mail.Storage.Settings;
using Microsoft.Data.Sqlite;

namespace Mail.App;

/// <summary>
/// One settings window for the whole app: pick a level on the left — the
/// application, an account, a mailbox, or a folder — and see every setting that
/// applies there, with its value, where that value came from, and what it costs.
///
/// The list is generated from <see cref="SettingsCatalog"/> rather than
/// hand-built, so a setting cannot exist in the store without appearing here,
/// and cannot be offered at a level the resolver would ignore.
/// </summary>
public partial class SettingsWindow : Window
{
    /// <summary>A node in the left-hand tree, carrying the target it edits.</summary>
    public sealed class ScopeNode
    {
        public required string Label { get; init; }
        public required SettingTarget Target { get; init; }
        public string Hint { get; init; } = "";
        public ObservableCollection<ScopeNode> Children { get; } = [];
        public bool IsExpanded { get; init; } = true;
        public override string ToString() => Label;
    }

    /// <summary>One editable setting at the selected level.</summary>
    public sealed class SettingRow : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        public required SettingDefinition Definition { get; init; }
        public required SettingsStore Store { get; init; }
        public required SettingTarget Target { get; init; }
        public required SettingTarget[] Chain { get; init; }

        public string Key => Definition.Key;
        public string Name => Definition.Name;
        public string Description => Definition.Description;
        public string? Warning => Definition.Warning;
        public string Category => Definition.Category ?? "General";
        public IReadOnlyList<string>? Choices => Definition.Choices;
        public string KeyText => Definition.Key;

        /// <summary>Distinct per row, so automation can address one Clear button.</summary>
        public string ClearAutomationId => $"clear:{Definition.Key}";
        public string ClearCaption => $"Clear override for {Definition.Name}";

        /// <summary>Caption beside a checkbox, since a bare box reads as unlabelled.</summary>
        public string BoolCaption => BoolValue ? "on" : "off";

        string _value = "";
        public string TextValue
        {
            get => _value;
            set
            {
                if (_value == value) return;
                _value = value;
                Raise(nameof(TextValue));
                Raise(nameof(BoolValue));
                Raise(nameof(BoolCaption));
            }
        }

        public bool BoolValue
        {
            get => bool.TryParse(_value, out var b) && b;
            set => TextValue = value ? "true" : "false";
        }

        /// <summary>True when this level holds its own value rather than inheriting.</summary>
        public bool IsOverridden { get; set; }

        /// <summary>Where the effective value came from, spelled out.</summary>
        public string SourceText { get; set; } = "";

        public Visibility OverriddenVisibility =>
            IsOverridden ? Visibility.Visible : Visibility.Collapsed;
        public Visibility WarningVisibility =>
            string.IsNullOrEmpty(Warning) ? Visibility.Collapsed : Visibility.Visible;
        public Visibility BoolVisibility =>
            Definition.Kind == SettingKind.Bool ? Visibility.Visible : Visibility.Collapsed;
        public Visibility ChoiceVisibility =>
            Definition.Kind == SettingKind.Enum ? Visibility.Visible : Visibility.Collapsed;
        public Visibility TextVisibility =>
            Definition.Kind is SettingKind.Bool or SettingKind.Enum
                ? Visibility.Collapsed : Visibility.Visible;

        public Visibility RiskVisibility =>
            Definition.Risk == SettingRisk.Safe ? Visibility.Collapsed : Visibility.Visible;

        public string RiskLabel => Definition.Risk switch
        {
            SettingRisk.Performance => "performance",
            SettingRisk.Security => "security",
            SettingRisk.Destructive => "destructive",
            _ => "",
        };

        public Brush RiskBrush => Definition.Risk switch
        {
            SettingRisk.Performance => new SolidColorBrush(Color.FromRgb(0xB8, 0x6E, 0x00)),
            SettingRisk.Security => new SolidColorBrush(Color.FromRgb(0xA0, 0x40, 0x00)),
            SettingRisk.Destructive => new SolidColorBrush(Color.FromRgb(0xB0, 0x00, 0x00)),
            _ => Brushes.Transparent,
        };

        public void Refresh()
        {
            var resolved = Store.Resolve(Definition, Chain);
            TextValue = resolved.Value;
            IsOverridden = Store.TryGet(Definition.Key, Target, out _);
            SourceText = IsOverridden
                ? "Set at this level."
                : resolved.IsDefault
                    ? $"Not set anywhere — using the built-in default ({Definition.Default})."
                    : $"Inherited from {resolved.Source}.";
            Raise(nameof(IsOverridden));
            Raise(nameof(OverriddenVisibility));
            Raise(nameof(SourceText));
        }

        void Raise(string property) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
    }

    readonly SqliteConnection _appDb;
    readonly SettingsStore _store;
    readonly ObservableCollection<SettingRow> _rows = [];
    ScopeNode? _selected;
    bool _loading;

    public bool ChangesApplied { get; private set; }

    public SettingsWindow(SqliteConnection appDb)
    {
        InitializeComponent();
        _appDb = appDb;
        _store = new SettingsStore(appDb);

        var view = CollectionViewSource.GetDefaultView(_rows);
        view.GroupDescriptions.Add(new PropertyGroupDescription(nameof(SettingRow.Category)));
        SettingsList.ItemsSource = view;

        Loaded += (_, _) => BuildScopeTree();
    }

    /// <summary>
    /// Builds the level tree from the registry: application, then each account
    /// with its mailboxes, then each mailbox's folders. Folders are read from
    /// the mailbox database, so a mailbox not yet synced simply has none.
    /// </summary>
    void BuildScopeTree()
    {
        ScopeTree.Items.Clear();
        var application = new ScopeNode
        {
            Label = "Application",
            Target = SettingTarget.Application,
            Hint = "Defaults for everything, unless a narrower level overrides them.",
        };

        var entries = MailboxRegistry.List(_appDb);
        foreach (var account in entries.GroupBy(e => e.AccountId))
        {
            var primary = account.FirstOrDefault(e => e.Kind == "primary") ?? account.First();
            var accountNode = new ScopeNode
            {
                Label = primary.AccountUpn,
                Target = SettingTarget.Account(account.Key),
                Hint = $"Applies to every mailbox reached with {primary.AccountUpn}.",
            };

            foreach (var mailbox in account)
            {
                var mailboxNode = new ScopeNode
                {
                    Label = mailbox.Kind == "primary"
                        ? $"{mailbox.Upn} (this mailbox)"
                        : $"{mailbox.DisplayName} — shared",
                    Target = SettingTarget.Mailbox(mailbox.Id),
                    Hint = $"Applies to {mailbox.Upn} and its folders.",
                    IsExpanded = false,
                };

                foreach (var (folderId, folderName) in FoldersFor(mailbox))
                    mailboxNode.Children.Add(new ScopeNode
                    {
                        Label = folderName,
                        Target = SettingTarget.Folder(mailbox.Id, folderId),
                        Hint = $"Applies to {folderName} in {mailbox.Upn} only.",
                    });

                accountNode.Children.Add(mailboxNode);
            }
            application.Children.Add(accountNode);
        }

        ScopeTree.Items.Add(application);
        SelectNode(application);
    }

    /// <summary>Folder ids and paths, for the folder level of the tree.</summary>
    static List<(long Id, string Path)> FoldersFor(MailboxRegistry.MailboxEntry mailbox)
    {
        var result = new List<(long, string)>();
        try
        {
            var path = Path.IsPathRooted(mailbox.DbPath)
                ? mailbox.DbPath
                : Path.GetFullPath(mailbox.DbPath);
            if (!File.Exists(path)) return result;

            using var db = MailboxDatabase.Open(path, mailbox.Dek);
            using var cmd = db.CreateCommand();
            cmd.CommandText = "SELECT id, name FROM folders ORDER BY name;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                result.Add((reader.GetInt64(0), reader.GetString(1)));
        }
        catch (Exception)
        {
            // A mailbox whose database is missing or locked contributes no
            // folders; the mailbox level itself still works.
        }
        return result;
    }

    void SelectNode(ScopeNode node)
    {
        _selected = node;
        ScopeHeader.Text = node.Label;
        ScopeHint.Text = node.Hint;
        LoadSettings();
    }

    void OnScopeChanged(object sender, RoutedPropertyChangedEventArgs<object> e)
    {
        if (e.NewValue is ScopeNode node) SelectNode(node);
    }

    void LoadSettings()
    {
        if (_selected is null) return;
        _loading = true;
        _rows.Clear();

        var chain = ChainFor(_selected.Target);
        foreach (var definition in SettingsCatalog.All.Where(s => s.AppliesTo(_selected.Target.Scope)))
        {
            var row = new SettingRow
            {
                Definition = definition,
                Store = _store,
                Target = _selected.Target,
                Chain = chain,
            };
            row.Refresh();
            _rows.Add(row);
        }

        _loading = false;
        ApplyFilter();
        var overridden = _rows.Count(r => r.IsOverridden);
        StatusLabel.Text =
            $"{_rows.Count:N0} setting(s) apply here; {overridden:N0} set at this level.";
    }

    /// <summary>
    /// The resolution chain for a target: itself, then each broader level it
    /// belongs to. Built here because only the UI knows which mailbox and
    /// account a folder sits under.
    /// </summary>
    SettingTarget[] ChainFor(SettingTarget target)
    {
        var chain = new List<SettingTarget> { target };
        var entries = MailboxRegistry.List(_appDb);

        if (target.Scope == SettingScope.Folder && target.Key is string folderKey)
        {
            var mailboxId = long.Parse(folderKey.Split(':')[0]);
            chain.Add(SettingTarget.Mailbox(mailboxId));
            if (entries.FirstOrDefault(e => e.Id == mailboxId) is { } owner)
                chain.Add(SettingTarget.Account(owner.AccountId));
        }
        else if (target.Scope == SettingScope.Mailbox && target.Key is string mailboxKey)
        {
            var mailboxId = long.Parse(mailboxKey);
            if (entries.FirstOrDefault(e => e.Id == mailboxId) is { } owner)
                chain.Add(SettingTarget.Account(owner.AccountId));
        }

        if (target.Scope != SettingScope.Application)
            chain.Add(SettingTarget.Application);
        return [.. chain];
    }

    void OnValueChanged(object sender, RoutedEventArgs e)
    {
        if (_loading || sender is not FrameworkElement element || element.Tag is not string key) return;
        var row = _rows.FirstOrDefault(r => r.Key == key);
        if (row is null || _selected is null) return;

        // Refuse what cannot be stored, while the user is still looking at the
        // field. An unparseable value would otherwise be saved and then silently
        // ignored on every read, leaving the setting looking set but inert.
        if (!row.Definition.IsValid(row.TextValue, out var error))
        {
            StatusLabel.Text = $"{row.Name}: {error} Value not saved.";
            row.Refresh();   // put the stored value back in the editor
            return;
        }

        // Writing on edit rather than behind a Save button keeps the "set here"
        // marker honest: it always reflects the store, never pending intent.
        _store.Set(key, _selected.Target, row.TextValue);
        row.Refresh();
        ChangesApplied = true;
        StatusLabel.Text = $"{row.Name} set to \"{row.TextValue}\" at {_selected.Target.Scope} level.";

        if (row.Definition.Risk is SettingRisk.Security or SettingRisk.Destructive &&
            row.TextValue is not ("false" or "never"))
            StatusLabel.Text += "  ⚠ " + row.Warning;
    }

    void OnClearOverride(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement element || element.Tag is not string key) return;
        var row = _rows.FirstOrDefault(r => r.Key == key);
        if (row is null || _selected is null) return;

        _store.Clear(key, _selected.Target);
        row.Refresh();
        ChangesApplied = true;
        StatusLabel.Text = $"{row.Name} now inherits: {row.SourceText}";
    }

    void OnFilterChanged(object sender, RoutedEventArgs e) => ApplyFilter();
    void OnFilterChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    void ApplyFilter()
    {
        var view = CollectionViewSource.GetDefaultView(_rows);
        var term = FilterBox.Text.Trim();
        var onlyOverridden = OnlyOverridden.IsChecked == true;

        view.Filter = o =>
        {
            if (o is not SettingRow row) return false;
            if (onlyOverridden && !row.IsOverridden) return false;
            if (term.Length == 0) return true;
            return row.Name.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Key.Contains(term, StringComparison.OrdinalIgnoreCase)
                || row.Description.Contains(term, StringComparison.OrdinalIgnoreCase);
        };
        view.Refresh();
    }

    void OnClose(object sender, RoutedEventArgs e) => Close();
}
