using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Xml.Linq;

namespace Mail.Sync.Graph;

/// <summary>
/// Asks Exchange which mailboxes are mapped to the signed-in user, the way
/// Outlook does.
///
/// Graph has no endpoint that answers "which mailboxes may I open?" — Exchange
/// keeps that as an ACL on each mailbox and exposes no way to enumerate the
/// grants pointing at a user. The obvious workaround, trying each candidate and
/// seeing what succeeds, is enumeration: it fills the tenant's audit log with
/// failed access attempts and looks like reconnaissance regardless of intent.
///
/// Outlook avoids that entirely. Exchange records full-access grants in the
/// directory (msExchDelegateListLink / …BL), Autodiscover turns them into an
/// AlternateMailboxes list, and Outlook reads it in one authenticated call —
/// which is why an automapped mailbox appears without a restart. This does the
/// same thing: one request, per account, that asks only about the caller.
///
/// The catch is the audience: Autodiscover is an Exchange resource, so it needs
/// a token for outlook.office365.com (EWS.AccessAsUser.All), not a Graph token.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class AutodiscoverMailboxes(HttpClient http)
{
    const string Endpoint = "https://outlook.office365.com/autodiscover/autodiscover.svc";
    static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    static readonly XNamespace Auto = "http://schemas.microsoft.com/exchange/2010/Autodiscover";

    /// <param name="Type">Exchange's own word for the relationship: "Delegate"
    /// for automapped full-access mailboxes, "Archive" for online archives.</param>
    public sealed record AlternateMailbox(
        string Type, string DisplayName, string SmtpAddress, string LegacyDn);

    /// <summary>
    /// The mailboxes Exchange says are mapped to this user. Returns an empty
    /// list when the service declines rather than throwing: discovery is a
    /// convenience, and the typed-address path must keep working without it.
    /// </summary>
    public async Task<IReadOnlyList<AlternateMailbox>> GetAlternateMailboxesAsync(
        string userAddress, string ewsAccessToken, CancellationToken ct = default)
    {
        var envelope = BuildRequest(userAddress);
        using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint)
        {
            Content = new StringContent(envelope, Encoding.UTF8, "text/xml"),
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ewsAccessToken);
        // Autodiscover is picky about the SOAP action; without it the service
        // answers 500 rather than a useful fault.
        request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml")
        {
            CharSet = "utf-8",
        };

        using var response = await http.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) return [];

        var body = await response.Content.ReadAsStringAsync(ct);
        return Parse(body);
    }

    static string BuildRequest(string userAddress) =>
        $"""
        <?xml version="1.0" encoding="utf-8"?>
        <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
                       xmlns:a="http://schemas.microsoft.com/exchange/2010/Autodiscover"
                       xmlns:wsa="http://www.w3.org/2005/08/addressing">
          <soap:Header>
            <a:RequestedServerVersion>Exchange2013</a:RequestedServerVersion>
            <wsa:Action>http://schemas.microsoft.com/exchange/2010/Autodiscover/Autodiscover/GetUserSettings</wsa:Action>
            <wsa:To>{Endpoint}</wsa:To>
          </soap:Header>
          <soap:Body>
            <a:GetUserSettingsRequestMessage>
              <a:Request>
                <a:Users>
                  <a:User><a:Mailbox>{System.Security.SecurityElement.Escape(userAddress)}</a:Mailbox></a:User>
                </a:Users>
                <a:RequestedSettings>
                  <a:Setting>UserDisplayName</a:Setting>
                  <a:Setting>AlternateMailboxes</a:Setting>
                </a:RequestedSettings>
              </a:Request>
            </a:GetUserSettingsRequestMessage>
          </soap:Body>
        </soap:Envelope>
        """;

    /// <summary>
    /// Pulls the AlternateMailbox entries out of the response. The list arrives
    /// as XML escaped inside a settings value, so it is parsed in two passes.
    /// </summary>
    static List<AlternateMailbox> Parse(string body)
    {
        var result = new List<AlternateMailbox>();
        XDocument document;
        try { document = XDocument.Parse(body); }
        catch (System.Xml.XmlException) { return result; }

        foreach (var setting in document.Descendants(Auto + "UserSetting"))
        {
            if ((string?)setting.Element(Auto + "Name") != "AlternateMailboxes") continue;

            // The value is either nested elements or an escaped XML string.
            var collection = setting.Descendants(Auto + "AlternateMailbox").ToList();
            if (collection.Count > 0)
            {
                foreach (var entry in collection) Add(entry);
                continue;
            }

            var raw = (string?)setting.Element(Auto + "Value");
            if (string.IsNullOrWhiteSpace(raw)) continue;
            try
            {
                var inner = XDocument.Parse(raw);
                foreach (var entry in inner.Descendants().Where(e => e.Name.LocalName == "AlternateMailbox"))
                    Add(entry);
            }
            catch (System.Xml.XmlException) { /* unparseable value — skip */ }
        }
        return result;

        void Add(XElement entry)
        {
            string Value(string name) =>
                entry.Elements().FirstOrDefault(e => e.Name.LocalName == name)?.Value ?? "";

            var smtp = Value("SmtpAddress");
            if (smtp.Length == 0) return;
            result.Add(new AlternateMailbox(
                Value("Type"), Value("DisplayName"), smtp, Value("LegacyDN")));
        }
    }
}
