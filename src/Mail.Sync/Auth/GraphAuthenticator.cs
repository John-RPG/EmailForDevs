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

    readonly IPublicClientApplication _app;

    public GraphAuthenticator(string tokenCachePath, Func<IntPtr>? parentWindowHandle = null)
    {
        var builder = PublicClientApplicationBuilder.Create(ClientId)
            .WithAuthority(Authority);
        builder = parentWindowHandle is not null
            ? builder
                .WithBroker(new BrokerOptions(BrokerOptions.OperatingSystems.Windows))
                .WithParentActivityOrWindow(parentWindowHandle)
            : builder.WithRedirectUri("http://localhost");
        _app = builder.Build();
        TokenCacheStorage.Attach(_app.UserTokenCache, tokenCachePath);
    }

    public async Task<IReadOnlyList<IAccount>> GetAccountsAsync() =>
        [.. await _app.GetAccountsAsync()];

    /// <summary>Silent acquisition; null when interactive sign-in is required.</summary>
    public async Task<AuthenticationResult?> AcquireSilentAsync(
        IAccount account, CancellationToken ct = default)
    {
        try
        {
            return await _app.AcquireTokenSilent(MailScopes, account).ExecuteAsync(ct);
        }
        catch (MsalUiRequiredException)
        {
            return null;
        }
    }

    public Task<AuthenticationResult> SignInInteractiveAsync(
        string? loginHint = null, CancellationToken ct = default)
    {
        var request = _app.AcquireTokenInteractive(MailScopes);
        if (loginHint is not null)
            request = request.WithLoginHint(loginHint);
        return request.ExecuteAsync(ct);
    }

    public Task SignOutAsync(IAccount account) => _app.RemoveAsync(account);
}
