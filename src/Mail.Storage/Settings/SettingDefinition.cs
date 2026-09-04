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

    /// <summary>The broadest level this may be set at — where its default lives.</summary>
    public SettingScope RootScope => Scopes.Max();
}
