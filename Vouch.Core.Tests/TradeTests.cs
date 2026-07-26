using System.Text.Json;
using Vouch.Core.Steam;

namespace Vouch.Core.Tests;

public class TradeTests
{
    [Fact]
    public void ParseTradeUrl_ExtractsPartnerAndToken()
    {
        var t = SteamTradeService.ParseTradeUrl(
            "https://steamcommunity.com/tradeoffer/new/?partner=123456&token=aB_cd-12");
        Assert.NotNull(t);
        Assert.Equal(76561197960265728UL + 123456UL, t!.PartnerSteamId);
        Assert.Equal((uint)123456, t.PartnerAccountId);
        Assert.Equal("aB_cd-12", t.Token);
    }

    [Theory]
    [InlineData("https://steamcommunity.com/tradeoffer/new/?partner=123")] // no token
    [InlineData("not a url")]
    [InlineData("")]
    public void ParseTradeUrl_Invalid_ReturnsNull(string url)
        => Assert.Null(SteamTradeService.ParseTradeUrl(url));

    [Fact]
    public void BuildOfferJson_PutsItemsOnMeSide()
    {
        var assets = new[]
        {
            new TradeAsset(730, "2", "111", 1),
            new TradeAsset(730, "2", "222", 3),
        };
        var json = SteamTradeService.BuildOfferJson(assets);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;

        Assert.True(root.GetProperty("newversion").GetBoolean());
        var me = root.GetProperty("me").GetProperty("assets");
        Assert.Equal(2, me.GetArrayLength());
        Assert.Equal("111", me[0].GetProperty("assetid").GetString());
        Assert.Equal("2", me[0].GetProperty("contextid").GetString()); // contextid stays a string
        Assert.Equal(3, me[1].GetProperty("amount").GetInt32());
        Assert.Equal(0, root.GetProperty("them").GetProperty("assets").GetArrayLength());
    }

    [Fact]
    public void ParseSendResult_Success()
    {
        var r = SteamTradeService.ParseSendResult(
            """{"tradeofferid":"399123","needs_mobile_confirmation":true,"needs_email_confirmation":false}""");
        Assert.True(r.Ok);
        Assert.Equal("399123", r.TradeOfferId);
        Assert.True(r.NeedsMobileConfirmation);
    }

    [Fact]
    public void ParseSendResult_Errors()
    {
        var err = SteamTradeService.ParseSendResult("""{"strError":"There was an error (26)"}""");
        Assert.False(err.Ok);
        Assert.Contains("26", err.Error);

        var html = SteamTradeService.ParseSendResult("<html>session lost</html>");
        Assert.False(html.Ok);
        Assert.NotNull(html.Error);
    }

    [Fact]
    public void ParseTradable_KeepsOnlyTradableItems()
    {
        const string json = """
        {
          "assets": [
            {"appid":730,"contextid":"2","assetid":"1001","classid":"c1","instanceid":"i1","amount":"1"},
            {"appid":730,"contextid":"2","assetid":"1002","classid":"c2","instanceid":"i2","amount":"1"},
            {"appid":730,"contextid":"2","assetid":"1003","classid":"c1","instanceid":"i1","amount":"1"}
          ],
          "descriptions": [
            {"classid":"c1","instanceid":"i1","tradable":1},
            {"classid":"c2","instanceid":"i2","tradable":0}
          ]
        }
        """;
        var items = SteamInventoryClient.ParseTradable(json, 730, "2");
        Assert.Equal(2, items.Count); // 1001 and 1003 (class c1 tradable); 1002 excluded
        Assert.All(items, i => Assert.Equal(730, i.AppId));
        Assert.Contains(items, i => i.AssetId == "1001");
        Assert.Contains(items, i => i.AssetId == "1003");
        Assert.DoesNotContain(items, i => i.AssetId == "1002");
    }

    [Fact]
    public void ParseTradable_BadInput_Empty()
    {
        Assert.Empty(SteamInventoryClient.ParseTradable("nope", 730, "2"));
        Assert.Empty(SteamInventoryClient.ParseTradable("""{"success":0}""", 730, "2"));
    }

    [Fact]
    public void ParseTradableItems_KeepsNames()
    {
        const string json = """
        {
          "assets": [
            {"appid":730,"contextid":"2","assetid":"1001","classid":"c1","instanceid":"i1","amount":"1"},
            {"appid":730,"contextid":"2","assetid":"1002","classid":"c2","instanceid":"i2","amount":"1"}
          ],
          "descriptions": [
            {"classid":"c1","instanceid":"i1","tradable":1,"market_name":"AK-47 | Redline","icon_url":"abc123"},
            {"classid":"c2","instanceid":"i2","tradable":0,"market_name":"Locked Case"}
          ]
        }
        """;
        var items = SteamInventoryClient.ParseTradableItems(json, 730, "2");
        var only = Assert.Single(items);              // c2 is not tradable
        Assert.Equal("1001", only.AssetId);
        Assert.Equal("AK-47 | Redline", only.Name);
        Assert.Equal("abc123", only.IconUrl);
    }
}
