using System.Security.Cryptography;
using Mail.Storage.Database;
using Mail.Storage.Settings;
using Microsoft.Data.Sqlite;

namespace Mail.Tests;

public sealed class SettingsStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));
    readonly SqliteConnection _appDb;
    readonly SettingsStore _settings;

    // A folder in a mailbox in an account: the full chain, narrowest first.
    static readonly SettingTarget[] Chain =
    [
        SettingTarget.Folder(7, 42),
        SettingTarget.Mailbox(7),
        SettingTarget.Account(3),
        SettingTarget.Application,
    ];

    public SettingsStoreTests()
    {
        Directory.CreateDirectory(_dir);
        _appDb = AppDatabase.Open(
            Path.Combine(_dir, "app.db"), RandomNumberGenerator.GetBytes(32));
        _settings = new SettingsStore(_appDb);
    }

    public void Dispose()
    {
        _appDb.Dispose();
        SqliteConnection.ClearAllPools();
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Unset_resolves_to_the_declared_default()
    {
        var resolved = _settings.Resolve(SettingsCatalog.LoadRemoteImages, Chain);
        Assert.Equal("never", resolved.Value);
        Assert.True(resolved.IsDefault);
    }

    [Fact]
    public void Narrower_scope_wins()
    {
        _settings.Set(SettingsCatalog.LoadRemoteImages.Key, SettingTarget.Application, "always");
        _settings.Set(SettingsCatalog.LoadRemoteImages.Key, SettingTarget.Account(3), "known senders");
        _settings.Set(SettingsCatalog.LoadRemoteImages.Key, SettingTarget.Folder(7, 42), "never");

        var resolved = _settings.Resolve(SettingsCatalog.LoadRemoteImages, Chain);
        Assert.Equal("never", resolved.Value);
        Assert.Equal(SettingScope.Folder, resolved.Source);
    }

    [Fact]
    public void Resolution_reports_where_the_value_came_from()
    {
        // The UI has to distinguish "set here" from "inherited", or clearing an
        // override becomes impossible to offer meaningfully.
        _settings.Set(SettingsCatalog.LoadRemoteImages.Key, SettingTarget.Account(3), "always");

        var resolved = _settings.Resolve(SettingsCatalog.LoadRemoteImages, Chain);
        Assert.Equal(SettingScope.Account, resolved.Source);
        Assert.False(resolved.IsDefault);
    }

    [Fact]
    public void Clearing_an_override_restores_inheritance()
    {
        _settings.Set(SettingsCatalog.LoadRemoteImages.Key, SettingTarget.Account(3), "always");
        _settings.Set(SettingsCatalog.LoadRemoteImages.Key, SettingTarget.Folder(7, 42), "never");
        _settings.Clear(SettingsCatalog.LoadRemoteImages.Key, SettingTarget.Folder(7, 42));

        var resolved = _settings.Resolve(SettingsCatalog.LoadRemoteImages, Chain);
        Assert.Equal("always", resolved.Value);
        Assert.Equal(SettingScope.Account, resolved.Source);
    }

    [Fact]
    public void Changing_a_parent_moves_inheritors_but_not_overriders()
    {
        // The whole point of storing only overrides: a folder left inheriting
        // follows its parent, while one explicitly set keeps its own value even
        // when that value equals what it would have inherited.
        _settings.Set(SettingsCatalog.RowDensity.Key, SettingTarget.Account(3), "single");
        _settings.Set(SettingsCatalog.RowDensity.Key, SettingTarget.Folder(7, 42), "single");

        _settings.Set(SettingsCatalog.RowDensity.Key, SettingTarget.Account(3), "three-line");

        var pinned = _settings.Resolve(SettingsCatalog.RowDensity, Chain);
        Assert.Equal("single", pinned.Value);

        var inheriting = _settings.Resolve(SettingsCatalog.RowDensity,
            SettingTarget.Folder(7, 99), SettingTarget.Mailbox(7),
            SettingTarget.Account(3), SettingTarget.Application);
        Assert.Equal("three-line", inheriting.Value);
    }

    [Fact]
    public void Scopes_a_setting_does_not_declare_are_skipped()
    {
        // AllowScripts is application-only. A stray row at a narrower scope must
        // not take effect, or the declared scope list would be a lie.
        _settings.Set(SettingsCatalog.AllowScripts.Key, SettingTarget.Folder(7, 42), "true");
        _settings.Set(SettingsCatalog.AllowScripts.Key, SettingTarget.Application, "false");

        Assert.False(_settings.GetBool(SettingsCatalog.AllowScripts, Chain));
    }

    [Fact]
    public void Targets_of_the_same_scope_do_not_collide()
    {
        _settings.Set(SettingsCatalog.SyncPolicy.Key, SettingTarget.Mailbox(7), "WindowedCache");

        Assert.Equal("WindowedCache",
            _settings.Resolve(SettingsCatalog.SyncPolicy, SettingTarget.Mailbox(7)).Value);
        Assert.Equal("MirrorServer",
            _settings.Resolve(SettingsCatalog.SyncPolicy, SettingTarget.Mailbox(8)).Value);
    }

    [Fact]
    public void Folder_targets_are_scoped_to_their_mailbox()
    {
        // Folder ids repeat across mailboxes, so folder 1 of mailbox 7 and
        // folder 1 of mailbox 8 must not share a value.
        _settings.Set(SettingsCatalog.ShowInFavourites.Key, SettingTarget.Folder(7, 1), "true");

        Assert.True(_settings.GetBool(SettingsCatalog.ShowInFavourites, SettingTarget.Folder(7, 1)));
        Assert.False(_settings.GetBool(SettingsCatalog.ShowInFavourites, SettingTarget.Folder(8, 1)));
    }

    [Fact]
    public void Overrides_lists_only_what_is_set_here()
    {
        _settings.Set(SettingsCatalog.SyncPolicy.Key, SettingTarget.Application, "WindowedCache");
        _settings.Set(SettingsCatalog.RowDensity.Key, SettingTarget.Mailbox(7), "two-line");

        var mailbox = _settings.Overrides(SettingTarget.Mailbox(7));
        Assert.Single(mailbox);
        Assert.Equal("two-line", mailbox[SettingsCatalog.RowDensity.Key]);
    }

    [Fact]
    public void ClearAll_removes_a_targets_settings_only()
    {
        _settings.Set(SettingsCatalog.RowDensity.Key, SettingTarget.Mailbox(7), "two-line");
        _settings.Set(SettingsCatalog.RowDensity.Key, SettingTarget.Mailbox(8), "three-line");

        _settings.ClearAll(SettingTarget.Mailbox(7));

        Assert.Empty(_settings.Overrides(SettingTarget.Mailbox(7)));
        Assert.Single(_settings.Overrides(SettingTarget.Mailbox(8)));
    }

    [Fact]
    public void Typed_accessors_fall_back_to_the_default_on_bad_input()
    {
        _settings.Set(SettingsCatalog.MaxConcurrentDownloads.Key,
            SettingTarget.Application, "not a number");
        Assert.Equal(4, _settings.GetInt(SettingsCatalog.MaxConcurrentDownloads, Chain));
    }

    [Fact]
    public void Every_catalog_setting_declares_a_usable_default()
    {
        // A default that cannot be parsed as its own kind would fail only at the
        // point of use, which may be far from here.
        foreach (var setting in SettingsCatalog.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(setting.Key));
            Assert.NotEmpty(setting.Scopes);
            switch (setting.Kind)
            {
                case SettingKind.Bool:
                    Assert.True(bool.TryParse(setting.Default, out _), setting.Key);
                    break;
                case SettingKind.Int:
                    Assert.True(int.TryParse(setting.Default, out _), setting.Key);
                    break;
                case SettingKind.Enum:
                    Assert.NotNull(setting.Choices);
                    Assert.Contains(setting.Default, setting.Choices!);
                    break;
            }
        }
    }

    [Fact]
    public void Invalid_values_are_rejected_before_they_are_stored()
    {
        // An unparseable value would be saved and then silently ignored on every
        // read, leaving a setting that looks set but does nothing.
        Assert.False(SettingsCatalog.MaxConcurrentDownloads.IsValid("ser6", out var intError));
        Assert.NotEmpty(intError);
        Assert.True(SettingsCatalog.MaxConcurrentDownloads.IsValid("6", out _));

        Assert.False(SettingsCatalog.AllowScripts.IsValid("yes", out _));
        Assert.True(SettingsCatalog.AllowScripts.IsValid("true", out _));

        Assert.False(SettingsCatalog.RowDensity.IsValid("enormous", out var enumError));
        Assert.Contains("single", enumError);
        Assert.True(SettingsCatalog.RowDensity.IsValid("two-line", out _));
    }

    [Fact]
    public void Default_message_columns_name_only_real_columns()
    {
        // The shell hides any column the setting leaves out, so a default that
        // names a key the grid does not have — or omits one it ships visible —
        // silently changes the list the first time the setting is applied. The
        // keys are duplicated here rather than shared because the grid lives in
        // a WPF assembly this project cannot reference; the point of the test is
        // that the two lists are checked against each other at all.
        string[] known =
        [
            "status", "from", "fromname", "fromaddress",
            "to", "received", "size", "subject",
        ];

        var spec = SettingsCatalog.MessageColumns.Default
            .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        Assert.All(spec, key => Assert.Contains(key, known));
        Assert.Equal(spec.Length, spec.Distinct(StringComparer.OrdinalIgnoreCase).Count());

        // The description is what tells a developer what they may type, so it
        // has to list every key the shell actually accepts.
        foreach (var key in known)
            Assert.Contains(key, SettingsCatalog.MessageColumns.Description);
    }

    [Fact]
    public void Date_formats_are_validated_as_format_strings()
    {
        // A bad format throws at render time, a long way from where it was set.
        Assert.True(SettingsCatalog.ListDateFormat.IsValid("yyyy-MM-dd HH:mm:ss", out _));
        Assert.True(SettingsCatalog.ReaderDateFormat.IsValid("ddd d MMM yyyy, HH:mm:ss", out _));
        Assert.False(SettingsCatalog.ListDateFormat.IsValid("yyyy-MM-dd \\", out _));
    }

    [Fact]
    public void List_and_reader_dates_resolve_independently()
    {
        _settings.Set(SettingsCatalog.ListDateFormat.Key, SettingTarget.Application, "HH:mm");
        Assert.Equal("HH:mm",
            _settings.Resolve(SettingsCatalog.ListDateFormat, Chain).Value);
        // The reader keeps its own default rather than following the list.
        Assert.True(_settings.Resolve(SettingsCatalog.ReaderDateFormat, Chain).IsDefault);
    }

    [Fact]
    public void Describe_does_not_name_a_level_that_set_nothing()
    {
        // sync.enabled has no Application scope, so an unset value used to report
        // its root scope as the source — claiming a level had set it.
        var resolved = _settings.Resolve(SettingsCatalog.SyncEnabled, Chain);
        Assert.True(resolved.IsDefault);
        Assert.Equal("built-in default", resolved.Describe());

        _settings.Set(SettingsCatalog.SyncEnabled.Key, SettingTarget.Mailbox(7), "false");
        Assert.Equal("set at Mailbox",
            _settings.Resolve(SettingsCatalog.SyncEnabled, Chain).Describe());
    }

    [Fact]
    public void A_specific_default_is_not_overridden_by_the_general_default()
    {
        // The reading pane declares a long date form and the list a short one.
        // Falling back to the general format when nothing is set would erase
        // both, which is what happened before this was fixed.
        Assert.Equal("ddd d MMM yyyy, HH:mm:ss", SettingsCatalog.ReaderDateFormat.Default);
        Assert.Equal("yyyy-MM-dd HH:mm:ss", SettingsCatalog.ListDateFormat.Default);
        Assert.NotEqual(
            SettingsCatalog.ReaderDateFormat.Default,
            SettingsCatalog.ListDateFormat.Default);

        // Both unset: each keeps its own default rather than converging.
        Assert.True(_settings.Resolve(SettingsCatalog.ReaderDateFormat, Chain).IsDefault);
        Assert.True(_settings.Resolve(SettingsCatalog.ListDateFormat, Chain).IsDefault);

        // The general format set explicitly is a deliberate instruction, so it
        // does apply to a specific format the user has not touched.
        _settings.Set(SettingsCatalog.DateTimeFormat.Key, SettingTarget.Application, "s");
        Assert.False(_settings.Resolve(SettingsCatalog.DateTimeFormat, Chain).IsDefault);
    }

    [Fact]
    public void Risky_settings_explain_the_risk()
    {
        foreach (var setting in SettingsCatalog.All.Where(s =>
                     s.Risk is SettingRisk.Security or SettingRisk.Destructive))
            Assert.False(string.IsNullOrWhiteSpace(setting.Warning), setting.Key);
    }
}
