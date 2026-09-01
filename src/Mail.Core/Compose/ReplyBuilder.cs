using System.Text;
using MimeKit;

namespace Mail.Core.Compose;

public enum ReplyKind { Reply, ReplyAll, Forward }

/// <summary>A composed draft before it becomes MIME: plain fields the UI edits.</summary>
public sealed record Draft(
    string From,
    IReadOnlyList<string> To,
    IReadOnlyList<string> Cc,
    IReadOnlyList<string> Bcc,
    string Subject,
    string Body,
    string? InReplyTo = null,
    IReadOnlyList<string>? References = null,
    IReadOnlyList<DraftAttachment>? Attachments = null);

public sealed record DraftAttachment(string FileName, string ContentType, byte[] Content);

/// <summary>
/// Builds replies and forwards from an original message, and turns a
/// <see cref="Draft"/> into RFC 5322 MIME.
///
/// Threading is done properly: In-Reply-To carries the parent's Message-ID and
/// References appends it to the parent's own chain (RFC 5322 §3.6.4), so replies
/// thread correctly in every client — not just ours.
/// </summary>
public static class ReplyBuilder
{
    const int MaxReferences = 20; // keep headers sane on long threads

    public static Draft BuildReply(MimeMessage original, string fromAddress, ReplyKind kind)
    {
        var to = new List<string>();
        var cc = new List<string>();

        if (kind == ReplyKind.Forward)
        {
            // Forward carries no recipients; the user picks them.
        }
        else
        {
            // Reply-To wins over From when the sender asked for it.
            var replyTargets = original.ReplyTo.Mailboxes.Any()
                ? original.ReplyTo.Mailboxes
                : original.From.Mailboxes;
            to.AddRange(replyTargets.Select(m => m.Address));

            if (kind == ReplyKind.ReplyAll)
            {
                // Everyone else on the thread, minus ourselves and anyone already in To.
                foreach (var mailbox in original.To.Mailboxes.Concat(original.Cc.Mailboxes))
                {
                    if (SameAddress(mailbox.Address, fromAddress)) continue;
                    if (to.Any(t => SameAddress(t, mailbox.Address))) continue;
                    if (cc.Any(t => SameAddress(t, mailbox.Address))) continue;
                    cc.Add(mailbox.Address);
                }
            }
        }

        var references = new List<string>();
        foreach (var reference in original.References)
            references.Add(reference);
        if (!string.IsNullOrEmpty(original.MessageId) && !references.Contains(original.MessageId))
            references.Add(original.MessageId);
        if (references.Count > MaxReferences)
            references = [.. references.Take(1), .. references.Skip(references.Count - (MaxReferences - 1))];

        return new Draft(
            From: fromAddress,
            To: to,
            Cc: cc,
            Bcc: [],
            Subject: PrefixSubject(original.Subject, kind),
            Body: QuoteBody(original, kind),
            InReplyTo: kind == ReplyKind.Forward ? null : original.MessageId,
            References: kind == ReplyKind.Forward ? [] : references,
            Attachments: []);
    }

    public static bool SameAddress(string a, string b) =>
        string.Equals(a?.Trim(), b?.Trim(), StringComparison.OrdinalIgnoreCase);

    /// <summary>Adds Re:/Fwd: unless it is already there (in any common form).</summary>
    public static string PrefixSubject(string? subject, ReplyKind kind)
    {
        var text = (subject ?? "").Trim();
        var prefix = kind == ReplyKind.Forward ? "Fwd: " : "Re: ";
        var existing = kind == ReplyKind.Forward
            ? new[] { "fwd:", "fw:" }
            : new[] { "re:", "aw:", "sv:" };
        foreach (var candidate in existing)
            if (text.StartsWith(candidate, StringComparison.OrdinalIgnoreCase))
                return text;
        return prefix + text;
    }

    static string QuoteBody(MimeMessage original, ReplyKind kind)
    {
        var body = original.TextBody
            ?? (original.HtmlBody is { } html ? Ingest.HtmlText.ToPlainText(html) : "");
        var sb = new StringBuilder();
        sb.AppendLine();
        sb.AppendLine();
        if (kind == ReplyKind.Forward)
        {
            sb.AppendLine("---------- Forwarded message ----------");
            sb.AppendLine($"From: {Describe(original.From)}");
            sb.AppendLine($"Date: {original.Date.LocalDateTime:f}");
            sb.AppendLine($"Subject: {original.Subject}");
            sb.AppendLine($"To: {Describe(original.To)}");
            if (original.Cc.Mailboxes.Any())
                sb.AppendLine($"Cc: {Describe(original.Cc)}");
            sb.AppendLine();
            sb.AppendLine(body);
        }
        else
        {
            sb.AppendLine($"On {original.Date.LocalDateTime:f}, {Describe(original.From)} wrote:");
            foreach (var line in body.Replace("\r\n", "\n").Split('\n'))
                sb.AppendLine("> " + line);
        }
        return sb.ToString();
    }

    static string Describe(InternetAddressList list) =>
        string.Join(", ", list.Mailboxes.Select(m =>
            string.IsNullOrWhiteSpace(m.Name) ? m.Address : $"{m.Name} <{m.Address}>"));

    /// <summary>Draft to MIME. Throws <see cref="FormatException"/> on a bad address.</summary>
    public static MimeMessage ToMimeMessage(Draft draft, string? fromDisplayName = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromDisplayName ?? "", draft.From));
        AddAll(message.To, draft.To);
        AddAll(message.Cc, draft.Cc);
        AddAll(message.Bcc, draft.Bcc);
        message.Subject = draft.Subject ?? "";
        message.Date = DateTimeOffset.Now;
        // Derive the Message-ID from the sender's own domain. MimeKit defaults to
        // the local machine name (…@johnspc), and a message claiming to be from
        // @hotmail.com while carrying a Message-ID from a nonexistent host looks
        // like a forgery to receiving spam filters — which silently drop it.
        var domain = draft.From.Contains('@')
            ? draft.From[(draft.From.IndexOf('@') + 1)..].Trim()
            : "localhost";
        message.MessageId = MimeKit.Utils.MimeUtils.GenerateMessageId(domain);

        if (!string.IsNullOrEmpty(draft.InReplyTo))
            message.InReplyTo = draft.InReplyTo;
        foreach (var reference in draft.References ?? [])
            message.References.Add(reference);

        var builder = new BodyBuilder { TextBody = draft.Body ?? "" };
        foreach (var attachment in draft.Attachments ?? [])
            builder.Attachments.Add(
                attachment.FileName, attachment.Content, ContentType.Parse(attachment.ContentType));
        message.Body = builder.ToMessageBody();
        return message;
    }

    static void AddAll(InternetAddressList list, IReadOnlyList<string>? addresses)
    {
        foreach (var address in addresses ?? [])
        {
            var trimmed = address.Trim();
            if (trimmed.Length == 0) continue;
            list.Add(MailboxAddress.Parse(trimmed));
        }
    }

    /// <summary>Splits a UI recipient box (comma or semicolon separated).</summary>
    public static IReadOnlyList<string> SplitAddresses(string? text) =>
        string.IsNullOrWhiteSpace(text)
            ? []
            : [.. text.Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)];
}
