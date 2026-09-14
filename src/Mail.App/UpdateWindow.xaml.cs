using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Windows;
using Mail.Core.Update;

namespace Mail.App;

/// <summary>
/// Shows what an update is and what it will run, then installs it only if the
/// user says so.
///
/// The binary is unsigned, so there is no OS-level guarantee behind it. The
/// honest thing is to show exactly what is about to be downloaded — version,
/// size, and the digest it is checked against — and let the reader decide,
/// rather than presenting a bare "Update?" prompt.
/// </summary>
public partial class UpdateWindow : Window
{
    readonly ReleaseInfo _release;
    readonly HttpClient _http;
    readonly string _stagingDirectory;
    readonly Action<string> _log;

    public UpdateWindow(
        ReleaseInfo release, HttpClient http, string stagingDirectory, Action<string> log)
    {
        InitializeComponent();
        _release = release;
        _http = http;
        _stagingDirectory = stagingDirectory;
        _log = log;

        var running = typeof(UpdateWindow).Assembly.GetName().Version;
        Headline.Text = $"eeeMail {release.Tag} is available";
        AssetLine.Text =
            $"{release.AssetName}  ({release.SizeBytes / 1024.0 / 1024.0:N0} MB)\n" +
            $"installed: {running?.ToString(3) ?? "unknown"}   →   new: {release.Version.ToString(3)}";

        DigestLine.Text = release.Sha256 is { Length: 64 }
            ? $"SHA-256 {release.Sha256}\nchecked after download; the file is discarded if it differs"
            : "No digest published for this release — it cannot be verified, so it will not be installed.";

        // Nothing to install if it cannot be checked. The button would only lead
        // to a refusal, so it is disabled with the reason already on screen.
        InstallButton.IsEnabled = release.Sha256 is { Length: 64 };
        NotesText.Text = string.IsNullOrWhiteSpace(release.Notes)
            ? "(no release notes)"
            : release.Notes;
    }

    void OnViewRelease(object sender, RoutedEventArgs e)
    {
        // UseShellExecute so this opens the browser rather than trying to exec it.
        Process.Start(new ProcessStartInfo(_release.HtmlUrl) { UseShellExecute = true });
    }

    void OnLater(object sender, RoutedEventArgs e) => Close();

    async void OnInstall(object sender, RoutedEventArgs e)
    {
        InstallButton.IsEnabled = false;
        LaterButton.IsEnabled = false;
        Progress.Visibility = Visibility.Visible;
        StatusLine.Text = "Downloading…";

        try
        {
            var installer = new UpdateInstaller(_http);
            var progress = new Progress<double>(fraction => Progress.Value = fraction * 100);
            var zip = await installer.DownloadAsync(
                _release, _stagingDirectory, progress);

            StatusLine.Text = "Verified. Restarting to finish…";
            _log($"Update {_release.Tag} downloaded and verified.");

            var exe = Environment.ProcessPath
                ?? throw new InvalidOperationException("Cannot determine the running executable.");
            UpdateInstaller.StageAndSwap(zip, exe, _stagingDirectory);

            // The swap script waits for this process to exit before touching the
            // exe, so shutting down is what lets the update proceed.
            _log("Shutting down to apply the update.");
            Application.Current.Shutdown();
        }
        catch (UpdateVerificationException ex)
        {
            // Worth a dialog rather than a status line: a mismatch means the
            // bytes were not what GitHub published, which the user should see.
            Progress.Visibility = Visibility.Collapsed;
            StatusLine.Text = "Verification failed — nothing was installed.";
            _log($"Update refused: {ex.Message}");
            MessageBox.Show(this, ex.Message, "Update refused",
                MessageBoxButton.OK, MessageBoxImage.Warning);
            LaterButton.IsEnabled = true;
        }
        catch (Exception ex) when (ex is HttpRequestException or IOException or TaskCanceledException)
        {
            Progress.Visibility = Visibility.Collapsed;
            StatusLine.Text = $"Download failed: {ex.Message}";
            _log($"Update download failed: {ex.Message}");
            InstallButton.IsEnabled = true;
            LaterButton.IsEnabled = true;
        }
    }
}
