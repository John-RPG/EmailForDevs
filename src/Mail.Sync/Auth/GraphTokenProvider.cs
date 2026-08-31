using System.Runtime.Versioning;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions.Authentication;

namespace Mail.Sync.Auth;

/// <summary>
/// Bridges <see cref="GraphAuthenticator"/> into the Graph SDK: wrap with
/// BaseBearerTokenAuthenticationProvider when constructing GraphServiceClient.
/// Strictly silent — sync code must never pop UI; the shell owns sign-in.
/// Construct with the sign-in result so acquisition uses the granted scopes
/// (cache-friendly) rather than the full requested set.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GraphTokenProvider : IAccessTokenProvider
{
    readonly GraphAuthenticator _authenticator;
    readonly IAccount _account;
    readonly string[] _scopes;

    public GraphTokenProvider(GraphAuthenticator authenticator, AuthenticationResult signIn)
    {
        _authenticator = authenticator;
        _account = signIn.Account;
        _scopes = [.. signIn.Scopes.Where(s =>
            s.StartsWith("https://graph.microsoft.com/", StringComparison.OrdinalIgnoreCase))];
        if (_scopes.Length == 0)
            _scopes = GraphAuthenticator.MailScopes;
    }

    public AllowedHostsValidator AllowedHostsValidator { get; } = new(["graph.microsoft.com"]);

    public async Task<string> GetAuthorizationTokenAsync(
        Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        var result = await _authenticator.AcquireSilentAsync(_account, _scopes, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Silent token acquisition failed for {_account.Username}; interactive sign-in required.");
        return result.AccessToken;
    }
}
