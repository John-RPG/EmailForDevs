using System.Net.Http.Headers;
using System.Runtime.Versioning;
using System.Text;
using System.Text.RegularExpressions;

namespace Mail.Sync.Graph;

/// <summary>
/// Folder sizes in bytes, which Graph does not expose.
///
/// Graph reports item counts but no byte size for a folder, and its quota
/// figures live behind the reporting API — Reports.Read.All plus an
/// administrator role, which is a tenant-wide reporting grant in exchange for
/// one number. EWS returns the size directly as an extended property
/// (PR_MESSAGE_SIZE_EXTENDED, tag 0x0E08) using the Exchange token already held
/// for Autodiscover and live updates, so it costs no additional permission.
///
/// Sizes are the server's view of the whole folder, not what has been mirrored
/// locally. Both are worth showing: the difference is exactly how much is left
/// to download.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class MailboxFolderSizes(HttpClient http)
{
    const string Endpoint = "https://outlook.office365.com/EWS/Exchange.asmx";

    /// <param name="Bytes">Total size on the server, including items not mirrored.</param>
    public sealed record FolderSize(string Name, long Bytes, int ItemCount);

    /// <summary>
    /// Sizes for every folder in a mailbox, keyed by display name. Returns an
    /// empty result rather than throwing when EWS is unavailable: sizes are
    /// informational, and losing them must not break the folder tree.
    /// </summary>
    public async Task<IReadOnlyDictionary<string, FolderSize>> GetAsync(
        string mailbox, string ewsToken, string? signedInAs = null,
        CancellationToken ct = default)
    {
        // Impersonate only for a mailbox other than the signed-in user's own:
        // Exchange rejects a request impersonating the user it authenticated.
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
                <m:FindFolder Traversal="Deep">
                  <m:FolderShape>
                    <t:BaseShape>IdOnly</t:BaseShape>
                    <t:AdditionalProperties>
                      <t:FieldURI FieldURI="folder:DisplayName"/>
                      <t:FieldURI FieldURI="folder:TotalCount"/>
                      <t:ExtendedFieldURI PropertyTag="0x0E08" PropertyType="Long"/>
                    </t:AdditionalProperties>
                  </m:FolderShape>
                  <m:ParentFolderIds>
                    <t:DistinguishedFolderId Id="msgfolderroot"/>
                  </m:ParentFolderIds>
                </m:FindFolder>
              </soap:Body>
            </soap:Envelope>
            """;

        var body = await PostAsync(envelope, ewsToken, ct);
        if (body is null) return new Dictionary<string, FolderSize>();

        var result = new Dictionary<string, FolderSize>(StringComparer.OrdinalIgnoreCase);

        // Each folder is one <t:Folder> block; parse per block rather than
        // matching names and values separately, because a folder missing its
        // size would otherwise shift every later pairing by one.
        foreach (Match folder in Regex.Matches(body, @"<t:Folder>(.*?)</t:Folder>", RegexOptions.Singleline))
        {
            var block = folder.Groups[1].Value;
            var name = Regex.Match(block, @"<t:DisplayName>(.*?)</t:DisplayName>");
            if (!name.Success) continue;

            var size = Regex.Match(block, @"<t:Value>(\d+)</t:Value>");
            var count = Regex.Match(block, @"<t:TotalCount>(\d+)</t:TotalCount>");

            result[name.Groups[1].Value] = new FolderSize(
                name.Groups[1].Value,
                size.Success ? long.Parse(size.Groups[1].Value) : 0,
                count.Success ? int.Parse(count.Groups[1].Value) : 0);
        }
        return result;
    }

    static string ImpersonationHeader(string mailbox) => $"""
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
            using var response = await http.SendAsync(request, ct);
            return response.IsSuccessStatusCode
                ? await response.Content.ReadAsStringAsync(ct)
                : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
