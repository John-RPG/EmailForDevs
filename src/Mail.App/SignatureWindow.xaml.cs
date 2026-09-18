using System.Windows;

namespace Mail.App;

/// <summary>
/// Write or edit the signature for a sending account, from inside the message
/// being written.
///
/// It exists because the alternative is worse: discovering the signature is
/// missing or wrong while looking at the message that needs it, and having to
/// abandon the message for Settings to fix it.
/// </summary>
public partial class SignatureWindow : Window
{
    /// <summary>The text as the user left it. Trimmed of trailing blank lines.</summary>
    public string SignatureText { get; private set; } = "";

    /// <summary>Whether to store this as the account's signature.</summary>
    public bool Save { get; private set; }

    /// <summary>Whether to add it to the message being written.</summary>
    public bool Insert { get; private set; }

    /// <param name="fromAddress">The sending account, named so it is clear which one this affects.</param>
    /// <param name="existing">The stored signature, or null when there is none yet.</param>
    public SignatureWindow(string fromAddress, string? existing)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Themes.ThemeManager.ApplyTitleBar(this);

        Headline.Text = string.IsNullOrWhiteSpace(fromAddress)
            ? "Signature"
            : $"Signature for {fromAddress}";
        SignatureBox.Text = existing ?? "";

        // Nothing stored yet means there is nothing to save-without-inserting.
        SaveOnlyButton.IsEnabled = true;

        Loaded += (_, _) =>
        {
            SignatureBox.Focus();
            // Caret at the end: editing an existing signature usually means
            // adding to it, and selecting it all invites deleting it by accident.
            SignatureBox.CaretIndex = SignatureBox.Text.Length;
        };
    }

    /// <summary>
    /// Trailing whitespace in a signature turns into blank lines at the bottom
    /// of every message, so it goes here rather than at each use.
    /// </summary>
    string Text() => SignatureBox.Text.TrimEnd();

    void OnInsert(object sender, RoutedEventArgs e)
    {
        SignatureText = Text();
        Insert = true;
        Save = SaveBox.IsChecked == true;
        DialogResult = true;
    }

    void OnSaveOnly(object sender, RoutedEventArgs e)
    {
        SignatureText = Text();
        Insert = false;
        Save = true;
        DialogResult = true;
    }
}
