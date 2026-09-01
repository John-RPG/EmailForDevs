using System.Configuration;
using System.Data;
using System.Windows;

namespace Mail.App;

/// <summary>
/// Interaction logic for App.xaml
/// </summary>
public partial class App : Application
{
    public App()
    {
        // Surface XAML binding failures in the debug output: a mistyped binding
        // path otherwise fails silently and just renders nothing.
        System.Diagnostics.PresentationTraceSources.Refresh();
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Listeners.Add(
            new System.Diagnostics.ConsoleTraceListener());
        System.Diagnostics.PresentationTraceSources.DataBindingSource.Switch.Level =
            System.Diagnostics.SourceLevels.Error;
    }

}

