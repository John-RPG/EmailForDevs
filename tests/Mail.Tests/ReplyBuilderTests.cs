using Mail.Core.Compose;
using MimeKit;

namespace Mail.Tests;

public sealed class ReplyBuilderTests
{
    static MimeMessage Original(
        string from = "alice@example.com",
        string[]? to = null,
        string[]? cc = null,
        string subject = "Project kickoff",
        string? messageId = "<root@example.com>",
        string[]? references = null,
        string? replyTo = null)
    {
        var message = new MimeMessage();
        message.From.Add(new MailboxAddress("Alice Adams", from));
        foreach (var address in to ?? ["bob@example.com"])
            message.To.Add(MailboxAddress.Parse(address));
        foreach (var address in cc ?? [])
            message.Cc.Add(MailboxAddress.Parse(address));
        if (replyTo is not null)
            message.ReplyTo.Add(MailboxAddress.Parse(replyTo));
        message.Subject = subject;
        message.MessageId = messageId;
        message.Date = DateTimeOffset.Parse("2026-08-30T10:00:00Z");
        foreach (var reference in references ?? [])
            message.References.Add(reference);
        message.Body = new TextPart("plain") { Text = "Shall we start Monday?" };
        return message;
    }

    [Fact]
    public void Reply_targets_sender_and_threads_correctly()
    {
        var draft = ReplyBuilder.BuildReply(Original(), "bob@example.com", ReplyKind.Reply);

        Assert.Equal(["alice@example.com"], draft.To);
        Assert.Empty(draft.Cc);
        Assert.Equal("Re: Project kickoff", draft.Subject);
        Assert.Equal("root@example.com", draft.InReplyTo);
        Assert.Equal(["root@example.com"], draft.References);
        Assert.Contains("> Shall we start Monday?", draft.Body);
    }

    [Fact]
    public void Reply_all_keeps_others_and_excludes_self()
    {
        var original = Original(
            to: ["bob@example.com", "carol@example.com"],
            cc: ["dave@example.com"]);
        var draft = ReplyBuilder.BuildReply(original, "bob@example.com", ReplyKind.ReplyAll);

        Assert.Equal(["alice@example.com"], draft.To);
        Assert.Contains("carol@example.com", draft.Cc);
        Assert.Contains("dave@example.com", draft.Cc);
        Assert.DoesNotContain(draft.Cc, a => a.Contains("bob@"));
    }

    [Fact]
    public void Reply_honours_reply_to_header()
    {
        var original = Original(replyTo: "support@example.com");
        var draft = ReplyBuilder.BuildReply(original, "bob@example.com", ReplyKind.Reply);
        Assert.Equal(["support@example.com"], draft.To);
    }

    [Fact]
    public void References_chain_appends_parent()
    {
        var original = Original(
            messageId: "<third@example.com>",
            references: ["<first@example.com>", "<second@example.com>"]);
        var draft = ReplyBuilder.BuildReply(original, "bob@example.com", ReplyKind.Reply);

        Assert.Equal(
            ["first@example.com", "second@example.com", "third@example.com"],
            draft.References);
        Assert.Equal("third@example.com", draft.InReplyTo);
    }

    [Fact]
    public void Long_reference_chains_are_trimmed_keeping_root_and_recent()
    {
        var references = Enumerable.Range(1, 40).Select(i => $"<m{i}@example.com>").ToArray();
        var original = Original(messageId: "<latest@example.com>", references: references);
        var draft = ReplyBuilder.BuildReply(original, "bob@example.com", ReplyKind.Reply);

        Assert.Equal(20, draft.References!.Count);
        Assert.Equal("m1@example.com", draft.References[0]);            // root survives
        Assert.Equal("latest@example.com", draft.References[^1]);       // parent survives
    }

    [Fact]
    public void Subject_prefixes_are_not_doubled()
    {
        Assert.Equal("Re: Hello", ReplyBuilder.PrefixSubject("Hello", ReplyKind.Reply));
        Assert.Equal("Re: Hello", ReplyBuilder.PrefixSubject("Re: Hello", ReplyKind.Reply));
        Assert.Equal("RE: Hello", ReplyBuilder.PrefixSubject("RE: Hello", ReplyKind.Reply));
        Assert.Equal("Fwd: Hello", ReplyBuilder.PrefixSubject("Hello", ReplyKind.Forward));
        Assert.Equal("Fwd: Hello", ReplyBuilder.PrefixSubject("Fwd: Hello", ReplyKind.Forward));
        // A reply to a forward gets Re:, not a doubled Fwd:.
        Assert.Equal("Re: Fwd: Hello", ReplyBuilder.PrefixSubject("Fwd: Hello", ReplyKind.Reply));
    }

    [Fact]
    public void Forward_has_no_recipients_and_no_threading_headers()
    {
        var draft = ReplyBuilder.BuildReply(Original(), "bob@example.com", ReplyKind.Forward);
        Assert.Empty(draft.To);
        Assert.Null(draft.InReplyTo);
        Assert.Empty(draft.References!);
        Assert.Contains("Forwarded message", draft.Body);
        Assert.Contains("Shall we start Monday?", draft.Body);
    }

    [Fact]
    public void Draft_serializes_to_valid_mime_with_headers()
    {
        var draft = new Draft(
            From: "bob@example.com",
            To: ["alice@example.com", "carol@example.com"],
            Cc: [],
            Bcc: ["secret@example.com"],
            Subject: "Re: Project kickoff",
            Body: "Monday works.",
            InReplyTo: "<root@example.com>",
            References: ["<root@example.com>"],
            Attachments: [new DraftAttachment("notes.txt", "text/plain", "hello"u8.ToArray())]);

        var message = ReplyBuilder.ToMimeMessage(draft, "Bob Brown");
        using var stream = new MemoryStream();
        message.WriteTo(stream);
        var reparsed = MimeMessage.Load(new MemoryStream(stream.ToArray()));

        Assert.Equal("bob@example.com", reparsed.From.Mailboxes.Single().Address);
        Assert.Equal(2, reparsed.To.Mailboxes.Count());
        Assert.Equal("root@example.com", reparsed.InReplyTo);
        Assert.Equal(["root@example.com"], reparsed.References.ToArray());
        // On the wire the headers must carry angle brackets (RFC 5322).
        var wire = System.Text.Encoding.ASCII.GetString(stream.ToArray());
        Assert.Contains("In-Reply-To: <root@example.com>", wire);
        Assert.Contains("References: <root@example.com>", wire);
        Assert.False(string.IsNullOrEmpty(reparsed.MessageId));
        Assert.Equal("Monday works.", reparsed.TextBody!.Trim());
        Assert.Single(reparsed.Attachments);
    }

    [Fact]
    public void Message_id_uses_the_sender_domain_not_the_machine_name()
    {
        var draft = new Draft("bob@example.com", ["alice@example.com"], [], [], "Hi", "Body");
        var message = ReplyBuilder.ToMimeMessage(draft);
        // A Message-ID from a nonexistent host (the local machine name) makes
        // receiving spam filters drop the message silently.
        Assert.EndsWith("@example.com", message.MessageId);
        Assert.DoesNotContain(Environment.MachineName.ToLowerInvariant(),
            message.MessageId.ToLowerInvariant());
    }

    [Fact]
    public void Address_splitting_handles_commas_semicolons_and_spaces()
    {
        Assert.Equal(
            ["a@x.com", "b@x.com", "c@x.com"],
            ReplyBuilder.SplitAddresses(" a@x.com, b@x.com ; c@x.com "));
        Assert.Empty(ReplyBuilder.SplitAddresses(""));
        Assert.Empty(ReplyBuilder.SplitAddresses(null));
    }

    [Fact]
    public void Invalid_address_is_rejected_rather_than_silently_dropped()
    {
        var draft = new Draft("bob@example.com", ["not an address"], [], [], "Hi", "Body");
        Assert.ThrowsAny<Exception>(() => ReplyBuilder.ToMimeMessage(draft));
    }
}
