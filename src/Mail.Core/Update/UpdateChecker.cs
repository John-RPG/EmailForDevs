using System.Net.Http.Json;
using System.Text.Json;

namespace Mail.Core.Update;

/// <summary>
/// Asks GitHub what the latest release is, and decides whether it is newer than
/// what is running.
///
/// Deliberately does not download anything: checking is automatic, but fetching
/// and installing a new binary happens only after the user consents, so the two
/// are separate steps rather than one call that quietly does both.
/// </summary>
public sealed class UpdateChecker(HttpClient http, string owner, string repo)
{
    /// <summary>
    /// The newest release, or null when there is none published. Prereleases and
    /// drafts are skipped: /releases/latest already excludes them, which is why
    /// it is used rather than listing and picking the first.
    /// </summary>
    public async Task<ReleaseInfo?> LatestAsync(CancellationToken ct = default)
    {
        using var request = new HttpRequestMessage(
            HttpMethod.Get, $"https://api.github.com/repos/{owner}/{repo}/releases/latest");
        // Required by the GitHub API; a request without it is rejected outright.
        request.Headers.UserAgent.ParseAdd("eeeMail-updater");
        request.Headers.Accept.ParseAdd("application/vnd.github+json");

        using var response = await http.SendAsync(request, ct);
        // A repo with no releases answers 404, which is a normal answer to
        // "what is the latest release", not a failure worth surfacing.
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadFromJsonAsync<JsonElement>(ct);
        return Parse(json);
    }

    /// <summary>
    /// Builds a <see cref="ReleaseInfo"/> from the API payload. Separate from
    /// the fetch so it can be tested against recorded responses without a
    /// network call.
    /// </summary>
    public static ReleaseInfo? Parse(JsonElement release)
    {
        if (!release.TryGetProperty("tag_name", out var tagNode)) return null;
        var tag = tagNode.GetString();
        if (string.IsNullOrWhiteSpace(tag)) return null;
        if (ParseVersion(tag) is not { } version) return null;

        // Drafts have no usable assets, and prereleases are opt-in rather than
        // something to push at everyone. /releases/latest filters both, but the
        // parse is also used on payloads that have not been filtered.
        if (release.TryGetProperty("draft", out var draft) && draft.GetBoolean()) return null;
        if (release.TryGetProperty("prerelease", out var pre) && pre.GetBoolean()) return null;

        if (!release.TryGetProperty("assets", out var assets)) return null;
        foreach (var asset in assets.EnumerateArray())
        {
            var name = asset.TryGetProperty("name", out var n) ? n.GetString() : null;
            // The Windows build. A release may also carry other platforms later,
            // so the asset is chosen by name rather than by being the only one.
            if (name is null || !name.EndsWith("win-x64.zip", StringComparison.OrdinalIgnoreCase))
                continue;

            var url = asset.TryGetProperty("browser_download_url", out var u) ? u.GetString() : null;
            if (url is null) continue;

            // "sha256:abc..." — the algorithm prefix is stripped, and anything
            // that is not sha256 is treated as absent rather than trusted.
            string? digest = null;
            if (asset.TryGetProperty("digest", out var d) &&
                d.GetString() is { } raw &&
                raw.StartsWith("sha256:", StringComparison.OrdinalIgnoreCase))
                digest = raw["sha256:".Length..].Trim().ToLowerInvariant();

            return new ReleaseInfo(
                version,
                tag,
                name,
                url,
                asset.TryGetProperty("size", out var s) ? s.GetInt64() : 0,
                digest,
                release.TryGetProperty("body", out var b) ? b.GetString() ?? "" : "",
                release.TryGetProperty("html_url", out var h) ? h.GetString() ?? "" : "");
        }
        return null;
    }

    /// <summary>
    /// Reads a tag as a version. Accepts "v0.2.0" and "0.2.0"; anything with a
    /// suffix such as "-beta" is rejected, because comparing those correctly
    /// needs semver precedence rules that <see cref="Version"/> does not have
    /// and guessing would order them wrongly.
    /// </summary>
    public static Version? ParseVersion(string tag)
    {
        var text = tag.Trim();
        if (text.StartsWith('v') || text.StartsWith('V')) text = text[1..];
        return Version.TryParse(text, out var version) ? version : null;
    }

    /// <summary>
    /// Whether <paramref name="candidate"/> should be offered over
    /// <paramref name="running"/>.
    ///
    /// Only the first three components are compared. The build number is left
    /// out because the running assembly reports a four-part version whose last
    /// part is always zero, while a tag has three parts — comparing all four
    /// would make 0.2.0 look older than 0.2.0.0.
    /// </summary>
    public static bool IsNewer(Version candidate, Version running)
    {
        static Version Normalise(Version v) =>
            new(v.Major, v.Minor, v.Build < 0 ? 0 : v.Build);
        return Normalise(candidate) > Normalise(running);
    }
}
