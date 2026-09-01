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
    }

}

