using System.Runtime.Versioning;
using Microsoft.Identity.Client;
using Microsoft.Identity.Client.Broker;

namespace Mail.Sync.Auth;

/// <summary>
/// MSAL public-client wrapper for the EmailClientForDevs app registration.
/// Silent-first token acquisition against a DPAPI-protected file cache.
/// Interactive sign-in uses the system browser (loopback redirect) by default;
/// supply a window handle to use the Windows account broker (WAM) instead,
/// which gives native SSO with accounts Windows already knows.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class GraphAuthenticator
{
    public const string ClientId = "84d7958a-9db1-4cde-b670-5330d2f8422e";
    public const string Authority = "https://login.microsoftonline.com/common";

    /// <summary>openid/profile/offline_access are added by MSAL automatically.</summary>
    public static readonly string[] MailScopes =
    [
        "https://graph.microsoft.com/Mail.ReadWrite",
        "https://graph.microsoft.com/Mail.ReadWrite.Shared",
        "https://graph.microsoft.com/Mail.Send",
        "https://graph.microsoft.com/Mail.Send.Shared",
        "https://graph.microsoft.com/User.Read",
    ];

    /// <summary>
    /// Opt-in extras that let the app *find* other mailboxes: People.Read surfaces
    /// ones already in use (which is what Outlook automapping produces), and
    /// User.ReadBasic.All allows a directory search for one the user has rights to
    /// but has never mailed.
    ///
    /// Neither grants access to mail. Opening any mailbox still goes through the
    /// Exchange permission the signed-in user holds, so the app can never read
    /// mail its user could not read in OWA. They are separated from
    /// <see cref="MailScopes"/> because they are useless on consumer accounts
    /// (which have no directory) and would otherwise force a fresh consent prompt
    /// on every account for a feature only work tenants can use.
    /// </summary>
    public static readonly string[] DiscoveryScopes =
    [
        "https://graph.microsoft.com/People.Read",
        "https://graph.microsoft.com/User.ReadBasic.All",
    ];

    public static string[] ScopesFor(bool withDiscovery) =>
        withDiscovery ? [.. MailScopes, .. DiscoveryScopes] : MailScopes;

    readonly IPublicClientApplication _app;

    /// <summary>
    /// Browser-based sign-in by default: tokens belong to this app alone, and
    /// nothing is registered with Windows. The WAM broker is opt-in only
    /// (<paramref name="useWindowsBroker"/>) because it prompts to add the
    /// account to the device ("Use this account everywhere on your device"),
    /// which for work accounts can also pull in device-registration policy.
    /// </summary>
    public GraphAuthenticator(
        string tokenCachePath,
        Func<IntPtr>? parentWindowHandle = null,
        bool useWindowsBroker = false)
    {
        var builder = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority(Authority);
        if (useWindowsBroker && parentWindowHandle is not null)
        {
            builder = builder
                .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
                .WithParentActivityOrWindow(parentWindowHandle);
        }
        else
        {
            builder = builder.WithRedirectUri("http://localhost");
            if (parentWindowHandle is not null)
                builder = builder.WithParentActivityOrWindow(parentWindowHandle);
        }
        _app = builder.Build();
        TokenCacheStorage.Attach(_app.UserTokenCache, tokenCachePath);
    }

    public async Task<IReadOnlyList<IAccount>> GetAccountsAsync() =>
        [.. await _app.GetAccountsAsync()];

    /// <summary>
    /// Silent acquisition; null when interactive sign-in is required.
    /// Pass the scopes actually granted at sign-in where known — personal accounts
    /// never grant the .Shared scopes, and requesting ungranted scopes defeats the
    /// token cache (every call redeems the refresh token until the service throttles).
    /// </summary>
    public async Task<AuthenticationResult?> AcquireSilentAsync(
        IAccount account, IEnumerable<string>? scopes = null, CancellationToken ct = default)
    {
        try
        {
            return await _app.AcquireTokenSilent(scopes ?? MailScopes, account).ExecuteAsync(ct);
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
    }

    /// <summary>
    /// Interactive sign-in in the system browser (loopback redirect). Prompts
    /// for account selection so a second account can be added even when Windows
    /// already knows one.
    /// </summary>
    public Task<AuthenticationResult> SignInInteractiveAsync(
        string? loginHint = null, CancellationToken ct = default,
        bool withDiscovery = false)
    {
        var request = _app.AcquireTokenInteractive(ScopesFor(withDiscovery))
            .WithUseEmbeddedWebView(false);
        request = loginHint is not null
            ? request.WithLoginHint(loginHint)
            : request.WithPrompt(Prompt.SelectAccount);
        return request.ExecuteAsync(ct);
    }

    public Task SignOutAsync(IAccount account) => _app.RemoveAsync(account);
}
