using System.Security.Cryptography;
using System.Text;
using Vouch.Core.Steam;

namespace Vouch.Core.Tests;

public class SteamConfirmationTests
{
    // Independent re-implementation of the confirmation HMAC (different byte-writing).
    private static string IndependentHash(long time, string tag, string identitySecret)
    {
        var buf = new List<byte>();
        for (int i = 7; i >= 0; i--) buf.Add((byte)(time >> (i * 8)));
        buf.AddRange(Encoding.UTF8.GetBytes(tag.Length > 32 ? tag[..32] : tag));
        using var h = new HMACSHA1(Convert.FromBase64String(identitySecret));
        return Convert.ToBase64String(h.ComputeHash(buf.ToArray()));
    }

    [Fact]
    public void ConfirmationHash_MatchesIndependentImplementation()
    {
        var secret = Convert.ToBase64String(RandomNumberGenerator.GetBytes(20));
        foreach (var tag in new[] { "conf", "accept", "reject", "details" })
        {
            long time = RandomNumberGenerator.GetInt32(1, int.MaxValue);
            Assert.Equal(
                IndependentHash(time, tag, secret),
                SteamConfirmationService.GenerateConfirmationHash(time, tag, secret));
        }
    }

    [Fact]
    public void QueryParams_ContainsExpectedFields()
    {
        var creds = new ConfirmationCredentials(
            76561198000000001, Convert.ToBase64String(RandomNumberGenerator.GetBytes(20)),
            "android:abc-123", "tok");
        var q = SteamConfirmationService.QueryParams(creds, "conf", 1700000000);

        Assert.Contains("p=android%3Aabc-123", q);
        Assert.Contains("a=76561198000000001", q);
        Assert.Contains("t=1700000000", q);
        Assert.Contains("m=react", q);
        Assert.Contains("tag=conf", q);
        Assert.Contains("k=", q);
    }

    [Fact]
    public void SteamTime_ParsesQueryTimeResponse()
    {
        // Steam returns server_time as a string; a plain number must work too.
        Assert.Equal(1700000123,
            SteamTime.ParseServerTime("""{"response":{"server_time":"1700000123"}}"""));
        Assert.Equal(1700000123,
            SteamTime.ParseServerTime("""{"response":{"server_time":1700000123}}"""));
        Assert.Null(SteamTime.ParseServerTime("""{"response":{}}"""));
        Assert.Null(SteamTime.ParseServerTime("not json"));
    }

    [Fact]
    public void ParseConfirmations_ReadsEntries()
    {
        const string json = """
            {"success":true,"conf":[
              {"type":2,"type_name":"Market Listing","id":"709","creator_id":"383","nonce":"104",
               "creation_time":1687810731,"cancel":"Cancel","accept":"Confirm",
               "icon":"https://img/x.png","multi":false,"headline":"Sticker | Titan",
               "summary":["1 for 0.29"]},
              {"type":1,"type_name":"Trade Offer","id":"800","creator_id":"999","nonce":"205",
               "creation_time":1687810800,"headline":"Trade with Bob","summary":["You give 2","You get 1"]}
            ]}
            """;
        var confs = SteamConfirmationService.ParseConfirmations(json);

        Assert.Equal(2, confs.Count);
        Assert.Equal("709", confs[0].Id);
        Assert.Equal("104", confs[0].Nonce);
        Assert.Equal("Market Listing", confs[0].TypeName);
        Assert.Equal("Sticker | Titan", confs[0].Headline);
        Assert.Equal(new[] { "1 for 0.29" }, confs[0].Summary);
        Assert.Equal(2, confs[1].Summary.Count);
    }

    [Fact]
    public void ParseConfirmations_HandlesFailureAndGarbage()
    {
        Assert.Empty(SteamConfirmationService.ParseConfirmations("""{"success":false,"needauth":true}"""));
        Assert.Empty(SteamConfirmationService.ParseConfirmations("not json"));
        Assert.Empty(SteamConfirmationService.ParseConfirmations("""{"success":true}"""));
    }
}
