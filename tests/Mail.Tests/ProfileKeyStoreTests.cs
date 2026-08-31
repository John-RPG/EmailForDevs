using Mail.Storage.Security;

namespace Mail.Tests;

public sealed class ProfileKeyStoreTests : IDisposable
{
    readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public void Create_then_unlock_returns_same_key()
    {
        var store = new ProfileKeyStore(_dir);
        var created = store.Create();
        Assert.Equal(ProfileKeyStore.KeySizeBytes, created.MasterKey.Length);
        Assert.Equal(created.MasterKey, store.Unlock());
    }

    [Fact]
    public void Recover_after_dpapi_loss_restores_key_and_self_heals()
    {
        var store = new ProfileKeyStore(_dir);
        var created = store.Create();

        // Simulate a dead Windows profile: DPAPI-wrapped key gone, recovery blob survives.
        File.Delete(store.MasterKeyPath);
        Assert.Throws<ProfileKeyUnavailableException>(() => store.Unlock());

        var recovered = store.Recover(created.RecoveryCode);
        Assert.Equal(created.MasterKey, recovered);

        // Recover() must have rewritten master.key so normal unlock works again.
        Assert.Equal(created.MasterKey, store.Unlock());
    }

    [Fact]
    public void Wrong_recovery_code_is_rejected()
    {
        var store = new ProfileKeyStore(_dir);
        store.Create();
        var wrong = RecoveryCode.Generate();
        Assert.Throws<InvalidRecoveryCodeException>(() => store.Recover(wrong));
    }

    [Fact]
    public void Recovery_code_input_is_forgiving()
    {
        var store = new ProfileKeyStore(_dir);
        var created = store.Create();
        // Lowercase, no separators, random whitespace — still accepted.
        var messy = " " + created.RecoveryCode.Replace("-", "").ToLowerInvariant() + " ";
        Assert.Equal(created.MasterKey, store.Recover(messy));
    }

    [Fact]
    public void Rotating_recovery_code_invalidates_the_old_one()
    {
        var store = new ProfileKeyStore(_dir);
        var created = store.Create();
        var newCode = store.RotateRecoveryCode(created.MasterKey);
        Assert.NotEqual(created.RecoveryCode, newCode);
        Assert.Throws<InvalidRecoveryCodeException>(() => store.Recover(created.RecoveryCode));
        Assert.Equal(created.MasterKey, store.Recover(newCode));
    }

    [Fact]
    public void Normalize_maps_lookalike_characters()
    {
        Assert.Equal(new string('0', 16) + new string('1', 16),
            RecoveryCode.Normalize(new string('O', 16) + "IIIILLLL" + new string('1', 8)));
        Assert.Throws<FormatException>(() => RecoveryCode.Normalize("UUUU-" + new string('2', 28)));
        Assert.Throws<FormatException>(() => RecoveryCode.Normalize("2222"));
    }
}
