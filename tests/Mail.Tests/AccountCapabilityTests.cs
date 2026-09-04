using Mail.Sync.Auth;

namespace Mail.Tests;

public sealed class AccountCapabilityTests
{
    [Fact]
    public void Essential_scopes_are_included_even_when_not_requested()
    {
        // The app cannot function without them, so they are never optional.
        var scopes = AccountCapability.GraphScopesFor([]);
        Assert.Contains("https://graph.microsoft.com/Mail.ReadWrite", scopes);
        Assert.Contains("https://graph.microsoft.com/Mail.Send", scopes);
    }

    [Fact]
    public void Declined_capabilities_contribute_no_scopes()
    {
        var scopes = AccountCapability.GraphScopesFor([]);
        // Requesting scopes that were never granted defeats the MSAL token
        // cache, so an ungranted capability must contribute nothing.
        Assert.DoesNotContain("https://graph.microsoft.com/User.ReadBasic.All", scopes);
        Assert.DoesNotContain("https://graph.microsoft.com/People.Read", scopes);
    }

    [Fact]
    public void Granted_capabilities_add_their_scopes()
    {
        var scopes = AccountCapability.GraphScopesFor(["directory", "people"]);
        Assert.Contains("https://graph.microsoft.com/User.ReadBasic.All", scopes);
        Assert.Contains("https://graph.microsoft.com/People.Read", scopes);
    }

    [Fact]
    public void Exchange_scopes_are_kept_out_of_the_graph_request()
    {
        // A token request may name only one resource; mixing them is rejected.
        var scopes = AccountCapability.GraphScopesFor(["autodiscover"]);
        Assert.DoesNotContain(scopes, s => s.Contains("outlook.office365.com"));
        Assert.True(AccountCapability.NeedsExchangeToken(["autodiscover"]));
        Assert.False(AccountCapability.NeedsExchangeToken(["people", "directory"]));
    }

    [Fact]
    public void Scopes_are_not_duplicated_across_capabilities()
    {
        var scopes = AccountCapability.GraphScopesFor(
            AccountCapability.All.Select(c => c.Id));
        Assert.Equal(scopes.Length, scopes.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    [Fact]
    public void Every_capability_explains_itself()
    {
        // The settings screen is the only place these are described, so an
        // empty field would leave the user guessing at what they are granting.
        foreach (var capability in AccountCapability.All)
        {
            Assert.False(string.IsNullOrWhiteSpace(capability.Name), capability.Id);
            Assert.False(string.IsNullOrWhiteSpace(capability.Summary), capability.Id);
            Assert.False(string.IsNullOrWhiteSpace(capability.WithoutIt), capability.Id);
        }
    }

    [Fact]
    public void Risky_capabilities_carry_a_warning()
    {
        foreach (var capability in AccountCapability.All.Where(c =>
                     c.Risk is CapabilityRisk.Broad or CapabilityRisk.Dangerous))
            Assert.False(string.IsNullOrWhiteSpace(capability.Warning), capability.Id);
    }

    [Fact]
    public void Only_mail_is_required()
    {
        // Everything beyond reading and sending mail must be refusable, or the
        // settings screen is decoration.
        Assert.Single(AccountCapability.All.Where(c => c.Required));
        Assert.True(AccountCapability.Mail.Required);
    }

    [Fact]
    public void Ids_are_unique()
    {
        var ids = AccountCapability.All.Select(c => c.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.All(ids, id => Assert.NotNull(AccountCapability.ById(id)));
    }
}
