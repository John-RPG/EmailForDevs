using System.Runtime.Versioning;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions.Authentication;

namespace Mail.Sync.Auth;

/// <summary>
/// Bridges <see cref="GraphAuthenticator"/> into the Graph SDK: wrap with
/// BaseBearerTokenAuthenticationProvider when constructing GraphServiceClient.
/// Strictly silent — sync code must never pop UI; the shell owns sign-in.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GraphTokenProvider(GraphAuthenticator authenticator, IAccount account)
    : IAccessTokenProvider
{
    public AllowedHostsValidator AllowedHostsValidator { get; } = new(["graph.microsoft.com"]);

    public async Task<string> GetAuthorizationTokenAsync(
        Uri uri,
        Dictionary<string, object>? additionalAuthenticationContext = null,
        CancellationToken cancellationToken = default)
    {
        var result = await authenticator.AcquireSilentAsync(account, cancellationToken)
            ?? throw new InvalidOperationException(
                $"Silent token acquisition failed for {account.Username}; interactive sign-in required.");
        return result.AccessToken;
    }
}
