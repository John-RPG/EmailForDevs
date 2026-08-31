using System.Net;
using System.Text;

namespace Mail.Core.Ingest;

/// <summary>
/// HTML → plain text for FTS indexing and previews. Deliberately simple: drops
/// tags (including script/style content), decodes entities, collapses
/// whitespace. Not a sanitizer — the viewer has its own, stricter pipeline.
/// </summary>
public static class HtmlText
{
    public static string ToPlainText(string html)
    {
        var sb = new StringBuilder(html.Length);
        var i = 0;
        while (i < html.Length)
        {
            if (html[i] == '<')
            {
                var tagEnd = html.IndexOf('>', i);
                if (tagEnd < 0) break;
                var tag = html.AsSpan(i + 1, tagEnd - i - 1);
                if (StartsWithName(tag, "script")) i = SkipToClose(html, tagEnd + 1, "</script");
                else if (StartsWithName(tag, "style")) i = SkipToClose(html, tagEnd + 1, "</style");
                else i = tagEnd + 1;
                sb.Append(' ');
            }
            else
            {
                sb.Append(html[i]);
                i++;
            }
        }
        return CollapseWhitespace(WebUtility.HtmlDecode(sb.ToString()));
    }

    static bool StartsWithName(ReadOnlySpan<char> tag, string name) =>
        tag.StartsWith(name, StringComparison.OrdinalIgnoreCase) &&
        (tag.Length == name.Length || !char.IsLetterOrDigit(tag[name.Length]));

    static int SkipToClose(string html, int from, string closeTag)
    {
        var idx = html.IndexOf(closeTag, from, StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return html.Length;
        var end = html.IndexOf('>', idx);
        return end < 0 ? html.Length : end + 1;
    }

    static string CollapseWhitespace(string s)
    {
        var sb = new StringBuilder(s.Length);
        var pendingSpace = false;
        foreach (var c in s)
        {
            if (char.IsWhiteSpace(c))
            {
                pendingSpace = sb.Length > 0;
            }
            else
            {
                if (pendingSpace) sb.Append(' ');
                pendingSpace = false;
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}
