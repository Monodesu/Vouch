using Vouch.Core.Steam;

namespace Vouch.Core.Tests;

public class SteamWebClientTests
{
    private const string ProfileXml = """
        <?xml version="1.0" encoding="UTF-8" standalone="yes"?>
        <profile>
          <steamID64>76561197960287930</steamID64>
          <steamID><![CDATA[Rabscuttle]]></steamID>
          <avatarFull><![CDATA[https://avatars.example/abc_full.jpg]]></avatarFull>
          <vacBanned>1</vacBanned>
          <tradeBanState>Banned</tradeBanState>
          <isLimitedAccount>0</isLimitedAccount>
        </profile>
        """;

    [Fact]
    public void ParseProfileXml_ExtractsFields()
    {
        var p = SteamWebClient.ParseProfileXml(ProfileXml);
        Assert.NotNull(p);
        Assert.Equal("Rabscuttle", p!.PersonaName);
        Assert.Equal("https://avatars.example/abc_full.jpg", p.AvatarUrl);
        Assert.True(p.VacBanned);
        Assert.True(p.TradeBanned);
        Assert.False(p.LimitedAccount);
    }

    [Fact]
    public void ParseProfileXml_CleanAccount()
    {
        var xml = ProfileXml.Replace("<vacBanned>1</vacBanned>", "<vacBanned>0</vacBanned>")
                            .Replace("<tradeBanState>Banned</tradeBanState>", "<tradeBanState>None</tradeBanState>");
        var p = SteamWebClient.ParseProfileXml(xml);
        Assert.NotNull(p);
        Assert.False(p!.VacBanned);
        Assert.False(p.TradeBanned);
    }

    [Fact]
    public void ParseProfileXml_PrivateOrError_ReturnsNull()
    {
        // Steam returns a <response> root for private/non-existent profiles.
        Assert.Null(SteamWebClient.ParseProfileXml("<response><error>The specified profile could not be found.</error></response>"));
        Assert.Null(SteamWebClient.ParseProfileXml("not xml at all"));
    }

    [Fact]
    public void ParseBansJson_ExtractsCounts()
    {
        const string json = """
            {"players":[{"SteamId":"76561197960287930","CommunityBanned":false,"VACBanned":true,
            "NumberOfVACBans":2,"DaysSinceLastBan":100,"NumberOfGameBans":1,"EconomyBan":"none"}]}
            """;
        var b = SteamWebClient.ParseBansJson(json);
        Assert.NotNull(b);
        Assert.Equal(2, b!.VacBans);
        Assert.Equal(1, b.GameBans);
        Assert.False(b.CommunityBanned);
        Assert.Equal("none", b.EconomyBan);
    }

    [Fact]
    public void ParseBansJson_Empty_ReturnsNull()
    {
        Assert.Null(SteamWebClient.ParseBansJson("""{"players":[]}"""));
        Assert.Null(SteamWebClient.ParseBansJson("garbage"));
    }

    [Fact]
    public void ParseBanStatusHtml_GameBan_ParsesCount()
    {
        // Real banner shape from the profile page (?l=english).
        const string html = """
            <div class="profile_ban_status"> 1 game ban on record | <span class="profile_ban_info">Info</span>
            121 day(s) since last ban </div>
            """;
        var s = SteamWebClient.ParseBanStatusHtml(html);
        Assert.Equal(1, s.GameBans);
        Assert.Equal(0, s.VacBans);
        Assert.False(s.TradeBanned);
    }

    [Fact]
    public void ParseBanStatusHtml_MultipleVacAndTrade()
    {
        const string html = """
            <div class="profile_ban_status"> 2 VAC bans on record | Info
            Currently trade banned </div>
            """;
        var s = SteamWebClient.ParseBanStatusHtml(html);
        Assert.Equal(2, s.VacBans);
        Assert.True(s.TradeBanned);
    }

    [Fact]
    public void ParseBanStatusHtml_UnnumberedBanner_CountsOne()
    {
        var s = SteamWebClient.ParseBanStatusHtml(
            "<div class=\"profile_ban_status\">Multiple VAC bans on record | Info</div>");
        Assert.Equal(1, s.VacBans);
    }

    [Fact]
    public void ParseBanStatusHtml_NoBanner_IsClean()
    {
        // A clean profile has no ban_status block; stray page text must not false-positive.
        var s = SteamWebClient.ParseBanStatusHtml("<html><body>Steam is a game ban... no banner here</body></html>");
        Assert.Equal(0, s.GameBans);
        Assert.Equal(0, s.VacBans);
        Assert.False(s.TradeBanned);
    }
}
