using System.Windows;
using System.Windows.Documents;
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

    /// <summary>Set once, so windows opened later are themed as they load.</summary>
    static bool _hooked;

    public static void Apply(Theme theme)
    {
        if (!_hooked)
        {
            _hooked = true;
            // Every window, including ones created long after this call. Loaded
            // rather than Initialized so a window's own XAML values are already
            // in place and can take precedence.
            EventManager.RegisterClassHandler(
                typeof(Window), FrameworkElement.LoadedEvent,
                new RoutedEventHandler((sender, _) =>
                {
                    if (sender is Window window) { ApplyChrome(window); ApplyTitleBar(window); }
                }));
        }

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

            // AvalonDock's own chrome is light regardless of the app palette,
            // so its surfaces are pointed at the theme brushes too.
            merged.Add(new ResourceDictionary
            {
                Source = new Uri("pack://application:,,,/Themes/DockStyles.xaml", UriKind.Absolute),
            });
        }

        foreach (var window in Application.Current.Windows.OfType<Window>())
        {
            ApplyChrome(window);
            ApplyTitleBar(window);
        }
    }

    /// <summary>
    /// Paints a window's own surface and default text colour.
    ///
    /// Needed because an implicit <c>Style TargetType="Window"</c> does not reach
    /// a derived window class, and every window here is one — so the styles in
    /// ControlStyles.xaml silently missed all of them, leaving grey-on-grey text
    /// on the system default background. Applied per window instead, and only
    /// where the window has not set its own, so a deliberate choice still wins.
    /// </summary>
    public static void ApplyChrome(Window window)
    {
        if (window.ReadLocalValue(Window.BackgroundProperty) == DependencyProperty.UnsetValue)
            window.SetResourceReference(Window.BackgroundProperty, "Chrome.Background");
        if (window.ReadLocalValue(Window.ForegroundProperty) == DependencyProperty.UnsetValue)
            window.SetResourceReference(Window.ForegroundProperty, "Text.Primary");

        // TextBlock ignores inherited Foreground from a Window, so controls that
        // render text through one need the default set on the text element too.
        if (window.ReadLocalValue(TextElement.ForegroundProperty) == DependencyProperty.UnsetValue)
            window.SetResourceReference(TextElement.ForegroundProperty, "Text.Primary");

        ApplyIcon(window);
    }

    /// <summary>The app icon, loaded once and shared by every window.</summary>
    static System.Windows.Media.Imaging.BitmapFrame? _icon;

    /// <summary>
    /// Puts the app icon in the title bar and Alt-Tab. Set per window because
    /// WPF has no application-wide icon: a window without one shows the generic
    /// executable icon even when the .exe itself is branded.
    /// </summary>
    static void ApplyIcon(Window window)
    {
        if (window.ReadLocalValue(Window.IconProperty) != DependencyProperty.UnsetValue)
            return;
        try
        {
            _icon ??= System.Windows.Media.Imaging.BitmapFrame.Create(
                new Uri("pack://application:,,,/Assets/icon.ico"));
            window.Icon = _icon;
        }
        catch (Exception)
        {
            // An icon is decoration: a packaging mistake should not stop a
            // window from opening.
        }
    }

    /// <summary>
    /// Darkens the title bar, which WPF cannot style because it is drawn by the
    /// window manager rather than by the app. Without this the app has a bright
    /// white cap in dark mode, which is the single most obvious wrong thing on
    /// screen. Best-effort: the attribute is Windows 10 1809 and later, and the
    /// older build used a different id for it.
    /// </summary>
    public static void ApplyTitleBar(Window window)
    {
        try
        {
            var handle = new System.Windows.Interop.WindowInteropHelper(window).Handle;
            if (handle == IntPtr.Zero) return;   // not shown yet; re-applied on load
            var dark = IsDark ? 1 : 0;
            // 20 since 1903; 19 on 1809. Trying the current one first and only
            // falling back keeps the newer path free of a pointless second call.
            if (DwmSetWindowAttribute(handle, 20, ref dark, sizeof(int)) != 0)
                DwmSetWindowAttribute(handle, 19, ref dark, sizeof(int));
        }
        catch (DllNotFoundException)
        {
            // No dwmapi: nothing to darken, and not a reason to fail startup.
        }
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

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
