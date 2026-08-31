using System.Text;
using MimeKit;

namespace Mail.Core.Ingest;

/// <summary>
/// Splits a raw RFC 5322 message into byte ranges that tile it exactly, marking
/// large leaf-part bodies (attachments, signature images, logos — usually
/// identically base64-encoded on every send) as dedup candidates for the
/// content-addressed blob store. Reconstruction is pure concatenation, so raw
/// storage stays byte-exact no matter how boundaries are chosen; any structural
/// surprise degrades to fewer, larger skeleton segments — never to corruption.
/// </summary>
public static class MimeSegmenter
{
    public const int DedupThresholdBytes = 4096;

    public static IReadOnlyList<RawSegment> Segment(byte[] raw, MimeMessage message)
    {
        var dedup = new List<(int Start, int Length)>();
        var bodyStart = FindHeaderEnd(raw, 0, raw.Length);
        if (bodyStart >= 0 && message.Body is not null)
            Descend(raw, message.Body, bodyStart, raw.Length, dedup);
        return Tile(raw.Length, dedup);
    }

    static void Descend(byte[] raw, MimeEntity entity, int start, int end, List<(int, int)> dedup)
    {
        if (entity is Multipart multipart && !string.IsNullOrEmpty(multipart.Boundary))
        {
            var parts = ScanParts(raw, start, end, multipart.Boundary);
            if (parts.Count != multipart.Count) return; // structure mismatch → leave as skeleton
            for (var i = 0; i < parts.Count; i++)
            {
                var (partStart, partEnd) = parts[i];
                if (multipart[i] is Multipart)
                {
                    Descend(raw, multipart[i], partStart, partEnd, dedup);
                }
                else
                {
                    var headerEnd = FindHeaderEnd(raw, partStart, partEnd);
                    if (headerEnd >= 0 && partEnd - headerEnd >= DedupThresholdBytes)
                        dedup.Add((headerEnd, partEnd - headerEnd));
                }
            }
        }
        else if (entity is not Multipart && end - start >= DedupThresholdBytes)
        {
            dedup.Add((start, end - start));
        }
    }

    /// <summary>Child ranges between "--boundary" delimiter lines; preamble/epilogue stay skeleton.</summary>
    static List<(int Start, int End)> ScanParts(byte[] raw, int start, int end, string boundary)
    {
        var parts = new List<(int, int)>();
        var delimiter = Encoding.ASCII.GetBytes("--" + boundary);
        var pos = start;
        int? partStart = null;
        while (pos < end)
        {
            var lineStart = pos;
            var lineEnd = NextLine(raw, pos, end);
            if (MatchesAt(raw, lineStart, end, delimiter))
            {
                if (partStart is int s) parts.Add((s, lineStart));
                var after = lineStart + delimiter.Length;
                var isClosing = after + 1 < end && raw[after] == (byte)'-' && raw[after + 1] == (byte)'-';
                if (isClosing) return parts;
                partStart = lineEnd;
            }
            pos = lineEnd;
        }
        return parts;
    }

    static int NextLine(byte[] raw, int pos, int end)
    {
        var idx = Array.IndexOf(raw, (byte)'\n', pos, end - pos);
        return idx < 0 ? end : idx + 1;
    }

    static bool MatchesAt(byte[] raw, int pos, int end, byte[] prefix)
    {
        if (pos + prefix.Length > end) return false;
        for (var i = 0; i < prefix.Length; i++)
            if (raw[pos + i] != prefix[i])
                return false;
        return true;
    }

    /// <summary>Index just past the blank line ending a header block, or -1.</summary>
    static int FindHeaderEnd(byte[] raw, int start, int end)
    {
        for (var i = start; i + 1 < end; i++)
        {
            if (raw[i] == (byte)'\n')
            {
                if (raw[i + 1] == (byte)'\n') return i + 2;
                if (i + 2 < end && raw[i + 1] == (byte)'\r' && raw[i + 2] == (byte)'\n') return i + 3;
            }
        }
        return -1;
    }

    static IReadOnlyList<RawSegment> Tile(int totalLength, List<(int Start, int Length)> dedup)
    {
        dedup.Sort((a, b) => a.Start.CompareTo(b.Start));
        var segments = new List<RawSegment>();
        var cursor = 0;
        foreach (var (start, length) in dedup)
        {
            if (start < cursor) continue; // overlap safety: keep earlier range, skip this one
            if (start > cursor) segments.Add(new RawSegment(cursor, start - cursor, Dedup: false));
            segments.Add(new RawSegment(start, length, Dedup: true));
            cursor = start + length;
        }
        if (cursor < totalLength || segments.Count == 0)
            segments.Add(new RawSegment(cursor, totalLength - cursor, Dedup: false));
        return segments;
    }
}
