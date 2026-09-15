using System.Text.Json;

namespace Mail.Core.Storage;

/// <summary>
/// Decides where the profile, mailbox databases and caches live.
///
/// This cannot be an ordinary setting, because settings are stored in app.db and
/// app.db lives in the directory being located. So the location comes from a
/// small JSON file beside the executable, or an environment variable, and
/// everything else — including the setting a user edits in the settings tree —
/// is resolved relative to whatever this returns.
/// </summary>
public static class DataLocation
{
    /// <summary>Overrides the resolved directory outright. Mainly for tests and CI.</summary>
    public const string EnvironmentVariable = "EEEMAIL_DATA_DIR";

    /// <summary>
    /// Sits next to the executable. Present means "portable": keep the data
    /// alongside the app rather than in the user's profile, which is what makes
    /// an install on a USB stick or a dev checkout self-contained.
    /// </summary>
    public const string PortableMarker = "eeemail.portable.json";

    /// <summary>
    /// The directory holding <c>profile</c>, <c>mailboxes</c> and the caches.
    /// Created if missing, so callers can use it without checking.
    /// </summary>
    /// <param name="executableDirectory">
    /// Where the app is installed. Passed in rather than read from
    /// AppContext so this stays testable.
    /// </param>
    public static string Resolve(string executableDirectory)
    {
        var resolved = ResolveWithoutCreating(executableDirectory);
        Directory.CreateDirectory(resolved);
        return resolved;
    }

    /// <summary>The same decision, without touching the disk.</summary>
    public static string ResolveWithoutCreating(string executableDirectory)
    {
        // 1. An explicit environment variable wins, so a test or a script can
        //    redirect the whole store without editing anything.
        var fromEnvironment = Environment.GetEnvironmentVariable(EnvironmentVariable);
        if (!string.IsNullOrWhiteSpace(fromEnvironment))
            return Path.GetFullPath(fromEnvironment);

        // 2. A marker file beside the executable, for portable installs.
        var marker = Path.Combine(executableDirectory, PortableMarker);
        if (File.Exists(marker))
        {
            var configured = ReadMarker(marker);
            // An empty or unreadable marker still means portable; it just does
            // not name a directory, so the app's own folder is used.
            var target = string.IsNullOrWhiteSpace(configured)
                ? Path.Combine(executableDirectory, "data")
                : configured;
            return Path.GetFullPath(Path.Combine(executableDirectory, target));
        }

        // 3. The default: per-user application data. Roaming rather than local,
        //    because the profile key is not a copy of anything on a server and
        //    losing it costs the local store.
        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "eeeMail");
    }

    /// <summary>
    /// Reads the "dataDirectory" value from the marker, if it has one. A broken
    /// file is treated as an empty one: refusing to start because a JSON file is
    /// malformed would be worse than falling back to the app's own folder.
    /// </summary>
    static string? ReadMarker(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            return document.RootElement.TryGetProperty("dataDirectory", out var value)
                ? value.GetString()
                : null;
        }
        catch (Exception ex) when (ex is JsonException or IOException or UnauthorizedAccessException)
        {
            return null;
        }
    }

    /// <summary>
    /// Directories that already hold a profile, in the order this class would
    /// resolve them.
    ///
    /// Only ever called because a user pressed a button. Nothing here runs at
    /// startup: the app uses <see cref="Resolve"/>, which consults three known
    /// places and never searches. The candidates are deliberately few and fixed
    /// — the standard location and the app's own folder — rather than a walk of
    /// the disk looking for other people's mail.
    /// </summary>
    public static IEnumerable<string> FindExistingProfiles(string executableDirectory)
    {
        var candidates = new[]
        {
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "eeeMail"),
            Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "eeeMail"),
            Path.Combine(executableDirectory, "data"),
            executableDirectory,
        };

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var candidate in candidates)
        {
            if (!seen.Add(Path.GetFullPath(candidate))) continue;
            // A profile is only real if the master key is there; an empty
            // directory left by an uninstall is not one.
            if (File.Exists(Path.Combine(candidate, "profile", "master.key")))
                yield return candidate;
        }
    }

    public static string ProfileDirectory(string dataDirectory) =>
        Path.Combine(dataDirectory, "profile");

    public static string MailboxDirectory(string dataDirectory) =>
        Path.Combine(dataDirectory, "mailboxes");
}
