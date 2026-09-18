using System.Configuration;
using System.Data;
using System.Windows;

namespace Mail.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
/// <summary>
/// Reports binding failures, minus the two that WPF's own TreeViewItem style
/// raises while recycling containers (it probes for an ancestor ItemsControl
/// that is not there yet). Those are harmless and would otherwise train us to
/// ignore the output.
/// </summary>
sealed class BindingErrorListener : System.Diagnostics.TraceListener
{
    public override void Write(string? message) { }

    public override void WriteLine(string? message)
    {
        if (message is null) return;
        if (message.Contains("AncestorType='System.Windows.Controls.ItemsControl'") &&
            (message.Contains("HorizontalContentAlignment") ||
             message.Contains("VerticalContentAlignment")))
            return;
        Console.WriteLine(message);
    }
}

public partial class App : Application
{
    public App()
    {
        // Surface XAML binding failures in the debug output: a mistyped binding
        // path otherwise fails silently and just renders nothing.
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(
            new BindingErrorListener());
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level =
            System.Diagnostics.SourceLevels.Error;

        // Three routes an exception can escape by, and all three used to end the
        // process silently. A mail client that vanishes mid-send teaches the user
        // nothing and tells us nothing.
        DispatcherUnhandledException += (_, args) =>
        {
            // The UI thread is intact here, so the app can carry on afterwards.
            args.Handled = Report(args.Exception, fatal: false);
        };

        // An async void continuation that threw. Nobody is awaiting it, so
        // without this the process dies with no window and no message — which is
        // exactly what a crash after pressing Send looks like.
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            args.SetObserved();
            OnUi(() => Report(args.Exception, fatal: false));
        };

        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            // Last stop: the runtime is tearing down and cannot be stopped, so
            // this only gets the report in front of the user before it goes.
            if (args.ExceptionObject is Exception ex) OnUi(() => Report(ex, fatal: true));
        };
    }

    /// <summary>The repository crash reports are offered to.</summary>
    const string ReportRepository = "John-RPG/EmailForDevs";

    /// <summary>Guards against a crash inside the crash handler looping forever.</summary>
    static bool _reporting;

    static void OnUi(Action action) =>
        Current?.Dispatcher.Invoke(action);

    /// <summary>
    /// Shows the crash window. Returns whether the exception was handled, which
    /// for a non-fatal fault lets the app keep running.
    /// </summary>
    static bool Report(Exception exception, bool fatal)
    {
        if (_reporting) return false;
        _reporting = true;
        try
        {
            var version = System.Reflection.Assembly.GetExecutingAssembly()
                .GetName().Version?.ToString(3) ?? "dev";
            var window = new CrashWindow(exception, version, ReportRepository, fatal)
            {
                // Owner only when there is a live one: during shutdown the main
                // window may already be gone, and setting it then throws.
                Owner = Current?.Windows.OfType<MainWindow>()
                    .FirstOrDefault(w => w.IsLoaded),
            };
            window.ShowDialog();
            return !fatal;
        }
        catch (Exception)
        {
            // If reporting itself fails there is nothing useful left to do, and
            // throwing from here would replace a clear crash with a confusing one.
            return false;
        }
        finally
        {
            _reporting = false;
        }
    }
}

