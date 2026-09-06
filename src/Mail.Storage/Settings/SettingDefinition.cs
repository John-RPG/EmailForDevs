namespace Mail.Storage.Settings;

public enum SettingKind { Bool, Int, String, Enum, Duration }

/// <summary>
/// How risky a setting is to change. Drives presentation, not permission: an
/// eeeMail user is a developer and may change anything, but they should be able
/// to see at a glance which switches bite.
/// </summary>
public enum SettingRisk
{
    /// <summary>Cosmetic or convenience; no way to lose anything.</summary>
    Safe,

    /// <summary>Affects how much is downloaded, stored, or how often work runs.</summary>
    Performance,

    /// <summary>Weakens a defence, or exposes something that was contained.</summary>
    Security,

    /// <summary>Can destroy mail or local data irreversibly.</summary>
    Destructive,
}

/// <summary>
/// One declared setting: what it means, where it may be set, and what it costs.
///
/// Declaring settings as data rather than scattering them across dialogs means
/// the settings UI is generated from this list, every setting is documented in
/// exactly one place, and the levels a value may be set at cannot drift from the
/// levels the resolver actually reads.
/// </summary>
/// <param name="Key">Stable identifier; never renamed once shipped, since it is stored.</param>
/// <param name="Scopes">
/// Levels this may be set at, narrowest first. A setting that only makes sense
/// once for the install lists only <see cref="SettingScope.Application"/>.
/// </param>
public sealed record SettingDefinition(
    string Key,
    string Name,
    string Description,
    SettingKind Kind,
    string Default,
    IReadOnlyList<SettingScope> Scopes,
    SettingRisk Risk = SettingRisk.Safe,
    string? Warning = null,
    IReadOnlyList<string>? Choices = null,
    string? Category = null)
{
    public bool AppliesTo(SettingScope scope) => Scopes.Contains(scope);

    /// <summary>
    /// Whether a value is storable for this setting. Checked on write rather
    /// than only on read: a value that cannot be parsed silently falls back to
    /// the default at every read, so the setting appears set while doing
    /// nothing — the worst of both. Rejecting it at the point of entry lets the
    /// UI say so while the user is still looking at the field.
    /// </summary>
    public bool IsValid(string value, out string error)
    {
        error = "";
        switch (Kind)
        {
            case SettingKind.Bool when !bool.TryParse(value, out _):
                error = "Expected true or false.";
                return false;
            case SettingKind.Int when !int.TryParse(
                    value, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out _):
                error = "Expected a whole number.";
                return false;
            case SettingKind.Enum when Choices is not null &&
                    !Choices.Contains(value, StringComparer.OrdinalIgnoreCase):
                error = $"Expected one of: {string.Join(", ", Choices)}.";
                return false;
            case SettingKind.String when Key.EndsWith("datetime_format", StringComparison.Ordinal):
                // A bad format string throws at render time, far from here.
                try { _ = DateTimeOffset.Now.ToString(value); }
                catch (FormatException)
                {
                    error = "Not a valid .NET date/time format string.";
                    return false;
                }
                break;
        }
        return true;
    }

    /// <summary>The broadest level this may be set at — where its default lives.</summary>
    public SettingScope RootScope => Scopes.Max();
}
