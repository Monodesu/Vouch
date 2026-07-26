using System.Buffers.Binary;
using System.Security.Cryptography;
using Vouch.Core.Steam;

namespace Vouch.Core.Tests;

public class SteamGuardTests
{
    private const string Alphabet = "23456789BCDFGHJKMNPQRTVWXY";

    /// <summary>
    /// A deliberately different re-implementation of Steam's TOTP (BinaryPrimitives for the
    /// big-endian counter, a separate truncation expression). Matching the library across many
    /// random inputs validates the derivation logic, not just a shared code path.
    /// </summary>
    private static string IndependentCode(byte[] secret, long window)
    {
        Span<byte> counter = stackalloc byte[8];
        BinaryPrimitives.WriteInt64BigEndian(counter, window);

        byte[] mac = HMACSHA1.HashData(secret, counter.ToArray());
        int off = mac[mac.Length - 1] & 0xF;
        long code = ((long)(mac[off] & 0x7F) << 24)
                    + ((long)(mac[off + 1] & 0xFF) << 16)
                    + ((long)(mac[off + 2] & 0xFF) << 8)
                    + (mac[off + 3] & 0xFF);

        var chars = new char[5];
        for (int i = 0; i < 5; i++)
        {
            chars[i] = Alphabet[(int)(code % 26)];
            code /= 26;
        }
        return new string(chars);
    }

    [Fact]
    public void Totp_MatchesIndependentImplementation_AcrossRandomInputs()
    {
        for (int i = 0; i < 500; i++)
        {
            var secret = RandomNumberGenerator.GetBytes(20);
            long window = RandomNumberGenerator.GetInt32(1, int.MaxValue);

            Assert.Equal(IndependentCode(secret, window), SteamGuard.GenerateCode(secret, window));
        }
    }

    [Fact]
    public void Totp_ProducesFiveCharsFromSteamAlphabet()
    {
        var code = SteamGuard.GenerateCode(RandomNumberGenerator.GetBytes(20), 56_000_000);
        Assert.Equal(5, code.Length);
        Assert.All(code, c => Assert.Contains(c, Alphabet));
    }

    [Fact]
    public void Totp_IsStableForSameWindow_AndBase64OverloadAgrees()
    {
        var secret = RandomNumberGenerator.GetBytes(20);
        var b64 = Convert.ToBase64String(secret);
        long window = 56_123_456;

        Assert.Equal(SteamGuard.GenerateCode(secret, window), SteamGuard.GenerateCode(secret, window));
        Assert.Equal(SteamGuard.GenerateCode(secret, window), SteamGuard.GenerateCode(b64, window));
    }
}
