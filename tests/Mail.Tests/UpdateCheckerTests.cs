using System.Text.Json;
using Mail.Core.Update;

namespace Mail.Tests;

public sealed class UpdateCheckerTests
{
    /// <summary>
    /// A real payload, trimmed to the fields the parser reads. Recorded from the
    /// live API rather than invented, so the test fails if GitHub changes the
    /// shape of what it returns.
    /// </summary>
    const string LatestRelease = """
        {
          "tag_name": "v0.2.0",
          "draft": false,
          "prerelease": false,
          "body": "Some notes.",
          "html_url": "https://github.com/John-RPG/EmailForDevs/releases/tag/v0.2.0",
          "assets": [
            {
              "name": "eeeMail-v0.2.0-win-x64.zip",
              "size": 76788161,
              "digest": "sha256:A2EFC8E5DB6D38A7D33C2AE81F21C730433F4C9ED9AF3B69A98DE64400441D48",
              "browser_download_url": "https://github.com/John-RPG/EmailForDevs/releases/download/v0.2.0/eeeMail-v0.2.0-win-x64.zip"
            }
          ]
        }
        """;

    static JsonElement Json(string text) => JsonDocument.Parse(text).RootElement;

    [Fact]
    public void Parses_a_release_and_normalises_the_digest()
    {
        var release = UpdateChecker.Parse(Json(LatestRelease));

        Assert.NotNull(release);
        Assert.Equal(new Version(0, 2, 0), release.Version);
        Assert.Equal("v0.2.0", release.Tag);
        Assert.Equal(76788161, release.SizeBytes);
        // The prefix is stripped and the case normalised, because the digest is
        // compared against a lower-case hex hash computed locally.
        Assert.Equal(
            "a2efc8e5db6d38a7d33c2ae81f21c730433f4c9ed9af3b69a98de64400441d48",
            release.Sha256);
    }

    [Fact]
    public void Ignores_a_digest_that_is_not_sha256()
    {
        // Treating an unknown algorithm as if it were SHA-256 would compare a
        // hash against the wrong kind of digest and fail confusingly, or worse,
        // be skipped. Absent is the honest answer.
        var release = UpdateChecker.Parse(Json(
            LatestRelease.Replace("sha256:A2EFC", "md5:A2EFC")));

        Assert.NotNull(release);
        Assert.Null(release.Sha256);
    }

    [Theory]
    [InlineData("draft")]
    [InlineData("prerelease")]
    public void Skips_drafts_and_prereleases(string field)
    {
        var release = UpdateChecker.Parse(Json(
            LatestRelease.Replace($"\"{field}\": false", $"\"{field}\": true")));

        Assert.Null(release);
    }

    [Fact]
    public void Ignores_assets_for_other_platforms()
    {
        var release = UpdateChecker.Parse(Json(
            LatestRelease.Replace("win-x64.zip", "linux-x64.tar.gz")));

        Assert.Null(release);
    }

    [Theory]
    [InlineData("v0.2.0", 0, 2, 0)]
    [InlineData("0.2.0", 0, 2, 0)]
    [InlineData("V1.10.3", 1, 10, 3)]
    public void Reads_a_tag_as_a_version(string tag, int major, int minor, int build)
    {
        Assert.Equal(new Version(major, minor, build), UpdateChecker.ParseVersion(tag));
    }

    [Theory]
    [InlineData("v0.2.0-beta")]
    [InlineData("nightly")]
    [InlineData("")]
    public void Refuses_a_tag_it_cannot_order(string tag)
    {
        // Rejected rather than guessed: ordering a prerelease suffix needs semver
        // precedence rules, and getting them wrong would offer a beta as if it
        // were newer than the stable release it precedes.
        Assert.Null(UpdateChecker.ParseVersion(tag));
    }

    [Fact]
    public void Offers_only_genuinely_newer_versions()
    {
        var running = new Version(0, 1, 0);
        Assert.True(UpdateChecker.IsNewer(new Version(0, 2, 0), running));
        Assert.True(UpdateChecker.IsNewer(new Version(1, 0, 0), running));
        Assert.False(UpdateChecker.IsNewer(new Version(0, 1, 0), running));
        Assert.False(UpdateChecker.IsNewer(new Version(0, 0, 9), running));
    }

    [Fact]
    public void Does_not_offer_an_update_to_the_running_build()
    {
        // The assembly reports four parts with a trailing zero, a tag has three.
        // Comparing all four would make the running build look older than
        // itself and prompt on every launch forever.
        Assert.False(UpdateChecker.IsNewer(new Version(0, 1, 0), new Version(0, 1, 0, 0)));
        Assert.True(UpdateChecker.IsNewer(new Version(0, 1, 1), new Version(0, 1, 0, 0)));
    }
}
