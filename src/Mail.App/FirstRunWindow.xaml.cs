using System.IO;
using System.Windows;
using Mail.Core.Storage;

namespace Mail.App;

/// <summary>
/// Asked once, before anything is created: where the profile and the mail
/// databases should live.
///
/// It runs before the profile exists rather than after, because these are the
/// two decisions that are painful to revisit — changing them later does not move
/// anything, it just points the app somewhere new and leaves the old data
/// behind. Everything else a first-time user might set can wait for Settings.
/// </summary>
public partial class FirstRunWindow : Window
{
    /// <summary>Where the profile and app database go.</summary>
    public string DataDirectory { get; private set; } = "";

    /// <summary>Where mailbox databases go. May sit outside the data directory.</summary>
    public string MailDirectory { get; private set; } = "";

    /// <summary>Whether to check for updates on launch.</summary>
    public bool CheckForUpdates { get; private set; } = true;

    /// <summary>False when the user closed the window rather than continuing.</summary>
    public bool Completed { get; private set; }

    readonly string _defaultData;

    public FirstRunWindow(string defaultDataDirectory)
    {
        InitializeComponent();
        // Its own HWND, so it needs the dark title bar applied separately.
        SourceInitialized += (_, _) => Themes.ThemeManager.ApplyTitleBar(this);
        _defaultData = defaultDataDirectory;
        DataDirBox.Text = defaultDataDirectory;
        MailDirBox.Text = DataLocation.MailboxDirectory(defaultDataDirectory);
    }

    void OnUseDefaults(object sender, RoutedEventArgs e)
    {
        DataDirBox.Text = _defaultData;
        MailDirBox.Text = DataLocation.MailboxDirectory(_defaultData);
        UpdatesBox.IsChecked = true;
    }

    /// <summary>
    /// Looks for a profile left by a previous install, on request.
    ///
    /// Behind a button on purpose. Searching someone's disk for their mail
    /// without being asked is not something an app should do quietly at startup,
    /// and the result here only fills in a box — the user still confirms it.
    /// </summary>
    void OnFindExisting(object sender, RoutedEventArgs e)
    {
        var found = DataLocation.FindExistingProfiles(AppContext.BaseDirectory).ToList();
        if (found.Count == 0)
        {
            Problem.Text = "No existing profile found in the usual places. " +
                           "Use Browse if it is somewhere else.";
            return;
        }

        // The first is the most likely: the search returns them in the order the
        // app itself would resolve them.
        DataDirBox.Text = found[0];
        MailDirBox.Text = DataLocation.MailboxDirectory(found[0]);
        Problem.Text = "";
        MessageBox.Show(this,
            found.Count == 1
                ? $"Found a profile at:\n\n{found[0]}\n\nThe locations have been filled in."
                : $"Found {found.Count} profiles. Using:\n\n{found[0]}\n\nOthers:\n" +
                  string.Join("\n", found.Skip(1)),
            "Existing profile", MessageBoxButton.OK, MessageBoxImage.Information);
    }

    void OnBrowseData(object sender, RoutedEventArgs e) => Browse(DataDirBox, "Profile location");

    void OnBrowseMail(object sender, RoutedEventArgs e) => Browse(MailDirBox, "Mail database location");

    void Browse(System.Windows.Controls.TextBox box, string title)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog
        {
            Title = title,
            // Start where the box points, when that place exists.
            InitialDirectory = Directory.Exists(box.Text) ? box.Text : _defaultData,
        };
        if (dialog.ShowDialog(this) == true) box.Text = dialog.FolderName;
    }

    /// <summary>
    /// Validates as the user types. Both paths must be absolute and creatable;
    /// finding that out after the profile has been half-built would be worse.
    /// </summary>
    void OnPathChanged(object sender, System.Windows.Controls.TextChangedEventArgs e)
    {
        // TextChanged fires during InitializeComponent, before the rest exists.
        if (ContinueButton is null || Problem is null) return;

        var problem = Validate(DataDirBox.Text, "Profile location")
                      ?? Validate(MailDirBox.Text, "Mail database location");
        Problem.Text = problem ?? "";
        ContinueButton.IsEnabled = problem is null;
    }

    static string? Validate(string path, string label)
    {
        if (string.IsNullOrWhiteSpace(path)) return $"{label}: enter a folder.";
        try
        {
            if (!Path.IsPathFullyQualified(path))
                return $"{label}: needs a full path, such as D:\\Mail.";
            // Reject characters the filesystem will refuse, rather than failing
            // later inside a half-finished profile.
            _ = Path.GetFullPath(path);
        }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return $"{label}: {ex.Message}";
        }
        return null;
    }

    void OnContinue(object sender, RoutedEventArgs e)
    {
        var data = Path.GetFullPath(DataDirBox.Text.Trim());
        var mail = Path.GetFullPath(MailDirBox.Text.Trim());
        try
        {
            // Create both now, so a permission problem surfaces here rather than
            // after the user has signed in to an account.
            Directory.CreateDirectory(data);
            Directory.CreateDirectory(mail);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Problem.Text = $"Could not create that folder: {ex.Message}";
            return;
        }

        DataDirectory = data;
        MailDirectory = mail;
        CheckForUpdates = UpdatesBox.IsChecked == true;
        Completed = true;
        Close();
    }
}
