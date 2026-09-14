using System.Net;
using Mail.Core.Update;

namespace Mail.Tests;

public sealed class UpdateInstallerTests : IDisposable
{
    readonly string _dir = Path.Combine(
        AppContext.BaseDirectory, "testdata", Guid.NewGuid().ToString("N"));

    public UpdateInstallerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch (IOException) { }
    }

    /// <summary>Serves fixed bytes, so a download can be tested without a network.</summary>
    sealed class StubHandler(byte[] payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(payload),
            });
    }

    static ReleaseInfo Release(string? sha256, long size = 4) => new(
        new Version(0, 2, 0), "v0.2.0", "eeeMail-v0.2.0-win-x64.zip",
        "https://example.com/asset.zip", size, sha256, "notes", "https://example.com");

    [Fact]
    public async Task Keeps_a_download_whose_digest_matches()
    {
        var payload = "not really a zip"u8.ToArray();
        var expected = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(payload)).ToLowerInvariant();

        using var http = new HttpClient(new StubHandler(payload));
        var installer = new UpdateInstaller(http);

        var path = await installer.DownloadAsync(Release(expected), _dir);

        Assert.True(File.Exists(path));
        Assert.Equal(expected, await UpdateInstaller.Sha256Async(path));
    }

    [Fact]
    public async Task Rejects_and_deletes_a_download_that_does_not_match()
    {
        // The whole point of verifying: a file that is not what GitHub published
        // must not survive on disk where it could later be run.
        using var http = new HttpClient(new StubHandler("tampered"u8.ToArray()));
        var installer = new UpdateInstaller(http);
        var wrong = new string('a', 64);

        var error = await Assert.ThrowsAsync<UpdateVerificationException>(
            () => installer.DownloadAsync(Release(wrong), _dir));

        Assert.Contains("did not match", error.Message);
        Assert.Empty(Directory.GetFiles(_dir, "*.zip"));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("tooshort")]
    public async Task Refuses_to_install_what_it_cannot_verify(string? digest)
    {
        // Refused rather than installed on trust. An unverifiable download is
        // the case this whole step exists to catch, so falling back to
        // "install anyway" would defeat it.
        using var http = new HttpClient(new StubHandler("anything"u8.ToArray()));
        var installer = new UpdateInstaller(http);

        var error = await Assert.ThrowsAsync<UpdateVerificationException>(
            () => installer.DownloadAsync(Release(digest), _dir));

        Assert.Contains("cannot be verified", error.Message);
        // Nothing was fetched, so nothing needs cleaning up.
        Assert.Empty(Directory.GetFiles(_dir));
    }

    [Fact]
    public async Task Hashes_a_file_the_same_way_github_reports_it()
    {
        // Pinned against a published reference value rather than against our own
        // implementation, which would only prove it is self-consistent.
        var path = Path.Combine(_dir, "abc.txt");
        await File.WriteAllBytesAsync(path, "abc"u8.ToArray());

        Assert.Equal(
            "ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad",
            await UpdateInstaller.Sha256Async(path));
    }
}
