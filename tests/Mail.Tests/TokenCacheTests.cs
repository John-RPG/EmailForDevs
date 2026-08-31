using Mail.Sync.Auth;

namespace Mail.Tests;

public sealed class TokenCacheTests : IDisposable
{
    readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));

    public TokenCacheTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* best effort */ }
    }

    [Fact]
    public async Task Missing_and_corrupt_cache_files_start_fresh_instead_of_throwing()
    {
        var path = Path.Combine(_dir, "msal.cache");

        // No file at all: empty account list, no crash.
        var auth = new GraphAuthenticator(path);
        Assert.Empty(await auth.GetAccountsAsync());

        // Garbage that is not a DPAPI blob: discarded, still empty, no crash.
        File.WriteAllBytes(path, [1, 2, 3, 4, 5]);
        var auth2 = new GraphAuthenticator(path);
        Assert.Empty(await auth2.GetAccountsAsync());
    }
}
