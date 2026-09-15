using System.Windows;

namespace Mail.App;

/// <summary>
/// Asks which kind of account before opening a browser at anyone's sign-in page.
///
/// Microsoft is the only provider that works today, so this could have stayed a
/// straight jump to the Microsoft flow — but that gave no clue what eeeMail
/// supports, and dropped the reader into a Microsoft login whether or not that
/// was the account they meant. Listing IMAP as present-but-unbuilt answers the
/// question the button used to leave open.
/// </summary>
public partial class AddAccountWindow : Window
{
    /// <summary>The chosen provider, or null when the user cancelled.</summary>
    public AccountProvider? Provider { get; private set; }

    public enum AccountProvider { Microsoft }

    public AddAccountWindow()
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Themes.ThemeManager.ApplyTitleBar(this);
    }

    void OnContinue(object sender, RoutedEventArgs e)
    {
        Provider = AccountProvider.Microsoft;
        Close();
    }

    void OnCancel(object sender, RoutedEventArgs e) => Close();
}
