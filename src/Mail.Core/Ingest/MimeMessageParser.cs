using MimeKit;

namespace Mail.Core.Ingest;

/// <summary>Raw RFC 5322 bytes → everything storage needs (via MimeKit).</summary>
public static class MimeMessageParser
{
    const int PreviewLength = 200;

    public static ParsedMessage Parse(byte[] raw)
    {
        using var stream = new MemoryStream(raw, writable: false);
        var message = MimeMessage.Load(stream);

        var addresses = new List<MessageAddress>();
        AddMailboxes(addresses, message.From, AddressKind.From);
        if (message.Sender is not null)
            Add(addresses, message.Sender, AddressKind.Sender);
        AddMailboxes(addresses, message.To, AddressKind.To);
        AddMailboxes(addresses, message.Cc, AddressKind.Cc);
        AddMailboxes(addresses, message.Bcc, AddressKind.Bcc);
        AddMailboxes(addresses, message.ReplyTo, AddressKind.ReplyTo);

        var references = new List<string>();
        if (!string.IsNullOrEmpty(message.InReplyTo))
            references.Add(message.InReplyTo);
        foreach (var reference in message.References)
            if (!references.Contains(reference))
                references.Add(reference);

        var bodyText = message.TextBody;
        if (string.IsNullOrWhiteSpace(bodyText) && message.HtmlBody is { } html)
            bodyText = HtmlText.ToPlainText(html);

        var attachments = new List<AttachmentContent>();
        foreach (var entity in message.BodyParts)
        {
            if (entity is not MimePart part) continue;
            var isInline = !part.IsAttachment && part.ContentId is not null;
            if (!part.IsAttachment && !isInline) continue;
            using var content = new MemoryStream();
            part.Content?.DecodeTo(content);
            attachments.Add(new AttachmentContent(
                part.FileName, part.ContentType.MimeType, isInline, part.ContentId, content.ToArray()));
        }

        return new ParsedMessage(
            Subject: message.Subject,
            MessageId: string.IsNullOrEmpty(message.MessageId) ? null : message.MessageId,
            References: references,
            SentAt: message.Date == DateTimeOffset.MinValue ? null : message.Date,
            BodyText: bodyText,
            Preview: MakePreview(bodyText),
            HasAttachments: attachments.Any(a => !a.IsInline),
            Addresses: addresses,
            Attachments: attachments,
            Segments: MimeSegmenter.Segment(raw, message));
    }

    static void AddMailboxes(List<MessageAddress> list, InternetAddressList source, AddressKind kind)
    {
        foreach (var mailbox in source.Mailboxes)
            Add(list, mailbox, kind);
    }

    static void Add(List<MessageAddress> list, MailboxAddress mailbox, AddressKind kind) =>
        list.Add(new MessageAddress(kind, mailbox.Address,
            string.IsNullOrWhiteSpace(mailbox.Name) ? null : mailbox.Name));

    static string? MakePreview(string? bodyText)
    {
        if (string.IsNullOrWhiteSpace(bodyText)) return null;
        var collapsed = string.Join(' ',
            bodyText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries));
        return collapsed.Length <= PreviewLength ? collapsed : collapsed[..PreviewLength];
    }
}
