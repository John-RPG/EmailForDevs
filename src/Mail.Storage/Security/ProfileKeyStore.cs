using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Mail.Storage.Security;

/// <summary>The unwrapped master key plus the one-time-displayed recovery code.</summary>
public sealed record ProfileKeys(byte[] MasterKey, string RecoveryCode);

/// <summary>master.key cannot be read on this machine/user; the recovery code is needed.</summary>
public sealed class ProfileKeyUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

public sealed class InvalidRecoveryCodeException(Exception? inner = null)
    : Exception("The recovery code is incorrect.", inner);

/// <summary>
/// Manages a profile's 32-byte master key, wrapped twice on disk:
///  - master.key    — DPAPI (CurrentUser); machine+user bound, used silently day-to-day.
///  - recovery.blob — AES-256-GCM under a key PBKDF2-derived from the recovery code;
///                    machine-independent, so it travels with backups.
/// Losing DPAPI (reinstall, new machine) is survivable via <see cref="Recover"/>, which
/// also rewrites master.key for the current Windows user.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ProfileKeyStore(string profileDirectory)
{
    public const int KeySizeBytes = 32;
    const byte MasterKeyFileVersion = 1;
    const int Pbkdf2Iterations = 600_000;
    const int NonceSize = 12;
    const int TagSize = 16;

    public string MasterKeyPath => Path.Combine(profileDirectory, "master.key");
    public string RecoveryBlobPath => Path.Combine(profileDirectory, "recovery.blob");

    public bool Exists => File.Exists(MasterKeyPath) || File.Exists(RecoveryBlobPath);

    public ProfileKeys Create()
    {
        if (Exists)
            throw new InvalidOperationException($"Profile key material already exists in '{profileDirectory}'.");
        Directory.CreateDirectory(profileDirectory);
        var masterKey = RandomNumberGenerator.GetBytes(KeySizeBytes);
        var code = RecoveryCode.Generate();
        WriteRecoveryBlob(masterKey, code);
        WriteMasterKeyFile(masterKey);
        return new ProfileKeys(masterKey, code);
    }

    public byte[] Unlock()
    {
        if (!File.Exists(MasterKeyPath))
            throw new ProfileKeyUnavailableException("master.key is missing.");
        var file = File.ReadAllBytes(MasterKeyPath);
        if (file.Length < 2 || file[0] != MasterKeyFileVersion)
            throw new ProfileKeyUnavailableException("master.key has an unknown format.");
        try
        {
            return ProtectedData.Unprotect(file[1..], null, DataProtectionScope.CurrentUser);
        }
        catch (CryptographicException ex)
        {
            throw new ProfileKeyUnavailableException(
                "DPAPI could not decrypt master.key (different Windows user or restored profile).", ex);
        }
    }

    /// <summary>Unwraps the master key with the recovery code and re-protects master.key for this Windows user.</summary>
    public byte[] Recover(string recoveryCode)
    {
        if (!File.Exists(RecoveryBlobPath))
            throw new FileNotFoundException("recovery.blob is missing; the profile is unrecoverable.", RecoveryBlobPath);
        var blob = JsonSerializer.Deserialize<RecoveryBlob>(File.ReadAllBytes(RecoveryBlobPath))
                   ?? throw new InvalidDataException("recovery.blob is empty.");
        var kek = DeriveKek(recoveryCode, blob.Salt, blob.Iterations);
        var masterKey = new byte[blob.Ciphertext.Length];
        try
        {
            using var gcm = new AesGcm(kek, TagSize);
            gcm.Decrypt(blob.Nonce, blob.Ciphertext, blob.Tag, masterKey);
        }
        catch (CryptographicException ex)
        {
            throw new InvalidRecoveryCodeException(ex);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
        WriteMasterKeyFile(masterKey);
        return masterKey;
    }

    /// <summary>Issues a new recovery code; the old one stops working immediately.</summary>
    public string RotateRecoveryCode(byte[] masterKey)
    {
        var code = RecoveryCode.Generate();
        WriteRecoveryBlob(masterKey, code);
        return code;
    }

    void WriteMasterKeyFile(byte[] masterKey)
    {
        var wrapped = ProtectedData.Protect(masterKey, null, DataProtectionScope.CurrentUser);
        var file = new byte[wrapped.Length + 1];
        file[0] = MasterKeyFileVersion;
        wrapped.CopyTo(file, 1);
        File.WriteAllBytes(MasterKeyPath, file);
    }

    void WriteRecoveryBlob(byte[] masterKey, string code)
    {
        var salt = RandomNumberGenerator.GetBytes(16);
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var ciphertext = new byte[masterKey.Length];
        var tag = new byte[TagSize];
        var kek = DeriveKek(code, salt, Pbkdf2Iterations);
        try
        {
            using var gcm = new AesGcm(kek, TagSize);
            gcm.Encrypt(nonce, masterKey, ciphertext, tag);
        }
        finally
        {
            CryptographicOperations.ZeroMemory(kek);
        }
        var blob = new RecoveryBlob
        {
            Iterations = Pbkdf2Iterations,
            Salt = salt,
            Nonce = nonce,
            Ciphertext = ciphertext,
            Tag = tag,
        };
        File.WriteAllBytes(RecoveryBlobPath, JsonSerializer.SerializeToUtf8Bytes(blob));
    }

    static byte[] DeriveKek(string code, byte[] salt, int iterations) =>
        Rfc2898DeriveBytes.Pbkdf2(
            Encoding.ASCII.GetBytes(RecoveryCode.Normalize(code)),
            salt, iterations, HashAlgorithmName.SHA256, KeySizeBytes);

    sealed class RecoveryBlob
    {
        public int Version { get; set; } = 1;
        public string Kdf { get; set; } = "PBKDF2-SHA256";
        public int Iterations { get; set; }
        public byte[] Salt { get; set; } = [];
        public byte[] Nonce { get; set; } = [];
        public byte[] Ciphertext { get; set; } = [];
        public byte[] Tag { get; set; } = [];
    }
}
