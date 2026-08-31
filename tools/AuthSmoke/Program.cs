// End-to-end probe for the app registration: signs in (silent when cached,
// system browser otherwise), then hits Graph for /me and the mail folder list.
// Run from the repo root:  dotnet run --project tools/AuthSmoke
using Mail.Sync.Auth;
using Microsoft.Graph;
using Microsoft.Identity.Client;
using Microsoft.Kiota.Abstractions.Authentication;

var cachePath = args.Length > 0
    ? args[0]
    : Path.Combine(Directory.GetCurrentDirectory(), ".scratch", "msal.cache");
Console.WriteLine($"Token cache: {cachePath}");

var auth = new GraphAuthenticator(cachePath);

AuthenticationResult? result = null;
var accounts = await auth.GetAccountsAsync();
if (accounts.Count > 0)
{
    Console.WriteLine($"Cached account found: {accounts[0].Username} — trying silent sign-in…");
    result = await auth.AcquireSilentAsync(accounts[0]);
}
if (result is null)
{
    Console.WriteLine("Opening the system browser for sign-in…");
    result = await auth.SignInInteractiveAsync();
}

Console.WriteLine($"Signed in as {result.Account.Username}");
Console.WriteLine($"Granted scopes: {string.Join(' ', result.Scopes)}");
Console.WriteLine();

var graph = new GraphServiceClient(
    new BaseBearerTokenAuthenticationProvider(new GraphTokenProvider(auth, result)));

var me = await graph.Me.GetAsync();
Console.WriteLine($"Graph /me: {me?.DisplayName} <{me?.Mail ?? me?.UserPrincipalName}>");
Console.WriteLine();

Console.WriteLine("Mail folders:");
var folders = await graph.Me.MailFolders.GetAsync(rc => rc.QueryParameters.Top = 50);
foreach (var folder in folders?.Value ?? [])
    Console.WriteLine(
        $"  {folder.DisplayName,-28} total {folder.TotalItemCount,6}  unread {folder.UnreadItemCount,6}");
