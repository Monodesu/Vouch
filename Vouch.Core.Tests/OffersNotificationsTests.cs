using Vouch.Core.Steam;

namespace Vouch.Core.Tests;

public class OffersNotificationsTests
{
    [Fact]
    public void ParseOffers_ReceivedAndSent()
    {
        const string json = """
        {"response":{
          "trade_offers_received":[
            {"tradeofferid":"111","accountid_other":123,"trade_offer_state":2,"message":"hi",
             "items_to_give":[],
             "items_to_receive":[{"appid":730,"classid":"c1","instanceid":"i1","amount":"1"},
                                 {"appid":730,"classid":"c2","instanceid":"i2","amount":"3"}]}
          ],
          "trade_offers_sent":[
            {"tradeofferid":"222","accountid_other":456,"trade_offer_state":2,
             "items_to_give":[{"appid":730,"classid":"c1","instanceid":"i1","amount":"1"}],"items_to_receive":[]},
            {"tradeofferid":"333","accountid_other":789,"trade_offer_state":3,
             "items_to_give":[{"appid":730,"classid":"c1","instanceid":"i1"}],"items_to_receive":[]}
          ],
          "descriptions":[
            {"appid":730,"classid":"c1","instanceid":"i1","market_name":"AK-47 | Redline"},
            {"appid":730,"classid":"c2","instanceid":"i2","market_name":"Mann Co. Key"}
          ]}}
        """;
        var offers = SteamTradeOffersService.ParseOffers(json);
        Assert.Equal(2, offers.Count); // the accepted (state 3) sent offer is dropped
        Assert.DoesNotContain(offers, o => o.Id == "333");

        var recv = offers.Single(o => o.Id == "111");
        Assert.True(recv.IsIncoming);
        Assert.Equal(76561197960265728UL + 123, recv.PartnerSteamId);
        Assert.Equal(0, recv.GiveCount);
        Assert.Equal(2, recv.ReceiveCount);
        Assert.Equal("hi", recv.Message);
        Assert.Equal("AK-47 | Redline", recv.ReceiveItems[0].Name);
        Assert.Equal("Mann Co. Key", recv.ReceiveItems[1].Name);
        Assert.Equal(3, recv.ReceiveItems[1].Amount);
        Assert.Equal(730, recv.ReceiveItems[0].AppId);

        var sent = offers.Single(o => o.Id == "222");
        Assert.False(sent.IsIncoming);
        Assert.Equal(1, sent.GiveCount);
        Assert.Equal("AK-47 | Redline", sent.GiveItems[0].Name);
    }

    [Fact]
    public void ParseOffers_Empty()
    {
        Assert.Empty(SteamTradeOffersService.ParseOffers("""{"response":{}}"""));
        Assert.Empty(SteamTradeOffersService.ParseOffers("garbage"));
    }

    [Fact]
    public void ParseActionResult_SuccessAndError()
    {
        var ok = SteamTradeOffersService.ParseActionResult("""{"tradeid":"5","needs_mobile_confirmation":true}""", true);
        Assert.True(ok.Ok);
        Assert.True(ok.NeedsMobileConfirmation);

        var err = SteamTradeOffersService.ParseActionResult("""{"strError":"This trade offer is no longer valid."}""", true);
        Assert.False(err.Ok);
        Assert.Contains("no longer valid", err.Error);

        var declined = SteamTradeOffersService.ParseActionResult("{}", true); // decline returns {}
        Assert.True(declined.Ok);
    }

    [Fact]
    public void ParseAppName_ReadsSuccessfulLookup()
    {
        const string json = """{"440":{"success":true,"data":{"name":"Team Fortress 2","steam_appid":440}}}""";
        Assert.Equal("Team Fortress 2", SteamTradeOffersService.ParseAppName(json, 440));
        Assert.Equal("", SteamTradeOffersService.ParseAppName("""{"440":{"success":false}}""", 440));
        Assert.Equal("", SteamTradeOffersService.ParseAppName("garbage", 440));
    }

    [Fact]
    public void ParseTradePartnerPage_ScrapesLevelMemberFriend()
    {
        // condensed from a real trade offer page
        const string html = """
        <div class="trade_partner_info_block">
          <div class="trade_partner_icon"><img src="https://x/comment_friendindicator_small.png"></div>
          You've been friends for <div class="trade_partner_info_text trade_partner_not_friends_for_long"> 1 day </div>
        </div>
        <div class="trade_partner_steam_level"><div class="friendPlayerLevel lvl_100"><span class="friendPlayerLevelNum">133</span></div></div>
        <div class="trade_partner_member trade_partner_info_text">Mono has been on Steam since</div>
        <div class="trade_partner_member_since trade_partner_info_text ">24 July, 2016</div>
        """;
        var p = SteamTradeOffersService.ParseTradePartnerPage(html);
        Assert.Equal(133, p.Level);
        Assert.Equal("24 July, 2016", p.MemberSince);
        Assert.True(p.IsFriend);
        Assert.Equal("1 day", p.FriendFor);

        var np = SteamTradeOffersService.ParseTradePartnerPage("<div>no partner block here</div>");
        Assert.False(np.IsFriend);
        Assert.Equal(0, np.Level);
        Assert.Equal("", np.MemberSince);
    }

    [Fact]
    public void ParseNotifications_ExtractsFields()
    {
        const string json = """
        {"response":{"notifications":[
          {"notification_id":"99","notification_type":4,"read":false,"timestamp":1700000000},
          {"notification_id":"98","notification_type":5,"read":true,"timestamp":1699999999}
        ]}}
        """;
        var n = SteamNotificationsService.ParseNotifications(json);
        Assert.Equal(2, n.Count);
        Assert.Equal("Item received", n[0].TypeName);
        Assert.False(n[0].Read);
        Assert.Equal("Comment", n[1].TypeName);
        Assert.True(n[1].Read);
    }

    [Fact]
    public void ParseNotifications_Empty()
    {
        Assert.Empty(SteamNotificationsService.ParseNotifications("""{"response":{}}"""));
        Assert.Empty(SteamNotificationsService.ParseNotifications("nope"));
    }
}
