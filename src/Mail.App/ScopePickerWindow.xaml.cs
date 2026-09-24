using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using Microsoft.Data.Sqlite;

namespace Mail.App;

/// <summary>
/// Picks folders to search, across mailboxes.
///
/// The whole tree is shown rather than the open mailbox alone, because the
/// reason to reach for this is usually "these two folders, in different
/// accounts" — which no single-mailbox scope can express.
/// </summary>
public partial class ScopePickerWindow : Window
{
    /// <summary>The chosen folders, once the dialog is accepted.</summary>
    public List<(string MailboxKey, long FolderId)> Selection { get; private set; } = [];

    readonly ObservableCollection<ScopeNode> _roots = [];

    /// <summary>
    /// One mailbox as the picker needs it: a key to report back, a label to
    /// show, and the database to read folders from. Deliberately not the app's
    /// mailbox handle, which carries keys and sync state a dialog has no
    /// business holding.
    /// </summary>
    public readonly record struct MailboxEntry(string Key, string Label, SqliteConnection Db);

    public ScopePickerWindow(
        IReadOnlyList<MailboxEntry> mailboxes,
        IReadOnlyCollection<(string MailboxKey, long FolderId)> already)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Themes.ThemeManager.ApplyTitleBar(this);

        var preselected = already
            .Select(a => (a.MailboxKey, a.FolderId))
            .ToHashSet();

        foreach (var mailbox in mailboxes)
        {
            var root = new ScopeNode(mailbox.Key, mailbox.Label)
            {
                // A mailbox row is a container: ticking it means every folder
                // under it rather than a folder of its own.
                FolderId = null,
            };
            foreach (var node in ReadFolders(mailbox, preselected))
                root.Children.Add(node);
            root.Attach();
            _roots.Add(root);
        }

        Tree.ItemsSource = _roots;
        UpdateCount();
        foreach (var root in _roots) root.Changed += (_, _) => UpdateCount();
    }

    /// <summary>
    /// Builds one mailbox's folder tree. Ordered by name within each parent so
    /// the picker reads the same way twice.
    /// </summary>
    static List<ScopeNode> ReadFolders(
        MailboxEntry mailbox,
        HashSet<(string, long)> preselected)
    {
        var all = new List<(long Id, long? Parent, string Name)>();
        using (var cmd = mailbox.Db.CreateCommand())
        {
            cmd.CommandText = "SELECT id, parent_id, name FROM folders ORDER BY name;";
            using var reader = cmd.ExecuteReader();
            while (reader.Read())
                all.Add((
                    reader.GetInt64(0),
                    reader.IsDBNull(1) ? null : reader.GetInt64(1),
                    reader.IsDBNull(2) ? "(unnamed)" : reader.GetString(2)));
        }

        var nodes = all.ToDictionary(
            f => f.Id,
            f => new ScopeNode(mailbox.Key, f.Name)
            {
                FolderId = f.Id,
                IsChecked = preselected.Contains((mailbox.Key, f.Id)),
            });

        var roots = new List<ScopeNode>();
        foreach (var (id, parent, _) in all)
        {
            // A folder whose parent is missing is treated as a root rather than
            // dropped: a partial sync should not make folders invisible here.
            if (parent is not null && nodes.TryGetValue(parent.Value, out var parentNode))
                parentNode.Children.Add(nodes[id]);
            else
                roots.Add(nodes[id]);
        }
        return roots;
    }

    void UpdateCount()
    {
        var count = _roots.Sum(r => r.CheckedFolders().Count());
        CountText.Text = count == 0 ? "Nothing selected" : $"{count:N0} folder(s) selected";
        OkButton.IsEnabled = count > 0;
    }

    void OnSelectAll(object sender, RoutedEventArgs e)
    {
        foreach (var root in _roots) root.SetAll(true);
        UpdateCount();
    }

    void OnClear(object sender, RoutedEventArgs e)
    {
        foreach (var root in _roots) root.SetAll(false);
        UpdateCount();
    }

    void OnOk(object sender, RoutedEventArgs e)
    {
        Selection = [.. _roots.SelectMany(r => r.CheckedFolders())];
        DialogResult = true;
    }
}

/// <summary>
/// A tickable row. Ticking a node ticks everything beneath it, which is what
/// "search this folder" means once it has subfolders.
/// </summary>
public sealed class ScopeNode(string mailboxKey, string name) : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>Raised for any change in the subtree, so the dialog can recount.</summary>
    public event EventHandler? Changed;

    public string MailboxKey { get; } = mailboxKey;
    public string Name { get; } = name;

    /// <summary>Null for a mailbox row, which is a container rather than a folder.</summary>
    public long? FolderId { get; init; }

    public ObservableCollection<ScopeNode> Children { get; } = [];

    bool _isChecked;
    public bool IsChecked
    {
        get => _isChecked;
        set
        {
            if (_isChecked == value) return;
            _isChecked = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsChecked)));
            // Cascade down: ticking a parent means its subfolders too, which is
            // what the label promises.
            foreach (var child in Children) child.IsChecked = value;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    /// <summary>Bubbles child changes so the dialog's count stays live.</summary>
    public void Attach()
    {
        foreach (var child in Children)
        {
            child.Attach();
            child.Changed += (_, _) => Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public void SetAll(bool value)
    {
        IsChecked = value;
        foreach (var child in Children) child.SetAll(value);
    }

    /// <summary>
    /// The ticked folders in this subtree. A mailbox row contributes nothing
    /// itself — ticking it has already ticked its folders.
    /// </summary>
    public IEnumerable<(string MailboxKey, long FolderId)> CheckedFolders()
    {
        if (IsChecked && FolderId is not null)
            yield return (MailboxKey, FolderId.Value);
        foreach (var child in Children)
            foreach (var found in child.CheckedFolders())
                yield return found;
    }
}
