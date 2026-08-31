namespace Mail.Core.Ingest;

/// <summary>Matches message_addresses.kind in the mailbox schema and the search compiler.</summary>
public enum AddressKind { From = 0, To = 1, Cc = 2, Bcc = 3, ReplyTo = 4, Sender = 5 }

public sealed record MessageAddress(AddressKind Kind, string Email, string? DisplayName);

public sealed record AttachmentContent(
    string? FileName, string ContentType, bool IsInline, string? ContentId, byte[] Content);

/// <summary>A byte range of the raw message; Dedup ranges are content-addressed candidates.</summary>
public sealed record RawSegment(int Offset, int Length, bool Dedup);

/// <summary>Everything ingestion extracts from one raw RFC 5322 message.</summary>
public sealed record ParsedMessage(
    string? Subject,
    string? MessageId,
    IReadOnlyList<string> References,
    DateTimeOffset? SentAt,
    string? BodyText,
    string? Preview,
    bool HasAttachments,
    IReadOnlyList<MessageAddress> Addresses,
    IReadOnlyList<AttachmentContent> Attachments,
    IReadOnlyList<RawSegment> Segments);
