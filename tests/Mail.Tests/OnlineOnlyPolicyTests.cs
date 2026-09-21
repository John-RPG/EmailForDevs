using Mail.Storage.Settings;

namespace Mail.Tests;

/// <summary>
/// The online-only policy exists to make one promise: no mail is written to
/// this machine. These pin the parts of that promise that can be checked
/// without a network — that the setting exists and is selectable, and that the
/// description tells the truth about what is given up.
/// </summary>
public sealed class OnlineOnlyPolicyTests
{
    [Fact]
    public void Is_an_offered_choice()
    {
        // A policy the settings UI cannot show is a policy nobody can turn on.
        Assert.Contains("OnlineOnly", SettingsCatalog.SyncPolicy.Choices!);
    }

    [Fact]
    public void Is_not_the_default()
    {
        // Storing nothing locally costs offline access and a round trip per
        // folder. That is a deliberate choice, never one made for someone.
        Assert.Equal("MirrorServer", SettingsCatalog.SyncPolicy.Default);
    }

    [Fact]
    public void Describes_what_is_given_up()
    {
        // The mode silently disables offline reading and changes how search and
        // grouping answer. A description that only advertised the privacy win
        // would be selling it rather than explaining it.
        var description = SettingsCatalog.SyncPolicy.Description;

        Assert.Contains("OnlineOnly", description);
        Assert.Contains("offline", description, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("search", description, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Says_an_existing_cache_is_kept()
    {
        // Switching modes must not look like it might delete mail. The chosen
        // behaviour is to leave the store alone, and the description has to say
        // so or the user has to guess.
        Assert.Contains("switching back", SettingsCatalog.SyncPolicy.Description,
            StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData("MirrorServer")]
    [InlineData("WindowedCache")]
    public void Other_policies_still_cache(string policy)
    {
        // Guards against a future edit that makes everything online-only by
        // renaming a constant.
        Assert.NotEqual("OnlineOnly", policy);
        Assert.Contains(policy, SettingsCatalog.SyncPolicy.Choices!);
    }
}
