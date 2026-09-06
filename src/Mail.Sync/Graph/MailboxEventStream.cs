using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Mail.Sync.Graph;

/// <summary>
/// A long-poll doorbell for new mail, using EWS streaming notifications.
///
/// Graph offers no push route for a desktop client: its change notifications
/// deliver to a public HTTPS endpoint or an Azure Event Hub, neither of which a
/// locally installed client has. Delta is explicitly a pull model with no wait
/// semantics — asking repeatedly is all it offers.
///
/// EWS streaming subscriptions are the exception, and are exactly a long poll:
/// Subscribe once, then GetStreamingEvents holds the connection open (up to 30
/// minutes) and writes events down it as they occur, returning when the window
/// closes so the caller re-issues it. That is push-shaped without a public
/// endpoint, and it reuses the Exchange token already held for Autodiscover, so
/// it costs no additional permission.
///
/// This class only reports *that* something changed. Acting on it — running a
/// delta pass — stays with the sync engine, because the events carry item ids
/// this client would have to reconcile anyway.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MailboxEventStream(HttpClient http)
{
    const string Endpoint = "https://outlook.office365.com/EWS/Exchange.asmx";

    /// <summary>
    /// Longest window Exchange accepts, in minutes. Using the maximum minimises
    /// reconnections, each of which costs a round trip and a new subscription
    /// if the old one has lapsed.
    /// </summary>
    public const int MaxConnectionMinutes = 30;

    public sealed record Subscription(string Id, string Mailbox, bool Impersonate);

    /// <param name="Events">Event names seen, e.g. NewMailEvent, ModifiedEvent.</param>
    /// <param name="SubscriptionLapsed">
    /// True when the server ended the subscription rather than the window: the
    /// caller must subscribe again before polling further.
    /// </param>
    public sealed record StreamResult(
        IReadOnlyList<string> Events, bool SubscriptionLapsed, TimeSpan Held);

    /// <summary>
    /// Subscribes to changes in a mailbox's inbox. Returns null when the mailbox
    /// or tenant will not serve streaming notifications, which is not an error:
    /// the caller falls back to periodic checks.
    /// </summary>
    /// <param name="signedInAs">
    /// The account the token belongs to. Impersonation is only sent when the
    /// mailbox differs — Exchange rejects a request that impersonates the very
    /// user it is authenticated as.
    /// </param>
    public async Task<Subscription?> SubscribeAsync(
        string mailbox, string ewsToken, string? signedInAs = null,
        CancellationToken ct = default)
    {
        var impersonate = signedInAs is not null &&
            !string.Equals(mailbox, signedInAs, StringComparison.OrdinalIgnoreCase);
        var envelope = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
                           xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
                           xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages">
              <soap:Header>
                <t:RequestServerVersion Version="Exchange2013"/>
                {(impersonate ? ImpersonationHeader(mailbox) : "")}
              </soap:Header>
              <soap:Body>
                <m:Subscribe>
                  <m:StreamingSubscriptionRequest>
                    <t:FolderIds><t:DistinguishedFolderId Id="inbox"/></t:FolderIds>
                    <t:EventTypes>
                      <t:EventType>NewMailEvent</t:EventType>
                      <t:EventType>CreatedEvent</t:EventType>
                      <t:EventType>ModifiedEvent</t:EventType>
                      <t:EventType>DeletedEvent</t:EventType>
                      <t:EventType>MovedEvent</t:EventType>
                    </t:EventTypes>
                  </m:StreamingSubscriptionRequest>
                </m:Subscribe>
              </soap:Body>
            </soap:Envelope>
            """;

        var body = await PostAsync(envelope, ewsToken, ct);
        if (body is null) return null;

        // The id comes back in the messages namespace, not types — worth being
        // explicit about, since matching the wrong prefix silently finds nothing.
        var match = Regex.Match(body, @"<[mt]:SubscriptionId>(.*?)</[mt]:SubscriptionId>");
        return match.Success ? new Subscription(match.Groups[1].Value, mailbox, impersonate) : null;
    }

    /// <summary>
    /// Holds the connection open until something happens or the window closes.
    /// Returns what was seen; an empty event list simply means a quiet window.
    /// </summary>
    /// <param name="onEvents">
    /// Called as soon as events arrive, before the window closes. This is the
    /// whole point: reading the response as one string would hold every event
    /// until the connection ended, making a long window *worse* than a short
    /// poll rather than better.
    /// </param>
    public async Task<StreamResult> WaitForEventsAsync(
        Subscription subscription, string ewsToken,
        int connectionMinutes = MaxConnectionMinutes,
        Action<IReadOnlyList<string>>? onEvents = null,
        CancellationToken ct = default)
    {
        var envelope = $"""
            <?xml version="1.0" encoding="utf-8"?>
            <soap:Envelope xmlns:soap="http://schemas.xmlsoap.org/soap/envelope/"
                           xmlns:t="http://schemas.microsoft.com/exchange/services/2006/types"
                           xmlns:m="http://schemas.microsoft.com/exchange/services/2006/messages">
              <soap:Header>
                <t:RequestServerVersion Version="Exchange2013"/>
                {(subscription.Impersonate ? ImpersonationHeader(subscription.Mailbox) : "")}
              </soap:Header>
              <soap:Body>
                <m:GetStreamingEvents>
                  <m:SubscriptionIds>
                    <t:SubscriptionId>{subscription.Id}</t:SubscriptionId>
                  </m:SubscriptionIds>
                  <m:ConnectionTimeout>{Math.Clamp(connectionMinutes, 1, MaxConnectionMinutes)}</m:ConnectionTimeout>
                </m:GetStreamingEvents>
              </soap:Body>
            </soap:Envelope>
            """;

        var started = DateTimeOffset.UtcNow;
        var seen = new List<string>();
        var lapsed = false;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ewsToken);
            request.Content = new StringContent(envelope, Encoding.UTF8, "text/xml");
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml")
            {
                CharSet = "utf-8",
            };

            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode)
                return new StreamResult([], true, DateTimeOffset.UtcNow - started);

            // Exchange writes each notification as its own chunk down the open
            // connection, so the stream is read as it arrives rather than at the
            // end. Buffer across reads: a chunk boundary can fall mid-element.
            await using var stream = await response.Content.ReadAsStreamAsync(ct);
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var buffer = new StringBuilder();
            var chunk = new char[4096];

            while (!ct.IsCancellationRequested)
            {
                var read = await reader.ReadAsync(chunk, ct);
                if (read == 0) break;                    // window closed
                buffer.Append(chunk, 0, read);

                var text = buffer.ToString();
                var fresh = Regex.Matches(text, @"<t:(\w+Event)>")
                    .Select(m => m.Groups[1].Value)
                    .Where(name => name != "StatusEvent")   // keep-alive, not a change
                    .Distinct()
                    .Where(name => !seen.Contains(name))
                    .ToList();

                if (fresh.Count > 0)
                {
                    seen.AddRange(fresh);
                    // Report immediately: waiting for the window to close would
                    // defeat the purpose of holding it open.
                    onEvents?.Invoke(fresh);
                }

                if (text.Contains("<t:ConnectionStatus>Closed</t:ConnectionStatus>",
                        StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("ErrorSubscriptionNotFound", StringComparison.OrdinalIgnoreCase) ||
                    text.Contains("ErrorInvalidSubscription", StringComparison.OrdinalIgnoreCase))
                {
                    lapsed = true;
                    break;
                }

                // Keep the tail only: a 30-minute window would otherwise grow an
                // unbounded buffer out of keep-alive traffic.
                if (buffer.Length > 64 * 1024)
                    buffer.Remove(0, buffer.Length - 8 * 1024);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            lapsed = true;
        }

        return new StreamResult(seen, lapsed, DateTimeOffset.UtcNow - started);
    }

    /// <summary>
    /// A shared mailbox is reached by impersonating the mailbox owner; the
    /// signed-in user's own mailbox needs no header at all.
    /// </summary>
    static string ImpersonationHeader(string mailbox) =>
        string.IsNullOrWhiteSpace(mailbox)
            ? ""
            : $"""
               <t:ExchangeImpersonation>
                 <t:ConnectingSID>
                   <t:SmtpAddress>{System.Security.SecurityElement.Escape(mailbox)}</t:SmtpAddress>
                 </t:ConnectingSID>
               </t:ExchangeImpersonation>
               """;

    async Task<string?> PostAsync(string envelope, string token, CancellationToken ct)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, Endpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(envelope, Encoding.UTF8, "text/xml");
            request.Content.Headers.ContentType = new MediaTypeHeaderValue("text/xml")
            {
                CharSet = "utf-8",
            };

            using var response = await http.SendAsync(
                request, HttpCompletionOption.ResponseHeadersRead, ct);
            if (!response.IsSuccessStatusCode) return null;
            return await response.Content.ReadAsStringAsync(ct);
        }
        catch (OperationCanceledException)
        {
            throw;   // shutting down, not a failure
        }
        catch (Exception)
        {
            // A dropped long poll is ordinary — laptops sleep and networks
            // change. The caller re-subscribes rather than treating it as fatal.
            return null;
        }
    }
}
