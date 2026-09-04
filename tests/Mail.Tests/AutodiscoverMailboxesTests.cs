using System.Net;
using System.Reflection;
using Mail.Sync.Graph;

namespace Mail.Tests;

/// <summary>
/// Parser tests over the real shape Exchange Online returns. The response is
/// awkward in two ways worth pinning down: the AlternateMailboxes value can
/// arrive either as nested elements or as an escaped XML string, and the entries
/// carry no namespace of their own.
/// </summary>
public sealed class AutodiscoverMailboxesTests
{
    /// <summary>Trimmed from an actual GetUserSettings response.</summary>
    const string NestedResponse = """
        <?xml version="1.0" encoding="utf-8"?>
        <s:Envelope xmlns:s="http://schemas.xmlsoap.org/soap/envelope/">
          <s:Body>
            <GetUserSettingsResponseMessage xmlns="http://schemas.microsoft.com/exchange/2010/Autodiscover">
              <Response>
                <UserResponses>
                  <UserResponse>
                    <UserSettings>
                      <UserSetting>
                        <Name>AlternateMailboxes</Name>
                        <AlternateMailboxes>
                          <AlternateMailbox>
                            <Type>Delegate</Type>
                            <DisplayName>MGS Helpdesk</DisplayName>
                            <LegacyDN>/o=ExchangeLabs/cn=helpdesk</LegacyDN>
                            <SmtpAddress>helpdesk@sd.example.com</SmtpAddress>
                          </AlternateMailbox>
                          <AlternateMailbox>
                            <Type>Archive</Type>
                            <DisplayName>In-Place Archive</DisplayName>
                            <LegacyDN>/o=ExchangeLabs/cn=archive</LegacyDN>
                            <SmtpAddress>archive@example.com</SmtpAddress>
                          </AlternateMailbox>
                        </AlternateMailboxes>
                      </UserSetting>
                    </UserSettings>
                  </UserResponse>
                </UserResponses>
              </Response>
            </GetUserSettingsResponseMessage>
          </s:Body>
        </s:Envelope>
        """;

    static IReadOnlyList<AutodiscoverMailboxes.AlternateMailbox> Parse(string body) =>
        (IReadOnlyList<AutodiscoverMailboxes.AlternateMailbox>)
        typeof(AutodiscoverMailboxes)
            .GetMethod("Parse", BindingFlags.NonPublic | BindingFlags.Static)!
            .Invoke(null, [body])!;

    [Fact]
    public void Parses_nested_alternate_mailboxes()
    {
        var mailboxes = Parse(NestedResponse);

        Assert.Equal(2, mailboxes.Count);
        var helpdesk = mailboxes[0];
        Assert.Equal("Delegate", helpdesk.Type);
        Assert.Equal("MGS Helpdesk", helpdesk.DisplayName);
        // Cross-domain: a directory search of the account's own domain would
        // never have surfaced this one.
        Assert.Equal("helpdesk@sd.example.com", helpdesk.SmtpAddress);
    }

    [Fact]
    public void Keeps_archives_so_the_caller_can_decide()
    {
        // The parser reports what Exchange said; filtering archives out is
        // discovery's decision, not the parser's.
        Assert.Contains(Parse(NestedResponse), m =>
            m.Type.Equals("Archive", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Entries_without_an_address_are_skipped()
    {
        var body = NestedResponse.Replace(
            "<SmtpAddress>helpdesk@sd.example.com</SmtpAddress>", "");
        Assert.Single(Parse(body));
    }

    [Fact]
    public void Malformed_xml_yields_nothing_rather_than_throwing()
    {
        // A failure here must not break the picker: typed-address entry has to
        // keep working when discovery cannot.
        Assert.Empty(Parse("not xml at all"));
        Assert.Empty(Parse("<Envelope><unclosed>"));
    }

    [Fact]
    public async Task Failed_request_returns_empty_not_an_exception()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.Unauthorized));
        var result = await new AutodiscoverMailboxes(http)
            .GetAlternateMailboxesAsync("john@example.com", "token");
        Assert.Empty(result);
    }

    [Fact]
    public async Task Successful_request_is_parsed()
    {
        using var http = new HttpClient(new StubHandler(HttpStatusCode.OK, NestedResponse));
        var result = await new AutodiscoverMailboxes(http)
            .GetAlternateMailboxesAsync("john@example.com", "token");
        Assert.Equal(2, result.Count);
    }

    sealed class StubHandler(HttpStatusCode status, string body = "") : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(status)
            {
                Content = new StringContent(body),
            });
    }
}
