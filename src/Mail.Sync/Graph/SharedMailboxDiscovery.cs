using System.Runtime.Versioning;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace Mail.Sync.Graph;

/// <summary>
/// Finds mailboxes other than the signed-in user's own that they can open.
///
/// Graph has no "list the mailboxes I can open" endpoint, so this triangulates:
/// the relevance graph (People) surfaces mailboxes already in use — which is
/// what Outlook automapping produces — and a directory search covers mailboxes
/// the user holds rights on but has never corresponded with. Neither route is
/// authoritative about *access*, so every candidate is confirmed by actually
/// opening its inbox; that call is the same permission check Exchange applies
/// to the user in OWA, so the app can never reach mail its user could not.
///
/// Discovery routes degrade independently: consumer accounts have no directory
/// at all and simply return nothing rather than failing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SharedMailboxDiscovery(GraphServiceClient graph)
{
    /// <param name="Address">SMTP address — the id used for /users/{address}.</param>
    /// <param name="Source">How it was found, for display: "in use" or "directory".</param>
    public sealed record Candidate(string Address, string DisplayName, string Source);

    /// <param name="TotalItems">Inbox size, so the user can sanity-check they
    /// picked the right mailbox before committing to a sync.</param>
    public sealed record AccessResult(bool CanOpen, string Detail, int TotalItems, int UnreadItems);

    /// <summary>
    /// Candidate mailboxes, most-likely first, excluding the user's own. Never
    /// throws: a tenant that denies a route contributes nothing from it.
    /// </summary>
    public async Task<IReadOnlyList<Candidate>> DiscoverAsync(
        string ownAddress, CancellationToken ct = default)
    {
        var found = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

        // Relevance graph first: these are the mailboxes the user actually works
        // in, so an automapped shared mailbox ranks high here.
        try
        {
            var people = await graph.Me.People.GetAsync(rc =>
            {
                rc.QueryParameters.Top = 100;
                rc.QueryParameters.Select = ["displayName", "scoredEmailAddresses", "personType"];
            }, ct);
            foreach (var person in people?.Value ?? [])
            {
                if (person.PersonType?.Subclass is not ("OrganizationUser" or "SharedMailbox" or "Group"))
                    continue;
                var address = person.ScoredEmailAddresses?.FirstOrDefault()?.Address;
                if (address is null) continue;
                Add(address, person.DisplayName, "in use");
            }
        }
        catch (ODataError) { /* no relevance graph (consumer) or scope not granted */ }

        return [.. found.Values];

        void Add(string address, string? name, string source)
        {
            if (string.Equals(address, ownAddress, StringComparison.OrdinalIgnoreCase)) return;
            if (found.ContainsKey(address)) return;
            found[address] = new Candidate(address, string.IsNullOrWhiteSpace(name) ? address : name, source);
        }
    }

    /// <summary>
    /// Directory search by name or address, for mailboxes the user has rights on
    /// but has never mailed. Returns nothing where the directory is unreadable.
    /// </summary>
    public async Task<IReadOnlyList<Candidate>> SearchDirectoryAsync(
        string term, string ownAddress, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(term)) return [];
        var results = new List<Candidate>();
        try
        {
            // startswith on both fields matches how people search — by what they
            // are typing, which may be either a name or the address itself.
            var escaped = term.Replace("'", "''");
            var users = await graph.Users.GetAsync(rc =>
            {
                rc.QueryParameters.Select = ["displayName", "mail", "userPrincipalName"];
                rc.QueryParameters.Filter =
                    $"mail ne null and (startswith(displayName,'{escaped}') or startswith(mail,'{escaped}'))";
                rc.QueryParameters.Top = 50;
                rc.Headers.Add("ConsistencyLevel", "eventual");
            }, ct);
            foreach (var user in users?.Value ?? [])
            {
                var address = user.Mail ?? user.UserPrincipalName;
                if (address is null) continue;
                if (string.Equals(address, ownAddress, StringComparison.OrdinalIgnoreCase)) continue;
                results.Add(new Candidate(address, user.DisplayName ?? address, "directory"));
            }
        }
        catch (ODataError) { /* directory reads denied in this tenant */ }
        return results;
    }

    /// <summary>
    /// Confirms the mailbox can actually be opened. This is the check that
    /// matters: discovery only proposes, Exchange decides.
    /// </summary>
    public async Task<AccessResult> TestAccessAsync(string address, CancellationToken ct = default)
    {
        try
        {
            var inbox = await graph.Users[address].MailFolders["inbox"].GetAsync(cancellationToken: ct);
            return new AccessResult(true, "Inbox reachable.",
                inbox?.TotalItemCount ?? 0, inbox?.UnreadItemCount ?? 0);
        }
        catch (ODataError ex)
        {
            // Distinguish "you lack permission" from "no such mailbox", because
            // the fix differs: ask an admin, versus check the address.
            var detail = ex.ResponseStatusCode switch
            {
                403 => "Access denied — you do not have permission to open this mailbox.",
                404 => "No mailbox found at that address.",
                _ => ex.Error?.Message ?? "Could not open the mailbox.",
            };
            return new AccessResult(false, detail, 0, 0);
        }
    }
}
