using System.Net.Http.Headers;
using System.Text.Json;

namespace Mail.Sync.Graph;

/// <summary>
/// Reads messages straight from Graph for display, without storing anything.
///
/// The rest of the app reads mail out of the local encrypted store, which is
/// what makes search, threading and offline reading work. This is the opposite
/// arrangement, for people who would rather nothing landed on the disk at all:
/// every list and every body is a live request, and closing the app leaves no
/// trace of the mail behind.
///
/// The trade is real and is not hidden from the user: no offline access, a
/// round trip per folder change, and search and grouping delegated to the
/// server, which answers differently from the local index.
/// </summary>
public sealed class GraphMessageReader(HttpClient http)
{
    /// <summary>
    /// One message as the list needs it. Deliberately the same shape the local
    /// query produces, so the UI does not care which side it came from.
    /// </summary>
    public sealed record LiveMessage(
        string Id,
        string Subject,
        string FromName,
        string FromAddress,
        string ToAddress,
        DateTimeOffset? Received,
        long Size,
        bool IsRead,
        bool IsFlagged,
        bool HasAttachments,
        int Importance,
        string ConversationId,
        string Preview);

    /// <summary>A message body, fetched only when one is actually opened.</summary>
    public sealed record LiveBody(string ContentType, string Content, string Headers);

    /// <summary>
    /// Fields the list needs. Requested explicitly rather than taking Graph's
    /// default projection, which returns the whole body for every row — on a
    /// thousand-message folder that is the difference between a page and a
    /// download.
    /// </summary>
    const string ListFields =
        "id,subject,from,toRecipients,receivedDateTime,isRead,flag," +
        "hasAttachments,importance,conversationId,bodyPreview";

    /// <summary>
    /// A page of messages from one folder, newest first.
    /// </summary>
    /// <param name="mailbox">The mailbox address; the signed-in user's own when null.</param>
    /// <param name="folderId">Graph folder id, as stored in folders.server_id.</param>
    /// <param name="top">How many to fetch. Graph caps a page at 999.</param>
    public async Task<IReadOnlyList<LiveMessage>> ListAsync(
        string? mailbox,
        string folderId,
        int top = 200,
        CancellationToken ct = default)
    {
        var url = $"{Root(mailbox)}/mailFolders/{Uri.EscapeDataString(folderId)}/messages" +
                  $"?$select={ListFields}" +
                  $"&$top={Math.Clamp(top, 1, 999)}" +
                  "&$orderby=receivedDateTime desc";

        using var response = await Send(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var messages = new List<LiveMessage>();
        if (json.RootElement.TryGetProperty("value", out var values))
            foreach (var item in values.EnumerateArray())
                messages.Add(ReadMessage(item));
        return messages;
    }

    /// <summary>
    /// Server-side search, standing in for the local index. Graph's search is
    /// not the same as ours — it is relevance-ranked rather than exhaustive,
    /// and it ignores $orderby — so results are worth labelling as the
    /// server's answer rather than presenting as identical.
    /// </summary>
    public async Task<IReadOnlyList<LiveMessage>> SearchAsync(
        string? mailbox,
        string query,
        int top = 200,
        CancellationToken ct = default)
    {
        // $search takes a quoted KQL string; embedded quotes would end it early.
        var term = query.Replace("\"", "", StringComparison.Ordinal);
        var url = $"{Root(mailbox)}/messages" +
                  $"?$select={ListFields}" +
                  $"&$top={Math.Clamp(top, 1, 999)}" +
                  $"&$search=\"{Uri.EscapeDataString(term)}\"";

        using var response = await Send(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var messages = new List<LiveMessage>();
        if (json.RootElement.TryGetProperty("value", out var values))
            foreach (var item in values.EnumerateArray())
                messages.Add(ReadMessage(item));
        return messages;
    }

    /// <summary>
    /// Every message in one conversation, for grouping. Uses Graph's own
    /// conversationId rather than our reference-chain threading, so a thread
    /// here can differ from the same thread in a cached mailbox — Graph groups
    /// by its own server-side notion rather than by Message-ID references.
    /// </summary>
    public async Task<IReadOnlyList<LiveMessage>> ConversationAsync(
        string? mailbox,
        string conversationId,
        CancellationToken ct = default)
    {
        var filter = $"conversationId eq '{conversationId.Replace("'", "''", StringComparison.Ordinal)}'";
        var url = $"{Root(mailbox)}/messages" +
                  $"?$select={ListFields}" +
                  $"&$filter={Uri.EscapeDataString(filter)}" +
                  "&$orderby=receivedDateTime desc";

        using var response = await Send(url, ct);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);

        var messages = new List<LiveMessage>();
        if (json.RootElement.TryGetProperty("value", out var values))
            foreach (var item in values.EnumerateArray())
                messages.Add(ReadMessage(item));
        return messages;
    }

    /// <summary>
    /// The body and headers of one message, fetched when it is opened.
    /// Internet headers are requested too, since reading them is most of the
    /// reason this client exists.
    /// </summary>
    public async Task<LiveBody?> BodyAsync(
        string? mailbox,
        string messageId,
        CancellationToken ct = default)
    {
        var url = $"{Root(mailbox)}/messages/{Uri.EscapeDataString(messageId)}" +
                  "?$select=body,internetMessageHeaders";

        using var response = await Send(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        await using var stream = await response.Content.ReadAsStreamAsync(ct);
        using var json = await JsonDocument.ParseAsync(stream, cancellationToken: ct);
        var root = json.RootElement;

        var contentType = "text";
        var content = "";
        if (root.TryGetProperty("body", out var body))
        {
            contentType = body.TryGetProperty("contentType", out var ct2)
                ? ct2.GetString() ?? "text" : "text";
            content = body.TryGetProperty("content", out var c)
                ? c.GetString() ?? "" : "";
        }

        // Rebuilt into the familiar "Name: value" block rather than handed over
        // as JSON, so the header view looks the same in either mode.
        var headers = "";
        if (root.TryGetProperty("internetMessageHeaders", out var headerList))
            headers = string.Join(Environment.NewLine, headerList.EnumerateArray()
                .Select(h =>
                    $"{(h.TryGetProperty("name", out var n) ? n.GetString() : "")}: " +
                    $"{(h.TryGetProperty("value", out var v) ? v.GetString() : "")}"));

        return new LiveBody(contentType, content, headers);
    }

    /// <summary>
    /// The raw MIME of a message, which Graph will serve as $value. This is the
    /// one thing online-only does better than the cache, since it is the
    /// original bytes rather than our reconstruction.
    /// </summary>
    public async Task<string?> MimeAsync(
        string? mailbox,
        string messageId,
        CancellationToken ct = default)
    {
        var url = $"{Root(mailbox)}/messages/{Uri.EscapeDataString(messageId)}/$value";
        using var response = await Send(url, ct);
        if (!response.IsSuccessStatusCode) return null;
        return await response.Content.ReadAsStringAsync(ct);
    }

    /// <summary>
    /// Marks a message read or unread on the server. There is no local copy to
    /// update, so the change is the request.
    /// </summary>
    public async Task<bool> SetReadAsync(
        string? mailbox,
        string messageId,
        bool isRead,
        CancellationToken ct = default)
    {
        var url = $"{Root(mailbox)}/messages/{Uri.EscapeDataString(messageId)}";
        using var request = new HttpRequestMessage(HttpMethod.Patch, url)
        {
            Content = new StringContent(
                $$"""{"isRead": {{(isRead ? "true" : "false")}}}""",
                System.Text.Encoding.UTF8,
                "application/json"),
        };
        using var response = await http.SendAsync(request, ct);
        return response.IsSuccessStatusCode;
    }

    /// <summary>
    /// Whether the server is reachable. Online-only has no fallback, so the UI
    /// needs to tell "no mail" apart from "no connection".
    /// </summary>
    public async Task<bool> IsReachableAsync(string? mailbox, CancellationToken ct = default)
    {
        try
        {
            using var response = await Send($"{Root(mailbox)}/mailFolders/inbox?$select=id", ct);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return false;
        }
    }

    Task<HttpResponseMessage> Send(string url, CancellationToken ct)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, url);
        // Preview text can carry any encoding; ask Graph to return it as UTF-8
        // rather than guessing at the other end.
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        return http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
    }

    /// <summary>
    /// The Graph path root. A shared mailbox is addressed by its own address;
    /// the signed-in user is /me. Both are gated by the user's own permissions,
    /// so this cannot reach mail the user could not otherwise open.
    /// </summary>
    static string Root(string? mailbox) =>
        string.IsNullOrWhiteSpace(mailbox)
            ? "https://graph.microsoft.com/v1.0/me"
            : $"https://graph.microsoft.com/v1.0/users/{Uri.EscapeDataString(mailbox)}";

    static LiveMessage ReadMessage(JsonElement item)
    {
        var (fromName, fromAddress) = ReadAddress(item, "from");

        var toAddress = "";
        if (item.TryGetProperty("toRecipients", out var recipients) &&
            recipients.ValueKind == JsonValueKind.Array)
        {
            var first = recipients.EnumerateArray().FirstOrDefault();
            if (first.ValueKind == JsonValueKind.Object &&
                first.TryGetProperty("emailAddress", out var email))
                toAddress = email.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "";
        }

        var flagged = false;
        if (item.TryGetProperty("flag", out var flag) &&
            flag.TryGetProperty("flagStatus", out var status))
            flagged = status.GetString() is "flagged" or "complete";

        return new LiveMessage(
            Id: Text(item, "id"),
            Subject: Text(item, "subject"),
            FromName: fromName,
            FromAddress: fromAddress,
            ToAddress: toAddress,
            Received: item.TryGetProperty("receivedDateTime", out var received) &&
                      received.ValueKind == JsonValueKind.String &&
                      DateTimeOffset.TryParse(received.GetString(), out var when)
                ? when
                : null,
            // Graph omits size from this projection; asking for it would cost a
            // second round trip per row, so the column reads as unknown here.
            Size: 0,
            IsRead: item.TryGetProperty("isRead", out var read) &&
                    read.ValueKind == JsonValueKind.True,
            IsFlagged: flagged,
            HasAttachments: item.TryGetProperty("hasAttachments", out var att) &&
                            att.ValueKind == JsonValueKind.True,
            Importance: Text(item, "importance") switch
            {
                "high" => 2,
                "low" => 0,
                _ => 1,
            },
            ConversationId: Text(item, "conversationId"),
            Preview: Text(item, "bodyPreview"));
    }

    static (string Name, string Address) ReadAddress(JsonElement item, string property)
    {
        if (!item.TryGetProperty(property, out var holder) ||
            holder.ValueKind != JsonValueKind.Object ||
            !holder.TryGetProperty("emailAddress", out var email))
            return ("", "");
        return (
            email.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
            email.TryGetProperty("address", out var a) ? a.GetString() ?? "" : "");
    }

    static string Text(JsonElement item, string property) =>
        item.TryGetProperty(property, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";
}
