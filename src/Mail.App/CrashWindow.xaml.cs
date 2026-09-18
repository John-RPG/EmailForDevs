using System.Diagnostics;
using System.IO;
using System.Windows;
using Mail.Core.Diagnostics;

namespace Mail.App;

/// <summary>
/// Shown when something escapes unhandled, instead of the process vanishing.
///
/// The report is built and displayed before anything is offered, so the user can
/// read exactly what would be published. Reporting opens GitHub in the browser
/// with the text prefilled rather than posting through the API: that keeps the
/// app free of any credential, and leaves the final press on the user.
/// </summary>
public partial class CrashWindow : Window
{
    readonly Exception _exception;
    readonly string _version;
    readonly string _title;
    readonly string _repository;

    /// <summary>True when the fault left the app in a state worth restarting from.</summary>
    public bool Fatal { get; }

    public CrashWindow(Exception exception, string version, string repository, bool fatal)
    {
        InitializeComponent();
        SourceInitialized += (_, _) => Themes.ThemeManager.ApplyTitleBar(this);
        _exception = exception;
        _version = version;
        _repository = repository;
        Fatal = fatal;

        _title = CrashReport.Title(exception);

        Headline.Text = fatal
            ? "eeeMail has to close"
            : "Something went wrong";
        Subhead.Text = fatal
            ? "An unexpected error stopped the app. Your mail is stored on disk and " +
              "nothing has been lost — reopening will pick up where this left off."
            : "The app is still running, but something failed unexpectedly. " +
              "It is worth reporting even if everything looks fine.";
        CloseButton.Content = fatal ? "Close eeeMail" : "Continue";
        ReportBox.Text = CrashReport.Build(exception, version);
        // Rebuilt as they type, so the box always shows exactly what will be
        // sent rather than a stale preview.
        NotesBox.TextChanged += (_, _) =>
            ReportBox.Text = CrashReport.Build(_exception, _version, NotesBox.Text);
    }

    void OnReport(object sender, RoutedEventArgs e)
    {
        var url = CrashReport.IssueUrl(_repository, _title, ReportBox.Text);
        try
        {
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
            Report("Opened GitHub — nothing is sent until you press Submit there.");
        }
        catch (Exception ex)
        {
            Report($"Could not open the browser: {ex.Message}. Use Copy instead.");
        }
    }

    void OnCopy(object sender, RoutedEventArgs e)
    {
        try
        {
            Clipboard.SetText(ReportBox.Text);
            Report("Copied.");
        }
        catch (System.Runtime.InteropServices.COMException)
        {
            Report("Could not reach the clipboard — select the text and copy it.");
        }
    }

    void OnSave(object sender, RoutedEventArgs e)
    {
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Title = "Save crash report",
            FileName = $"eeemail-crash-{DateTime.Now:yyyyMMdd-HHmmss}.md",
            DefaultExt = ".md",
            Filter = "Markdown (*.md)|*.md|Text file (*.txt)|*.txt|All files (*.*)|*.*",
            InitialDirectory = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments),
        };
        if (dialog.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(dialog.FileName, ReportBox.Text);
            Report($"Saved to {Path.GetFileName(dialog.FileName)}.");
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Report($"Could not save: {ex.Message}");
        }
    }

    void Report(string message) => ActionResult.Text = message;

    void OnClose(object sender, RoutedEventArgs e) => Close();
}
