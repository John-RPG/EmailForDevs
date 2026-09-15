using System.IO;
using System.Windows;

namespace Mail.App;

/// <summary>
/// Shows the profile recovery code once, in a form the reader can actually keep.
///
/// This was a MessageBox, which meant the only way to record a code you cannot
/// ever see again was to copy it off the screen by hand. Here it is selectable
/// text with copy and save-to-file, and Continue stays disabled until the reader
/// confirms they have it — the one moment where a small amount of friction is
/// worth it, because losing this code costs the local mail store.
/// </summary>
public partial class RecoveryCodeWindow : Window
{
    readonly string _code;

    public RecoveryCodeWindow(string code)
    {
        InitializeComponent();
        // Its own HWND, so it needs the dark title bar applied separately.
        SourceInitialized += (_, _) => Themes.ThemeManager.ApplyTitleBar(this);
        _code = code;
        CodeBox.Text = code;
        Loaded += (_, _) => CodeBox.SelectAll();
    }

    void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(_code);
            Report("Copied.");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            // Another process can hold the clipboard open; the code is still on
            // screen and selectable, so this is not worth failing over.
            Report("Could not reach the clipboard — select the code and copy it.");
        }
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save recovery code",
            FileName = "eeeMail-recovery-code.txt",
            DefaultExt = ".txt",
            Filter = "Text file (*.txt)|*.txt|All files (*.*)|*.*",
            InitialDirectory =
                Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;

        try
        {
            File.WriteAllText(dialog.FileName,
                $"""
                eeeMail recovery code
                =====================

                    {_code}

                This unlocks the local mail store on this machine if Windows can no
                longer do it for your account — after a profile reset, or on a new
                machine. It is not stored anywhere else and cannot be reissued.

                Saved {DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz}
                """);
            Report($"Saved to {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"Could not save: {ex.Message}");
        }
    }

    void Report(string message) => ActionResult.Text = message;

    void OnSavedChanged(object sender, RoutedEventArgs e) =>
        CloseButton.IsEnabled = SavedBox.IsChecked == true;

    void OnClose(object sender, RoutedEventArgs e) => Close();
}
