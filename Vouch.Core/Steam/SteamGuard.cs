using System.Security.Cryptography;

namespace Vouch.Core.Steam;

/// <summary>
/// Steam's TOTP algorithm — byte-for-byte compatible with SteamAuth's
/// GenerateSteamGuardCodeForTime (HMAC-SHA1 over the 30-second window, Steam's
/// custom base-26 alphabet).
/// </summary>
public static class SteamGuard
{
    private static readonly char[] Alphabet = "23456789BCDFGHJKMNPQRTVWXY".ToCharArray();

    public const int Period = 30;

    public static long CurrentWindow(DateTimeOffset now) => now.ToUnixTimeSeconds() / Period;

    public static double SecondsRemaining(DateTimeOffset now)
    {
        double secs = now.ToUnixTimeMilliseconds() / 1000.0;
        return Period - (secs % Period);
    }

    /// <summary>Generates the 5-character login code for a given time window from raw secret bytes.</summary>
    public static string GenerateCode(byte[] sharedSecret, long window)
    {
        Span<byte> time = stackalloc byte[8];
        for (int i = 8; i > 0; i--)
        {
            time[i - 1] = (byte)(window & 0xFF);
            window >>= 8;
        }

        byte[] hmac = HMACSHA1.HashData(sharedSecret, time.ToArray());
        int offset = hmac[^1] & 0x0F;
        int fullCode =
            ((hmac[offset] & 0x7F) << 24) |
            ((hmac[offset + 1] & 0xFF) << 16) |
            ((hmac[offset + 2] & 0xFF) << 8) |
            (hmac[offset + 3] & 0xFF);

        Span<char> code = stackalloc char[5];
        for (int i = 0; i < 5; i++)
        {
            code[i] = Alphabet[fullCode % Alphabet.Length];
            fullCode /= Alphabet.Length;
        }
        return new string(code);
    }

    /// <summary>Convenience overload: decodes a base64 <c>shared_secret</c> and generates the code.</summary>
    public static string GenerateCode(string sharedSecretBase64, long window)
        => GenerateCode(Convert.FromBase64String(sharedSecretBase64), window);
}
