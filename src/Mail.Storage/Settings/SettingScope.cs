namespace Mail.Storage.Settings;

/// <summary>
/// The level a setting is stored at. Values resolve from the most specific level
/// outwards, so a folder overrides its mailbox, which overrides its account,
/// which overrides the application default.
///
/// Ordered narrowest-first: <see cref="SettingResolver"/> relies on that to walk
/// the chain, so new levels must be inserted in the right place rather than
/// appended.
/// </summary>
public enum SettingScope
{
    /// <summary>One folder in one mailbox.</summary>
    Folder = 0,

    /// <summary>One mailbox, including a shared one.</summary>
    Mailbox = 1,

    /// <summary>One signed-in account and everything it reaches.</summary>
    Account = 2,

    /// <summary>The installation. The last stop before a setting's built-in default.</summary>
    Application = 3,
}

/// <summary>
/// Identifies where a setting value lives: a scope plus the id of the thing it
/// applies to (null at application level, which has only one instance).
/// </summary>
/// <param name="Key">
/// Mailbox id, account id, or "{mailboxId}:{folderId}" for a folder — folders
/// are only unique within their mailbox, so the pair is the identity.
/// </param>
public readonly record struct SettingTarget(SettingScope Scope, string? Key)
{
    public static SettingTarget Application => new(SettingScope.Application, null);

    public static SettingTarget Account(long accountId) =>
        new(SettingScope.Account, accountId.ToString());

    public static SettingTarget Mailbox(long mailboxId) =>
        new(SettingScope.Mailbox, mailboxId.ToString());

    public static SettingTarget Folder(long mailboxId, long folderId) =>
        new(SettingScope.Folder, $"{mailboxId}:{folderId}");

    public override string ToString() =>
        Key is null ? Scope.ToString() : $"{Scope}:{Key}";
}
