using System.Security.Cryptography;

namespace Broker;

/// <summary>
/// Minimal ULID: 48-bit millisecond timestamp + 80 bits of randomness, Crockford
/// Base32.
///
/// Hand-rolled rather than taking a dependency because the properties that matter
/// here are few and checkable: lexicographically sortable by time, and safe to put
/// in a URL path. #5 confirmed all 26 characters are RFC 3986 unreserved, so the
/// deep link needs no percent-encoding and survives Markdown and JSON intact.
///
/// A ULID rather than the problem id, because one problem can trigger several
/// profiles and each needs to be addressable on its own.
/// </summary>
internal static class Ulid
{
    private const string Alphabet = "0123456789ABCDEFGHJKMNPQRSTVWXYZ";

    public static string New()
    {
        var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        Span<byte> random = stackalloc byte[10];
        RandomNumberGenerator.Fill(random);

        Span<char> chars = stackalloc char[26];

        // 48-bit timestamp across the first 10 characters.
        for (var i = 9; i >= 0; i--)
        {
            chars[i] = Alphabet[(int)(timestamp & 31)];
            timestamp >>= 5;
        }

        // 80 bits of randomness across the remaining 16, five bits at a time.
        var bits = 0;
        var buffer = 0;
        var outIndex = 10;
        foreach (var b in random)
        {
            buffer = (buffer << 8) | b;
            bits += 8;
            while (bits >= 5 && outIndex < 26)
            {
                bits -= 5;
                chars[outIndex++] = Alphabet[(buffer >> bits) & 31];
            }
        }
        while (outIndex < 26) chars[outIndex++] = Alphabet[0];

        return new string(chars);
    }
}
