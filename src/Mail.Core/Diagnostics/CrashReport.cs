using System.Text;
using System.Text.RegularExpressions;

namespace Mail.Core.Diagnostics;

/// <summary>
/// Turns an unhandled exception into something safe to post in public.
///
/// A stack trace from a mail client is not innocuous: it can carry the user's
/// own address, a correspondent's, the machine name, and the build path of
/// whoever compiled it. A crash reporter that publishes that verbatim trades one
/// problem for a worse one, so everything here is redacted before it is shown —
/// and the user sees the redacted text before choosing to send it.
/// </summary>
public static partial class CrashReport
{
    /// <summary>
    /// Builds the report body. Deterministic and side-effect free so the exact
    /// text shown to the user is the text that gets posted.
    /// </summary>
    public static string Build(
        Exception exception,
        string version,
        string? userNotes = null,
        string? osDescription = null)
    {
        var report = new StringBuilder();
        report.AppendLine("### What happened");
        report.AppendLine();
        report.AppendLine(string.IsNullOrWhiteSpace(userNotes)
            ? "_(not described)_"
            : Redact(userNotes.Trim()));
        report.AppendLine();
        report.AppendLine("### Version");
        report.AppendLine();
        report.AppendLine($"- eeeMail {version}");
        report.AppendLine($"- {osDescription ?? System.Runtime.InteropServices.RuntimeInformation.OSDescription}");
        report.AppendLine($"- .NET {Environment.Version}");
        report.AppendLine();
        report.AppendLine("### Error");
        report.AppendLine();
        report.AppendLine("```");
        report.AppendLine(Redact(Describe(exception)));
        report.AppendLine("```");
        return report.ToString();
    }

    /// <summary>A one-line summary, for the issue title.</summary>
    public static string Title(Exception exception) =>
        $"{exception.GetType().Name}: {Redact(FirstLine(exception.Message))}";

    static string FirstLine(string text)
    {
        var line = text.ReplaceLineEndings(" ").Trim();
        return line.Length <= 90 ? line : line[..90] + "…";
    }

    /// <summary>
    /// The exception and everything under it. Inner exceptions are included
    /// because the outer one is frequently just "an error occurred".
    /// </summary>
    static string Describe(Exception exception)
    {
        var text = new StringBuilder();
        for (var current = exception; current is not null; current = current.InnerException)
        {
            text.AppendLine($"{current.GetType().FullName}: {current.Message}");
            if (!string.IsNullOrWhiteSpace(current.StackTrace))
                text.AppendLine(current.StackTrace);
            if (current.InnerException is not null)
                text.AppendLine("--- caused by ---");
        }
        return text.ToString().TrimEnd();
    }

    /// <summary>
    /// Removes the things a stack trace should not carry into a public issue.
    /// Conservative on purpose: over-redacting costs a little context, while
    /// under-redacting publishes someone's mail address.
    /// </summary>
    public static string Redact(string text)
    {
        // Addresses first, before the path rules can bite into them.
        text = EmailPattern().Replace(text, "<email>");

        // The user's own profile directory, which carries their account name.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
            text = text.Replace(profile, "<profile>", StringComparison.OrdinalIgnoreCase);

        // Any remaining C:\Users\someone\... that came from another machine —
        // a build path compiled into the trace, for instance.
        text = UserPathPattern().Replace(text, @"$1<user>\");

        var machine = Environment.MachineName;
        if (!string.IsNullOrEmpty(machine) && machine.Length > 2)
            text = text.Replace(machine, "<machine>", StringComparison.OrdinalIgnoreCase);

        return text;
    }

    [GeneratedRegex(@"[A-Za-z0-9._%+\-]+@[A-Za-z0-9.\-]+\.[A-Za-z]{2,}")]
    private static partial Regex EmailPattern();

    [GeneratedRegex(@"([A-Za-z]:\\Users\\)[^\\\r\n""]+\\", RegexOptions.IgnoreCase)]
    private static partial Regex UserPathPattern();

    /// <summary>
    /// A GitHub "new issue" URL with the report prefilled.
    ///
    /// The browser route is what keeps this credential-free: GitHub authenticates
    /// the user in their own session, and nothing is posted until they press
    /// Submit on a page showing exactly what will be sent. Embedding a token to
    /// post through the API would mean shipping a credential in a public,
    /// unsigned binary, which is not a trade worth making.
    /// </summary>
    public static string IssueUrl(string repository, string title, string body)
    {
        // GitHub rejects a URL over roughly 8k, and browsers have their own
        // limits. Truncate the body rather than produce a link that fails to
        // open — the user still has Copy and Save for the whole thing.
        const int BodyLimit = 6000;
        if (body.Length > BodyLimit)
            body = body[..BodyLimit] + "\n\n_(truncated — use Copy in eeeMail for the full report)_";

        return $"https://github.com/{repository}/issues/new" +
               $"?title={Uri.EscapeDataString(title)}" +
               $"&body={Uri.EscapeDataString(body)}" +
               "&labels=crash";
    }
}
