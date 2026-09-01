using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows;
using System.Windows.Media;

namespace Mail.App;

/// <summary>
/// A row in the folder tree: mailbox root, favourites group, or folder. Counts
/// are rendered in their own right-aligned column, so names truncate rather
/// than pushing the numbers out of view.
/// </summary>
public sealed class FolderNodeViewModel : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<FolderNodeViewModel> Children { get; } = [];

    /// <summary>Identifies the underlying folder; null for group headers.</summary>
    public object? Mailbox { get; init; }
    public long FolderId { get; init; }
    public string? ServerId { get; init; }
    public string? SpecialUse { get; init; }
    public bool IsFavouriteEntry { get; init; }
    public bool IsGroupHeader { get; init; }

    string _name = "";
    public string Name
    {
        get => _name;
        set { _name = value; Raise(nameof(Name)); }
    }

    string _counts = "";
    public string Counts
    {
        get => _counts;
        set { _counts = value; Raise(nameof(Counts)); }
    }

    bool _hasUnread;
    public bool HasUnread
    {
        get => _hasUnread;
        set
        {
            _hasUnread = value;
            Raise(nameof(HasUnread));
            Raise(nameof(Weight));
            Raise(nameof(CountBrush));
        }
    }

    bool _isExpanded = true;
    public bool IsExpanded
    {
        get => _isExpanded;
        set { _isExpanded = value; Raise(nameof(IsExpanded)); }
    }

    Visibility _visibility = Visibility.Visible;
    public Visibility Visibility
    {
        get => _visibility;
        set { _visibility = value; Raise(nameof(Visibility)); }
    }

    /// <summary>Newest received_at in the folder, for the "hide quiet folders" filter.</summary>
    public DateTimeOffset? LastActivity { get; set; }

    public FontWeight Weight => HasUnread ? FontWeights.Bold : FontWeights.Normal;

    public Brush CountBrush => HasUnread ? Brushes.Black : Brushes.Gray;

    /// <summary>Spells out what the count column is showing, since a bare
    /// "1,203/26,921" is meaningless without context.</summary>
    public string Tooltip
    {
        get
        {
            if (IsGroupHeader || Mailbox is null) return Name;
            var parts = new List<string> { Name };
            if (Counts.Contains('/'))
                parts.Add("synced/total, then unread local/server");
            else if (Counts.Length > 0)
                parts.Add($"{Counts} unread");
            if (LastActivity is { } last)
                parts.Add($"newest: {last.ToLocalTime():yyyy-MM-dd HH:mm:ss}");
            return string.Join("\n", parts);
        }
    }

    public string Icon => IsGroupHeader
        ? "★"                               // star: favourites group
        : Mailbox is null
            ? "\U0001F464"                       // bust: mailbox root
            : SpecialUse switch
            {
                "inbox" => "\U0001F4E5",         // inbox tray
                "sent" => "\U0001F4E4",          // outbox tray
                "drafts" => "\U0001F4DD",        // memo
                "trash" => "\U0001F5D1",         // wastebasket
                "junk" => "⚠",              // warning
                "archive" => "\U0001F4E6",       // package
                "outbox" => "\U0001F4EE",        // postbox
                _ => "\U0001F4C1",               // folder
            };

    void Raise(string property) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(property));
}
