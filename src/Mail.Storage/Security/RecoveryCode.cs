using System.Security.Cryptography;
using System.Text;

namespace Mail.Storage.Security;

/// <summary>
/// Human-typable recovery codes: 8 groups of 4 Crockford base32 characters (160 bits),
/// e.g. "7Q4M-K2P9-....". Normalization forgives case, separators, and O/0, I/L/1 mixups.
/// </summary>
public static class RecoveryCode
{
    // Crockford base32: no I, L, O, U.
    const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";
    public const int Length = 32;

    public static string Generate()
    {
        var chars = RandomNumberGenerator.GetItems<char>(Alphabet, Length);
        var sb = new StringBuilder(Length + 7);
        for (var i = 0; i < Length; i++)
        {
            if (i > 0 && i % 4 == 0) sb.Append('-');
            sb.Append(chars[i]);
        }
        return sb.ToString();
    }

    /// <summary>Canonical 32-char form used as KDF input; throws <see cref="FormatException"/> on garbage.</summary>
    public static string Normalize(string input)
    {
        var sb = new StringBuilder(Length);
        foreach (var raw in input.ToUpperInvariant())
        {
            if (char.IsWhiteSpace(raw) || raw == '-') continue;
            var c = raw switch { 'O' => '0', 'I' or 'L' => '1', _ => raw };
            if (!Alphabet.Contains(c))
                throw new FormatException($"Recovery code contains an invalid character: '{raw}'.");
            sb.Append(c);
        }
        if (sb.Length != Length)
            throw new FormatException($"Recovery code must have {Length} characters; got {sb.Length}.");
        return sb.ToString();
    }
}
