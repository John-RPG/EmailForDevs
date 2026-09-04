using System.Runtime.Versioning;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace Mail.Sync.Graph;

/// <summary>
/// Finds mailboxes other than the signed-in user's own that they can open.
///
/// Three routes, in descending order of authority:
///
///  1. Autodiscover (see <see cref="AutodiscoverMailboxes"/>) returns the
///     mailboxes Exchange has actually mapped to this user. This is what Outlook
///     uses, it is authoritative, and it costs one authenticated call.
///  2. The relevance graph (People) surfaces mailboxes the user deals with. A
///     hint only — being able to mail someone says nothing about opening their
///     mailbox.
///  3. A directory search covers mailboxes the user holds rights on but has
///     never corresponded with, for when they know what they are looking for.
///
/// Only route 1 is a statement about access. Routes 2 and 3 propose names, and
/// a proposal is confirmed by opening the mailbox — one deliberate check the
/// user asked for, never a sweep across candidates, which would fill the
/// tenant's audit log with denied-access entries and read as enumeration.
///
/// Routes degrade independently: consumer accounts have neither a directory nor
/// automapping, and simply contribute nothing.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class SharedMailboxDiscovery(GraphServiceClient graph)
{
    /// <param name="Address">SMTP address — the id used for /users/{address}.</param>
    /// <param name="Source">How it was found: "mapped", "in use" or "directory".</param>
    public sealed record Candidate(string Address, string DisplayName, string Source)
    {
        /// <summary>
        /// True when Exchange itself reported this mailbox as mapped to the user,
        /// which is a statement about access rather than a guess at one.
        /// </summary>
        public bool IsMapped => Source == "mapped";
    }

    /// <param name="TotalItems">Inbox size, so the user can sanity-check they
    /// picked the right mailbox before committing to a sync.</param>
    public sealed record AccessResult(bool CanOpen, string Detail, int TotalItems, int UnreadItems);

    /// <summary>
    /// Candidate mailboxes, most-likely first, excluding the user's own. Never
    /// throws: a tenant that denies a route contributes nothing from it.
    /// </summary>
    /// <param name="mapped">
    /// Mailboxes Autodiscover reported as mapped to this user, if that call was
    /// possible. Passed in rather than fetched here because it needs a token for
    /// the Exchange audience, which only the shell can acquire.
    /// </param>
    /// <param name="includeSuggestions">
    /// Whether to consult the relevance graph. Off when the user did not grant
    /// that capability: asking anyway would fail, and a silent failure would be
    /// indistinguishable from having nothing to suggest.
    /// </param>
    public async Task<IReadOnlyList<Candidate>> DiscoverAsync(
        string ownAddress,
        IEnumerable<AutodiscoverMailboxes.AlternateMailbox>? mapped = null,
        bool includeSuggestions = true,
        CancellationToken ct = default)
    {
        var found = new Dictionary<string, Candidate>(StringComparer.OrdinalIgnoreCase);

        // Exchange's own answer comes first and is never overwritten by a guess.
        foreach (var entry in mapped ?? [])
        {
            // Archives are the user's own mail under a second store, not another
            // mailbox to add: they would duplicate what is already synced.
            if (entry.Type.Equals("Archive", StringComparison.OrdinalIgnoreCase)) continue;
            Add(entry.SmtpAddress, entry.DisplayName, "mapped");
        }

        // Relevance graph next: mailboxes the user deals with, which may include
        // ones they can open but that were never automapped.
        if (includeSuggestions)
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

        // Mapped mailboxes first: they are the ones we can promise will open.
        return [.. found.Values.OrderByDescending(c => c.IsMapped).ThenBy(c => c.DisplayName)];

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
