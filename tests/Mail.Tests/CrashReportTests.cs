using Mail.Core.Diagnostics;

namespace Mail.Tests;

public sealed class CrashReportTests
{
    static Exception Thrown(Func<Exception> make)
    {
        // Thrown rather than constructed, so there is a real stack trace to redact.
        try { throw make(); }
        catch (Exception caught) { return caught; }
    }

    [Fact]
    public void Redacts_email_addresses()
    {
        // The reason this class exists. A stack trace from a mail client can
        // carry the user's address and a correspondent's, and the report is
        // meant for a public issue tracker.
        var redacted = CrashReport.Redact(
            "Failed sending to john.hadlow@example.com from shared@example.org");

        Assert.DoesNotContain("john.hadlow@example.com", redacted);
        Assert.DoesNotContain("shared@example.org", redacted);
        Assert.Contains("<email>", redacted);
    }

    [Fact]
    public void Redacts_user_profile_paths()
    {
        // Carries the account name, and appears in almost every stack trace
        // through the data directory.
        var redacted = CrashReport.Redact(@"at Open(""C:\Users\SomeUser\AppData\Roaming\eeeMail"")");

        Assert.DoesNotContain("SomeUser", redacted);
        Assert.Contains("<user>", redacted);
    }

    [Fact]
    public void Redacts_the_machine_name()
    {
        var redacted = CrashReport.Redact($"Host {Environment.MachineName} refused");

        Assert.DoesNotContain(Environment.MachineName, redacted);
        Assert.Contains("<machine>", redacted);
    }

    [Fact]
    public void Keeps_the_detail_that_makes_a_report_useful()
    {
        // Over-redacting would leave a report nobody can act on. The type, the
        // message and the frames have to survive.
        var report = CrashReport.Build(
            Thrown(() => new InvalidOperationException("DialogResult can be set only after Window is created")),
            "0.1.5");

        Assert.Contains("InvalidOperationException", report);
        Assert.Contains("DialogResult", report);
        Assert.Contains("eeeMail 0.1.5", report);
        Assert.Contains("CrashReportTests", report);   // a real frame survived
    }

    [Fact]
    public void Includes_inner_exceptions()
    {
        // The outer exception is often just "an error occurred"; the cause is
        // the part worth reading.
        var inner = Thrown(() => new IOException("the file is locked"));
        var report = CrashReport.Build(
            new InvalidOperationException("Save failed", inner), "0.1.5");

        Assert.Contains("Save failed", report);
        Assert.Contains("the file is locked", report);
        Assert.Contains("caused by", report);
    }

    [Fact]
    public void User_notes_are_redacted_too()
    {
        // Someone describing what they did will name the address they sent to.
        var report = CrashReport.Build(
            Thrown(() => new Exception("boom")), "0.1.5",
            userNotes: "I replied to dave@example.com and it died");

        Assert.DoesNotContain("dave@example.com", report);
        Assert.Contains("<email>", report);
    }

    [Fact]
    public void Issue_url_carries_no_credential_and_stays_openable()
    {
        // The browser route is the whole point: GitHub authenticates the user in
        // their own session, so the app ships no token. It also has to produce a
        // URL that actually opens, hence the length cap.
        var url = CrashReport.IssueUrl(
            "John-RPG/EmailForDevs", "Crash: boom", new string('x', 20_000));

        Assert.StartsWith("https://github.com/John-RPG/EmailForDevs/issues/new", url);
        Assert.DoesNotContain("token", url, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("api.github.com", url);
        Assert.True(url.Length < 8_000, $"URL is {url.Length} characters; browsers will refuse it.");
    }

    [Fact]
    public void Title_is_one_short_line()
    {
        // It becomes an issue title, so a stack trace pasted into it is useless.
        var title = CrashReport.Title(
            new InvalidOperationException("something\r\nacross\r\nseveral lines " + new string('y', 200)));

        Assert.DoesNotContain("\n", title);
        Assert.True(title.Length < 140, $"Title is {title.Length} characters.");
        Assert.Contains("InvalidOperationException", title);
    }
}
