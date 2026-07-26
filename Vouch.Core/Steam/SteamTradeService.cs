using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vouch.Core.Steam;

/// <summary>The recipient of a trade offer, parsed from a trade URL.</summary>
public record TradeTarget(ulong PartnerSteamId, string Token)
{
    /// <summary>The 32-bit account id form Steam uses in a trade URL's <c>partner=</c> param.</summary>
    public uint PartnerAccountId => (uint)(PartnerSteamId - 76561197960265728UL);
}

/// <summary>One item to put into a trade offer.</summary>
public record TradeAsset(int AppId, string ContextId, string AssetId, int Amount = 1);

/// <summary>Outcome of sending a trade offer.</summary>
public record TradeSendResult(bool Ok, string? TradeOfferId, bool NeedsMobileConfirmation, string? Error);

/// <summary>
/// Sends Steam trade offers to a trade-URL target using the account's web session (the login access
/// token as the <c>steamLoginSecure</c> cookie). URL parsing / offer-body / response parsing are pure
/// and tested; the actual POST needs a live account and can only be verified against real Steam.
/// </summary>
public class SteamTradeService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly string SessionId =
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return http;
    }

    public async Task<TradeSendResult> SendOfferAsync(
        ulong fromSteamId, string accessToken, TradeTarget target, IReadOnlyList<TradeAsset> myAssets,
        string message = "", CancellationToken ct = default)
    {
        if (myAssets.Count == 0) return new TradeSendResult(false, null, false, "No items to trade.");

        var form = new Dictionary<string, string>
        {
            ["sessionid"] = SessionId,
            ["serverid"] = "1",
            ["partner"] = target.PartnerSteamId.ToString(),
            ["tradeoffermessage"] = message,
            ["json_tradeoffer"] = BuildOfferJson(myAssets),
            ["captcha"] = "",
            ["trade_offer_create_params"] = $"{{\"trade_offer_access_token\":\"{target.Token}\"}}",
        };

        using var req = new HttpRequestMessage(HttpMethod.Post, "https://steamcommunity.com/tradeoffer/new/send")
        {
            Content = new FormUrlEncodedContent(form),
        };
        var loginSecure = $"{fromSteamId}%7C%7C{WebUtility.UrlEncode(accessToken)}";
        req.Headers.Add("Cookie", string.Join("; ",
            $"sessionid={SessionId}", $"steamid={fromSteamId}", $"steamLoginSecure={loginSecure}"));
        req.Headers.Referrer = new Uri(
            $"https://steamcommunity.com/tradeoffer/new/?partner={target.PartnerAccountId}&token={target.Token}");

        try
        {
            var resp = await Http.SendAsync(req, ct);
            return ParseSendResult(await resp.Content.ReadAsStringAsync(ct));
        }
        catch (Exception ex)
        {
            return new TradeSendResult(false, null, false, ex.Message);
        }
    }

    // ---- pure, testable ----

    /// <summary>Parses a Steam trade URL (<c>…/tradeoffer/new/?partner=ACCOUNTID&amp;token=TOKEN</c>).</summary>
    public static TradeTarget? ParseTradeUrl(string url)
    {
        var partner = Regex.Match(url ?? "", @"partner=(\d+)");
        var token = Regex.Match(url ?? "", @"token=([A-Za-z0-9_-]+)");
        if (!partner.Success || !token.Success) return null;
        if (!uint.TryParse(partner.Groups[1].Value, out var accountId)) return null;
        return new TradeTarget(76561197960265728UL + accountId, token.Groups[1].Value);
    }

    /// <summary>The <c>json_tradeoffer</c> body: our items on the "me" side, nothing on "them".</summary>
    public static string BuildOfferJson(IReadOnlyList<TradeAsset> myAssets)
    {
        var me = new
        {
            assets = myAssets.Select(a => new
            {
                appid = a.AppId,
                contextid = a.ContextId,
                amount = a.Amount,
                assetid = a.AssetId,
            }).ToArray(),
            currency = Array.Empty<object>(),
            ready = false,
        };
        var them = new { assets = Array.Empty<object>(), currency = Array.Empty<object>(), ready = false };
        return JsonSerializer.Serialize(new { newversion = true, version = myAssets.Count + 1, me, them });
    }

    public static TradeSendResult ParseSendResult(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("tradeofferid", out var id))
            {
                bool needs = root.TryGetProperty("needs_mobile_confirmation", out var nc)
                             && nc.ValueKind == JsonValueKind.True;
                return new TradeSendResult(true, id.GetString(), needs, null);
            }
            if (root.TryGetProperty("strError", out var err))
                return new TradeSendResult(false, null, false, err.GetString());
            return new TradeSendResult(false, null, false, "Unexpected response from Steam.");
        }
        catch (JsonException)
        {
            // A non-JSON body means Steam returned an HTML error page — usually an expired session.
            return new TradeSendResult(false, null, false, "Session invalid — sign in again.");
        }
    }
}
