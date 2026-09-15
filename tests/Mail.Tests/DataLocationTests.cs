using Mail.Core.Storage;

namespace Mail.Tests;

public sealed class DataLocationTests : IDisposable
{
    readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));

    public DataLocationTests()
    {
        Directory.CreateDirectory(_dir);
        Environment.SetEnvironmentVariable(DataLocation.EnvironmentVariable, null);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable(DataLocation.EnvironmentVariable, null);
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    [Fact]
    public void Defaults_to_application_data_not_the_install_directory()
    {
        // The bug this replaced: the app looked for a .scratch folder in or
        // above its own directory and refused to start without one, which no
        // installed copy has. An installed copy must land somewhere writable
        // that survives reinstalling the app.
        var resolved = DataLocation.ResolveWithoutCreating(_dir);

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Equal(Path.Combine(appData, "eeeMail"), resolved);
        Assert.DoesNotContain(".scratch", resolved);
    }

    [Fact]
    public void Environment_variable_overrides_everything()
    {
        var target = Path.Combine(_dir, "elsewhere");
        Environment.SetEnvironmentVariable(DataLocation.EnvironmentVariable, target);

        // Even with a portable marker present, which would otherwise win.
        File.WriteAllText(Path.Combine(_dir, DataLocation.PortableMarker), "{}");

        Assert.Equal(Path.GetFullPath(target), DataLocation.ResolveWithoutCreating(_dir));
    }

    [Fact]
    public void A_marker_without_a_path_keeps_data_beside_the_app()
    {
        File.WriteAllText(Path.Combine(_dir, DataLocation.PortableMarker), "{}");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(_dir, "data")),
            DataLocation.ResolveWithoutCreating(_dir));
    }

    [Fact]
    public void A_marker_can_name_a_directory_relative_to_the_app()
    {
        File.WriteAllText(
            Path.Combine(_dir, DataLocation.PortableMarker),
            """{ "dataDirectory": "store" }""");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(_dir, "store")),
            DataLocation.ResolveWithoutCreating(_dir));
    }

    [Fact]
    public void A_marker_can_name_an_absolute_directory()
    {
        var absolute = Path.Combine(_dir, "absolute");
        File.WriteAllText(
            Path.Combine(_dir, DataLocation.PortableMarker),
            $$"""{ "dataDirectory": {{System.Text.Json.JsonSerializer.Serialize(absolute)}} }""");

        Assert.Equal(Path.GetFullPath(absolute), DataLocation.ResolveWithoutCreating(_dir));
    }

    [Fact]
    public void A_malformed_marker_still_means_portable()
    {
        // Refusing to start because a config file is malformed would be worse
        // than falling back: the marker's presence is the portable signal, and
        // its contents only refine where.
        File.WriteAllText(Path.Combine(_dir, DataLocation.PortableMarker), "{ not json");

        Assert.Equal(
            Path.GetFullPath(Path.Combine(_dir, "data")),
            DataLocation.ResolveWithoutCreating(_dir));
    }

    [Fact]
    public void Resolve_creates_the_directory_it_returns()
    {
        var target = Path.Combine(_dir, "made", "on", "demand");
        Environment.SetEnvironmentVariable(DataLocation.EnvironmentVariable, target);

        Assert.True(Directory.Exists(DataLocation.Resolve(_dir)));
    }

    [Fact]
    public void Finds_a_profile_beside_the_app_only_when_one_is_really_there()
    {
        // An empty directory left behind by an uninstall is not a profile. The
        // master key is what makes it one.
        Directory.CreateDirectory(Path.Combine(_dir, "data", "profile"));
        Assert.DoesNotContain(
            Path.Combine(_dir, "data"), DataLocation.FindExistingProfiles(_dir));

        File.WriteAllText(Path.Combine(_dir, "data", "profile", "master.key"), "x");
        Assert.Contains(
            Path.Combine(_dir, "data"), DataLocation.FindExistingProfiles(_dir));
    }

    [Fact]
    public void Finding_profiles_is_never_what_startup_does()
    {
        // The guard behind the user's request: resolution consults three known
        // places and never searches, so an app that has not been asked cannot
        // wander off looking for someone's mail. Planting a profile beside the
        // executable must not change where a default install resolves to.
        Directory.CreateDirectory(Path.Combine(_dir, "data", "profile"));
        File.WriteAllText(Path.Combine(_dir, "data", "profile", "master.key"), "x");

        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Assert.Equal(
            Path.Combine(appData, "eeeMail"),
            DataLocation.ResolveWithoutCreating(_dir));
    }

    [Fact]
    public void Subdirectories_hang_off_the_resolved_root()
    {
        Assert.Equal(Path.Combine(_dir, "profile"), DataLocation.ProfileDirectory(_dir));
        Assert.Equal(Path.Combine(_dir, "mailboxes"), DataLocation.MailboxDirectory(_dir));
    }
}
