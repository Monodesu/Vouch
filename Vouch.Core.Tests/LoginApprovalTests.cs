using System.Security.Cryptography;
using Vouch.Core.Steam;

namespace Vouch.Core.Tests;

public class LoginApprovalTests
{
    [Fact]
    public void ParseClientIds_ReadsUnpackedRepeated()
    {
        // Two field-1 varints, unpacked: 0x08 <id>  0x08 <id>
        var body = new byte[] { 0x08, 0x2A, 0x08, 0x81, 0x01 }; // 42, 129
        Assert.Equal(new ulong[] { 42, 129 }, SteamLoginApprovalService.ParseClientIds(body));
    }

    [Fact]
    public void ParseClientIds_ReadsPackedRepeated()
    {
        // Field 1, wire 2 (length-delimited), 3 bytes of packed varints: 42, 129
        var body = new byte[] { 0x0A, 0x03, 0x2A, 0x81, 0x01 };
        Assert.Equal(new ulong[] { 42, 129 }, SteamLoginApprovalService.ParseClientIds(body));
    }

    [Fact]
    public void ParseClientIds_EmptyBody_IsEmpty()
    {
        Assert.Empty(SteamLoginApprovalService.ParseClientIds(Array.Empty<byte>()));
    }

    [Theory]
    [InlineData("https://s.team/q/1/1234567890", true, 1, 1234567890UL)]
    [InlineData("https://s.team/q/1/1234567890?abc", true, 1, 1234567890UL)]
    [InlineData("http://s.team/q/2/999", true, 2, 999UL)]
    [InlineData("https://steamcommunity.com/foo", false, 0, 0UL)]
    [InlineData("not a url", false, 0, 0UL)]
    [InlineData("", false, 0, 0UL)]
    public void TryParseQrChallenge_ParsesSteamLoginUrls(string url, bool ok, int version, ulong clientId)
    {
        var parsed = SteamLoginApprovalService.TryParseQrChallenge(url, out var v, out var c);
        Assert.Equal(ok, parsed);
        if (ok)
        {
            Assert.Equal(version, v);
            Assert.Equal(clientId, c);
        }
    }

    [Fact]
    public void Sign_MatchesManualHmacOverLittleEndianLayout()
    {
        var secret = RandomNumberGenerator.GetBytes(20);
        int version = 1;
        ulong clientId = 0x1122334455667788;
        ulong steamId = 76561198000000001;

        var expectedMsg = new byte[18];
        System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(expectedMsg.AsSpan(0), (ushort)version);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(expectedMsg.AsSpan(2), clientId);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt64LittleEndian(expectedMsg.AsSpan(10), steamId);
        var expected = new HMACSHA256(secret).ComputeHash(expectedMsg);

        Assert.Equal(expected, SteamLoginApprovalService.Sign(secret, version, clientId, steamId));
    }

    [Fact]
    public void ProtobufWriters_RoundTripThroughParseClientIds()
    {
        // A hand-written field 1 (uint64) should read back via the same field parser.
        using var ms = new System.IO.MemoryStream();
        SteamLoginApprovalService.WriteVarintField(ms, 1, 999UL);
        Assert.Equal(new ulong[] { 999 }, SteamLoginApprovalService.ParseClientIds(ms.ToArray()));
    }
}
