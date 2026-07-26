using System.Net;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vouch.Core.Steam;

/// <summary>Partner facts scraped from the trade offer page (the header Steam renders atop an offer):
/// Steam level, "member since" text, and friendship (with the "friends for" duration string).
/// Values are empty/0 when the page didn't include them.</summary>
public record TradePartnerPage(int Level, string MemberSince, bool IsFriend, string FriendFor);

/// <summary>ETradeOfferState.Active — the only state that's still actionable (accept/decline/cancel).</summary>
internal static class TradeOfferState { public const int Active = 2; }

/// <summary>One item in a trade offer. <see cref="Name"/>/<see cref="IconUrl"/> are empty when Steam
/// sent no description for it. IconUrl is the raw economy-image path (no host/size suffix). AppId is
/// the owning game — the reliable anti-spoof signal, since a look-alike item can't fake it.</summary>
public record TradeItem(string Name, int Amount, string IconUrl, int AppId);

/// <summary>An active trade offer (incoming or outgoing) with item counts and, when descriptions are
/// available, the itemized give/receive lists for an in-app detail view.</summary>
public record TradeOffer(
    string Id,
    bool IsIncoming,
    ulong PartnerSteamId,
    int GiveCount,
    int ReceiveCount,
    int State,
    string Message,
    IReadOnlyList<TradeItem> GiveItems,
    IReadOnlyList<TradeItem> ReceiveItems)
{
    public uint PartnerAccountId => (uint)(PartnerSteamId - 76561197960265728UL);
}

/// <summary>Result of accepting/declining an offer.</summary>
public record OfferActionResult(bool Ok, bool NeedsMobileConfirmation, string? Error);

/// <summary>
/// Lists and answers Steam trade offers using the account's access token (no Web API key).
/// GetTradeOffers is read via <c>access_token</c>; accept/decline hit the community endpoints with the
/// session cookie. Parsing is pure/testable; the network actions can only be verified against Steam.
/// </summary>
public class SteamTradeOffersService
{
    private static readonly HttpClient Http = CreateClient();
    private static readonly string SessionId =
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return http;
    }

    /// <summary>Active received + sent offers. Throws on a failed request so the caller can prompt re-login.</summary>
    public async Task<IReadOnlyList<TradeOffer>> FetchAsync(string accessToken, CancellationToken ct = default)
    {
        var url = "https://api.steampowered.com/IEconService/GetTradeOffers/v1/"
                  + $"?access_token={WebUtility.UrlEncode(accessToken)}"
                  + "&get_received_offers=true&get_sent_offers=true&active_only=true"
                  + "&get_descriptions=true&language=english";
        var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.StatusCode}");
        return ParseOffers(await resp.Content.ReadAsStringAsync(ct));
    }

    public async Task<OfferActionResult> AcceptAsync(
        ulong steamId, string accessToken, TradeOffer offer, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["sessionid"] = SessionId,
            ["serverid"] = "1",
            ["tradeofferid"] = offer.Id,
            ["partner"] = offer.PartnerSteamId.ToString(),
            ["captcha"] = "",
        };
        return await PostAsync(steamId, accessToken, $"https://steamcommunity.com/tradeoffer/{offer.Id}/accept", form, offer.Id, ct);
    }

    public async Task<OfferActionResult> DeclineAsync(
        ulong steamId, string accessToken, TradeOffer offer, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string> { ["sessionid"] = SessionId };
        var verb = offer.IsIncoming ? "decline" : "cancel";
        return await PostAsync(steamId, accessToken, $"https://steamcommunity.com/tradeoffer/{offer.Id}/{verb}", form, offer.Id, ct);
    }

    private async Task<OfferActionResult> PostAsync(
        ulong steamId, string accessToken, string url, Dictionary<string, string> form, string offerId, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, url) { Content = new FormUrlEncodedContent(form) };
        var loginSecure = $"{steamId}%7C%7C{WebUtility.UrlEncode(accessToken)}";
        req.Headers.Add("Cookie", string.Join("; ",
            $"sessionid={SessionId}", $"steamid={steamId}", $"steamLoginSecure={loginSecure}"));
        req.Headers.Referrer = new Uri($"https://steamcommunity.com/tradeoffer/{offerId}/");
        try
        {
            var resp = await Http.SendAsync(req, ct);
            return ParseActionResult(await resp.Content.ReadAsStringAsync(ct), resp.IsSuccessStatusCode);
        }
        catch (Exception ex) { return new OfferActionResult(false, false, ex.Message); }
    }

    /// <summary>Downloads the offer's web page (with the session cookie) so its partner header can be
    /// scraped for level / member-since / friendship — data the token web APIs don't return.</summary>
    public async Task<string> FetchOfferPageAsync(string offerId, ulong steamId, string accessToken, CancellationToken ct = default)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, $"https://steamcommunity.com/tradeoffer/{offerId}/");
        var loginSecure = $"{steamId}%7C%7C{WebUtility.UrlEncode(accessToken)}";
        req.Headers.Add("Cookie", $"steamLoginSecure={loginSecure}");
        var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // ---- pure, testable ----

    /// <summary>Scrapes the partner header from a trade offer page. Class names are language-stable and
    /// friendship is detected by the friend-indicator icon, so it works regardless of the page language.</summary>
    public static TradePartnerPage ParseTradePartnerPage(string html)
    {
        int level = 0;
        var lm = Regex.Match(html, "friendPlayerLevelNum\"[^>]*>\\s*(\\d+)");
        if (lm.Success) int.TryParse(lm.Groups[1].Value, out level);

        var mm = Regex.Match(html, "trade_partner_member_since[^\"]*\"[^>]*>\\s*([^<]+?)\\s*</div>");
        var memberSince = mm.Success ? WebUtility.HtmlDecode(mm.Groups[1].Value) : "";

        bool isFriend = html.Contains("comment_friendindicator", StringComparison.OrdinalIgnoreCase);
        var fm = Regex.Match(html,
            "comment_friendindicator.*?<div class=\"trade_partner_info_text[^\"]*\">\\s*([^<]+?)\\s*</div>",
            RegexOptions.Singleline);
        var friendFor = fm.Success ? WebUtility.HtmlDecode(fm.Groups[1].Value) : "";

        return new TradePartnerPage(level, memberSince, isFriend, friendFor);
    }

    public static IReadOnlyList<TradeOffer> ParseOffers(string json)
    {
        var list = new List<TradeOffer>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var resp)) return list;
            var descs = ParseDescriptions(resp);
            Add(resp, "trade_offers_received", incoming: true, list, descs);
            Add(resp, "trade_offers_sent", incoming: false, list, descs);
        }
        catch (JsonException) { /* return what we have */ }
        return list;

        static void Add(JsonElement resp, string prop, bool incoming, List<TradeOffer> list,
            Dictionary<string, (string Name, string Icon)> descs)
        {
            if (!resp.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return;
            foreach (var o in arr.EnumerateArray())
            {
                int state = o.TryGetProperty("trade_offer_state", out var s) && s.ValueKind == JsonValueKind.Number
                    ? s.GetInt32() : 0;
                // active_only=true still returns recently-changed offers (accepted/declined) for a window,
                // so drop anything that isn't still Active — otherwise a settled offer lingers with a
                // bogus Cancel/Decline button.
                if (state != TradeOfferState.Active) continue;
                ulong accountId = o.TryGetProperty("accountid_other", out var a) && a.ValueKind == JsonValueKind.Number
                    ? a.GetUInt64() : 0;
                var give = Items(o, "items_to_give", descs);
                var receive = Items(o, "items_to_receive", descs);
                list.Add(new TradeOffer(
                    Id: Str(o, "tradeofferid"),
                    IsIncoming: incoming,
                    PartnerSteamId: 76561197960265728UL + accountId,
                    GiveCount: give.Count,
                    ReceiveCount: receive.Count,
                    State: state,
                    Message: Str(o, "message"),
                    GiveItems: give,
                    ReceiveItems: receive));
            }
        }

        // Descriptions are a shared pool keyed by appid/classid/instanceid; map each to name + icon path.
        static Dictionary<string, (string Name, string Icon)> ParseDescriptions(JsonElement resp)
        {
            var map = new Dictionary<string, (string, string)>();
            if (!resp.TryGetProperty("descriptions", out var arr) || arr.ValueKind != JsonValueKind.Array) return map;
            foreach (var d in arr.EnumerateArray())
            {
                var name = Str(d, "market_name");
                if (name.Length == 0) name = Str(d, "market_hash_name");
                if (name.Length == 0) name = Str(d, "name");
                map[DescKey(d)] = (name, Str(d, "icon_url"));
            }
            return map;
        }

        static List<TradeItem> Items(JsonElement o, string prop, Dictionary<string, (string Name, string Icon)> descs)
        {
            var items = new List<TradeItem>();
            if (!o.TryGetProperty(prop, out var arr) || arr.ValueKind != JsonValueKind.Array) return items;
            foreach (var it in arr.EnumerateArray())
            {
                descs.TryGetValue(DescKey(it), out var d);
                int amount = int.TryParse(Str(it, "amount"), out var am) && am > 0 ? am : 1;
                int appId = it.TryGetProperty("appid", out var ap) && ap.ValueKind == JsonValueKind.Number
                    ? ap.GetInt32() : 0;
                items.Add(new TradeItem(d.Name ?? "", amount, d.Icon ?? "", appId));
            }
            return items;
        }

        // appid can arrive as a number (items) or string (descriptions); normalize to text either way.
        static string DescKey(JsonElement e)
        {
            var appid = e.TryGetProperty("appid", out var ap)
                ? (ap.ValueKind == JsonValueKind.Number ? ap.GetInt64().ToString() : ap.GetString() ?? "")
                : "";
            return $"{appid}_{Str(e, "classid")}_{Str(e, "instanceid")}";
        }
    }

    public static OfferActionResult ParseActionResult(string body, bool httpOk)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            if (root.TryGetProperty("strError", out var err))
                return new OfferActionResult(false, false, err.GetString());
            bool needs = root.TryGetProperty("needs_mobile_confirmation", out var nc) && nc.ValueKind == JsonValueKind.True;
            // accept returns tradeid; decline returns {} — both are success when there's no strError.
            return new OfferActionResult(true, needs, null);
        }
        catch (JsonException)
        {
            return httpOk
                ? new OfferActionResult(true, false, null)
                : new OfferActionResult(false, false, "Session invalid — sign in again.");
        }
    }

    /// <summary>Pulls a game's name from a Steam store <c>appdetails?filters=basic</c> response, keyed by
    /// appid. Returns empty if the app is missing or the call didn't succeed.</summary>
    public static string ParseAppName(string json, int appId)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(appId.ToString(), out var e)
                && e.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True
                && e.TryGetProperty("data", out var d)
                && d.TryGetProperty("name", out var n))
                return n.GetString() ?? "";
        }
        catch (JsonException) { /* fall through */ }
        return "";
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
}
