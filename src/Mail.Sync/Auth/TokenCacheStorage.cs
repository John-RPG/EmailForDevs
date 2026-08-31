using System.Runtime.Versioning;
using System.Security.Cryptography;
using Microsoft.Identity.Client;

namespace Mail.Sync.Auth;

/// <summary>
/// Persists MSAL's token cache to a single DPAPI(CurrentUser)-protected file.
/// A missing, corrupt, or foreign-user file is silently discarded — MSAL then
/// starts with an empty cache and the user signs in again.
/// </summary>
[SupportedOSPlatform("windows")]
public static class TokenCacheStorage
{
    public static void Attach(ITokenCache cache, string filePath)
    {
        cache.SetBeforeAccess(args =>
        {
            if (!File.Exists(filePath))
                return;
            try
            {
                var bytes = ProtectedData.Unprotect(
                    File.ReadAllBytes(filePath), null, DataProtectionScope.CurrentUser);
                args.TokenCache.DeserializeMsalV3(bytes, shouldClearExistingCache: true);
            }
            catch (CryptographicException) { /* different user/machine or corrupt */ }
            catch (MsalClientException) { /* malformed cache payload */ }
        });
        cache.SetAfterAccess(args =>
        {
            if (!args.HasStateChanged)
                return;
            var fullPath = Path.GetFullPath(filePath);
            Directory.CreateDirectory(Path.GetDirectoryName(fullPath)!);
            var protectedBytes = ProtectedData.Protect(
                args.TokenCache.SerializeMsalV3(), null, DataProtectionScope.CurrentUser);
            File.WriteAllBytes(fullPath, protectedBytes);
        });
    }
}
