using System.Windows;
using System.Windows.Media;
using Mail.Core.Themes;

namespace Mail.App.Themes;

/// <summary>
/// Applies a theme by swapping one merged resource dictionary.
///
/// The brushes are generated from <see cref="Palette"/> rather than written out
/// in XAML twice, so a colour cannot be defined in one theme and forgotten in
/// the other, and the contrast tests cover exactly what ships. Switching themes
/// replaces the dictionary in place, so open windows restyle without reopening.
/// </summary>
public static class ThemeManager
{
    public enum Theme { System, Light, Dark }

    /// <summary>Marks our dictionary so a switch replaces it rather than stacking.</summary>
    const string MarkerKey = "eeeMail.ThemeMarker";
    const string StylesMarkerKey = "eeeMail.StylesMarker";

    public static Theme Current { get; private set; } = Theme.System;

    /// <summary>True when the effective theme is dark, after resolving System.</summary>
    public static bool IsDark { get; private set; }

    public static void Apply(Theme theme)
    {
        Current = theme;
        IsDark = theme switch
        {
            Theme.Dark => true,
            Theme.Light => false,
            _ => IsWindowsInDarkMode(),
        };

        var dictionary = Build(IsDark);
        var merged = Application.Current.Resources.MergedDictionaries;

        // Replace ours in place: adding another would leave the old brushes
        // resolving first for anything already bound.
        for (var i = merged.Count - 1; i >= 0; i--)
            if (merged[i].Contains(MarkerKey))
                merged.RemoveAt(i);
        merged.Add(dictionary);

        // Control styles are added once and left alone: they reference the
        // brushes as DynamicResource, so replacing the palette above restyles
        // everything without rebuilding the styles themselves.
        if (!merged.Any(d => d.Contains(StylesMarkerKey)))
        {
            var styles = new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Themes/ControlStyles.xaml", UriKind.Absolute),
            };
            styles[StylesMarkerKey] = true;
            merged.Add(styles);
        }
    }

    static ResourceDictionary Build(bool dark)
    {
        var dictionary = new ResourceDictionary { [MarkerKey] = true };
        foreach (var entry in Palette.Entries)
        {
            var (r, g, b) = Palette.ParseRgb(dark ? entry.Dark : entry.Light);
            var brush = new SolidColorBrush(Color.FromRgb((byte)r, (byte)g, (byte)b));
            brush.Freeze();   // shared across threads and cheaper to render
            dictionary[entry.Key] = brush;
            // The raw colour too, for gradients and animations that need one.
            dictionary[$"{entry.Key}.Color"] = brush.Color;
        }
        return dictionary;
    }

    /// <summary>
    /// Windows' own light/dark preference. Read from the registry because WPF
    /// exposes no first-class API for it; a missing value means light, which is
    /// the Windows default.
    /// </summary>
    public static bool IsWindowsInDarkMode()
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            return key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public static Theme Parse(string value) => value?.ToLowerInvariant() switch
    {
        "dark" => Theme.Dark,
        "light" => Theme.Light,
        _ => Theme.System,
    };
}
