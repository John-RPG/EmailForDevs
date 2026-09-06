using System.Globalization;
using Microsoft.Data.Sqlite;

namespace Mail.Storage.Settings;

/// <summary>
/// Reads and writes setting values, and resolves them through the scope chain.
///
/// Only overrides are stored. A level that has not been set holds no row, which
/// is what makes "inherited" distinguishable from "set to the same value as its
/// parent" — the difference matters, because changing the parent should move the
/// first and not the second.
/// </summary>
public sealed class SettingsStore(SqliteConnection appDb)
{
    /// <param name="Source">
    /// The level the value actually came from, so the UI can say "inherited from
    /// Account" rather than presenting an inherited value as if it were set here.
    /// Meaningless when <paramref name="IsDefault"/> is true: nothing set it, so
    /// there is no source level to name. Read <see cref="Describe"/> instead of
    /// formatting Source directly.
    /// </param>
    public readonly record struct Resolved(
        string Value, SettingScope Source, bool IsDefault)
    {
        /// <summary>Where the value came from, in words, without claiming a
        /// level set something it did not.</summary>
        public string Describe() =>
            IsDefault ? "built-in default" : $"set at {Source}";
    }

    /// <summary>
    /// The value in force for a target, walking outwards until a level has one.
    /// Falls back to the setting's declared default.
    /// </summary>
    public Resolved Resolve(SettingDefinition setting, params SettingTarget[] chain)
    {
        // Narrowest first: the caller passes the chain it wants, which for a
        // folder is folder → mailbox → account → application.
        foreach (var target in chain.OrderBy(t => t.Scope))
        {
            if (!setting.AppliesTo(target.Scope)) continue;
            if (TryGet(setting.Key, target, out var value))
                return new Resolved(value, target.Scope, IsDefault: false);
        }
        return new Resolved(setting.Default, setting.RootScope, IsDefault: true);
    }

    public bool TryGet(string key, SettingTarget target, out string value)
    {
        using var cmd = appDb.CreateCommand();
        cmd.CommandText = """
            SELECT value FROM settings
            WHERE key = @k AND scope = @s AND coalesce(target, '') = coalesce(@t, '');
            """;
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@s", (int)target.Scope);
        cmd.Parameters.AddWithValue("@t", (object?)target.Key ?? DBNull.Value);
        var result = cmd.ExecuteScalar() as string;
        value = result ?? "";
        return result is not null;
    }

    /// <summary>Sets an override at one level.</summary>
    public void Set(string key, SettingTarget target, string value)
    {
        using var cmd = appDb.CreateCommand();
        cmd.CommandText = """
            INSERT INTO settings(key, scope, target, value, updated_at)
            VALUES(@k, @s, @t, @v, unixepoch())
            ON CONFLICT(key, scope, target) DO UPDATE
              SET value = excluded.value, updated_at = excluded.updated_at;
            """;
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@s", (int)target.Scope);
        cmd.Parameters.AddWithValue("@t", target.Key ?? "");
        cmd.Parameters.AddWithValue("@v", value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// Removes an override so the level inherits again. Distinct from setting it
    /// to the parent's current value, which would pin it against later changes.
    /// </summary>
    public void Clear(string key, SettingTarget target)
    {
        using var cmd = appDb.CreateCommand();
        cmd.CommandText = """
            DELETE FROM settings
            WHERE key = @k AND scope = @s AND coalesce(target, '') = coalesce(@t, '');
            """;
        cmd.Parameters.AddWithValue("@k", key);
        cmd.Parameters.AddWithValue("@s", (int)target.Scope);
        cmd.Parameters.AddWithValue("@t", (object?)target.Key ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    /// <summary>Every override set at one level, for showing what differs from inherited.</summary>
    public Dictionary<string, string> Overrides(SettingTarget target)
    {
        using var cmd = appDb.CreateCommand();
        cmd.CommandText = """
            SELECT key, value FROM settings
            WHERE scope = @s AND coalesce(target, '') = coalesce(@t, '');
            """;
        cmd.Parameters.AddWithValue("@s", (int)target.Scope);
        cmd.Parameters.AddWithValue("@t", (object?)target.Key ?? DBNull.Value);
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        using var reader = cmd.ExecuteReader();
        while (reader.Read()) result[reader.GetString(0)] = reader.GetString(1);
        return result;
    }

    /// <summary>Drops every override at a level — used when a mailbox is removed.</summary>
    public void ClearAll(SettingTarget target)
    {
        using var cmd = appDb.CreateCommand();
        cmd.CommandText = """
            DELETE FROM settings
            WHERE scope = @s AND coalesce(target, '') = coalesce(@t, '');
            """;
        cmd.Parameters.AddWithValue("@s", (int)target.Scope);
        cmd.Parameters.AddWithValue("@t", (object?)target.Key ?? DBNull.Value);
        cmd.ExecuteNonQuery();
    }

    // ---- typed accessors ----------------------------------------------------

    public bool GetBool(SettingDefinition setting, params SettingTarget[] chain) =>
        bool.TryParse(Resolve(setting, chain).Value, out var value) && value;

    public int GetInt(SettingDefinition setting, params SettingTarget[] chain) =>
        int.TryParse(Resolve(setting, chain).Value, NumberStyles.Integer,
            CultureInfo.InvariantCulture, out var value)
            ? value
            : int.Parse(setting.Default, CultureInfo.InvariantCulture);

    public string GetString(SettingDefinition setting, params SettingTarget[] chain) =>
        Resolve(setting, chain).Value;
}
