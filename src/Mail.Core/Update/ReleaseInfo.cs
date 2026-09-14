namespace Mail.Core.Update;

/// <summary>
/// A published release, as much of it as the updater needs. Parsed from the
/// GitHub releases API rather than from the HTML page, so nothing here depends
/// on the shape of a web page that can change without notice.
/// </summary>
/// <param name="Version">
/// The tag with any leading "v" removed, parsed. The tag is the authority on
/// what version a release is; the assembly version of the running process is
/// what it gets compared against.
/// </param>
/// <param name="Tag">The raw tag, for display and for building URLs.</param>
/// <param name="AssetName">File name of the Windows asset.</param>
/// <param name="DownloadUrl">Direct download for that asset.</param>
/// <param name="SizeBytes">Expected size, so a truncated download is caught early.</param>
/// <param name="Sha256">
/// Lower-case hex digest GitHub reports for the asset, without the "sha256:"
/// prefix. Null when the release predates GitHub publishing digests, which the
/// caller must treat as "cannot verify" rather than "verified".
/// </param>
/// <param name="Notes">Release body, shown to the user before they consent.</param>
/// <param name="HtmlUrl">The release page, for "view details".</param>
public sealed record ReleaseInfo(
    Version Version,
    string Tag,
    string AssetName,
    string DownloadUrl,
    long SizeBytes,
    string? Sha256,
    string Notes,
    string HtmlUrl);
