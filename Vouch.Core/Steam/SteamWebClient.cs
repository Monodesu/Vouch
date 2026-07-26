using System.Text.Json;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace Vouch.Core.Steam;

/// <summary>Public Steam profile info from the community XML endpoint (no API key needed).</summary>
public record SteamProfile(
    string PersonaName,
    string? AvatarUrl,
    bool VacBanned,
    bool TradeBanned,
    bool LimitedAccount);

/// <summary>Exact ban counts from ISteamUser/GetPlayerBans (needs a Web API key).</summary>
public record PlayerBans(
    int VacBans,
    int GameBans,
    bool CommunityBanned,
    string EconomyBan);

/// <summary>Ban status scraped from the public profile page — the no-key source for GAME bans,
/// which the community XML omits (it only exposes VAC and trade bans).</summary>
public record ProfileBanStatus(int VacBans, int GameBans, bool TradeBanned);

/// <summary>Fetches Steam profile / avatar / ban info over HTTP.</summary>
public class SteamWebClient
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Vouch/1.0 (+https://github.com)");
        return http;
    }

    public async Task<SteamProfile?> FetchProfileAsync(ulong steamId, CancellationToken ct = default)
    {
        var xml = await Http.GetStringAsync(
            $"https://steamcommunity.com/profiles/{steamId}/?xml=1", ct);
        return ParseProfileXml(xml);
    }

    public async Task<byte[]?> DownloadAsync(string url, CancellationToken ct = default)
    {
        try { return await Http.GetByteArrayAsync(url, ct); }
        catch { return null; }
    }

    public async Task<PlayerBans?> FetchBansAsync(ulong steamId, string apiKey, CancellationToken ct = default)
    {
        var json = await Http.GetStringAsync(
            $"https://api.steampowered.com/ISteamUser/GetPlayerBans/v1/?key={apiKey}&steamids={steamId}", ct);
        return ParseBansJson(json);
    }

    /// <summary>Scrapes the public profile page for ban status (game bans included). <c>?l=english</c>
    /// forces the ban text to a stable language regardless of the account's locale. Returns null on
    /// network/parse failure; an all-zero result means "no ban banner", i.e. clean.</summary>
    public async Task<ProfileBanStatus?> FetchBanStatusAsync(ulong steamId, CancellationToken ct = default)
    {
        try
        {
            var html = await Http.GetStringAsync(
                $"https://steamcommunity.com/profiles/{steamId}/?l=english", ct);
            return ParseBanStatusHtml(html);
        }
        catch { return null; }
    }

    // ---- parsing (pure, testable) ----

    public static SteamProfile? ParseProfileXml(string xml)
    {
        XDocument doc;
        try { doc = XDocument.Parse(xml); }
        catch (System.Xml.XmlException) { return null; }

        var root = doc.Root;
        if (root is null || root.Name.LocalName != "profile") return null; // private/error -> <response>

        string persona = (string?)root.Element("steamID") ?? "";
        string? avatar = (string?)root.Element("avatarFull");
        bool vac = (string?)root.Element("vacBanned") == "1";
        bool trade = ((string?)root.Element("tradeBanState") ?? "None") != "None";
        bool limited = (string?)root.Element("isLimitedAccount") == "1";

        return new SteamProfile(persona, avatar, vac, trade, limited);
    }

    /// <summary>
    /// Parses the profile page's <c>profile_ban_status</c> banner (English), e.g.
    /// "1 game ban on record | Info  121 day(s) since last ban" or "Multiple VAC bans on record".
    /// No banner → all zero (clean).
    /// </summary>
    public static ProfileBanStatus ParseBanStatusHtml(string html)
    {
        int idx = html.IndexOf("profile_ban_status", StringComparison.OrdinalIgnoreCase);
        if (idx < 0) return new ProfileBanStatus(0, 0, false); // no ban banner => clean

        // Scope matching to the banner so unrelated page text can't trip it.
        var block = html.Substring(idx, Math.Min(1500, html.Length - idx));
        return new ProfileBanStatus(
            VacBans: BanCount(block, "VAC ban"),
            GameBans: BanCount(block, "game ban"),
            TradeBanned: Regex.IsMatch(block, "trade ban", RegexOptions.IgnoreCase));
    }

    // "N <phrase>s on record" -> N; a banner mentioning the ban with no count (e.g. "Multiple VAC
    // bans") -> 1; phrase absent -> 0.
    private static int BanCount(string block, string phrase)
    {
        var m = Regex.Match(block, @"(\d+)\s+" + phrase, RegexOptions.IgnoreCase);
        if (m.Success) return int.Parse(m.Groups[1].Value);
        return Regex.IsMatch(block, phrase, RegexOptions.IgnoreCase) ? 1 : 0;
    }

    public static PlayerBans? ParseBansJson(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("players", out var players) || players.GetArrayLength() == 0)
                return null;

            var p = players[0];
            return new PlayerBans(
                VacBans: p.TryGetProperty("NumberOfVACBans", out var v) ? v.GetInt32() : 0,
                GameBans: p.TryGetProperty("NumberOfGameBans", out var g) ? g.GetInt32() : 0,
                CommunityBanned: p.TryGetProperty("CommunityBanned", out var c) && c.GetBoolean(),
                EconomyBan: p.TryGetProperty("EconomyBan", out var e) ? e.GetString() ?? "none" : "none");
        }
        catch (JsonException) { return null; }
    }
}
