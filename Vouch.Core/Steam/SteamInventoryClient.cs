using System.Net;
using System.Text.Json;

namespace Vouch.Core.Steam;

/// <summary>One inventory item (as needed to build a trade offer).</summary>
public record InventoryAsset(int AppId, string ContextId, string AssetId, int Amount);

/// <summary>A tradable inventory item with its display name and icon path (for the item-picker mode).</summary>
public record InventoryItem(int AppId, string ContextId, string AssetId, int Amount, string Name, string IconUrl = "");

/// <summary>A game the account actually holds inventory in (name + item count), for the inventory viewer —
/// mirrors Steam's own "only games with items" dropdown.</summary>
public record InventoryApp(int AppId, string ContextId, string Name, int AssetCount);

/// <summary>
/// Reads a Steam account's inventory for one game/context and returns the currently-tradable items.
/// Uses the account's session cookie so the owner's private inventory is visible too. Parsing is
/// pure/testable; a single page (up to 5000 items) is fetched.
/// </summary>
public class SteamInventoryClient
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return http;
    }

    private const int PageSize = 1000; // Steam rejects large counts (5000 -> HTTP 400); 1000 is the safe page cap.

    /// <summary>Tradable assets only (assetid/amount) — for the "transfer everything" flow.</summary>
    public async Task<IReadOnlyList<InventoryAsset>> FetchTradableAsync(
        ulong steamId, int appId, string contextId, string accessToken, CancellationToken ct = default)
    {
        var all = new List<InventoryAsset>();
        foreach (var body in await FetchPagesAsync(steamId, appId, contextId, accessToken, ct))
            all.AddRange(ParseTradable(body, appId, contextId));
        return all;
    }

    /// <summary>Lists the games the account holds inventory in (name + count), read from the inventory
    /// page's <c>g_rgAppContextData</c> — the same source Steam's own inventory dropdown uses, so only
    /// games that actually have items appear. Ordered by item count (desc). Empty if none / unreadable.</summary>
    public async Task<IReadOnlyList<InventoryApp>> FetchInventoryAppsAsync(
        ulong steamId, string accessToken, CancellationToken ct = default)
    {
        var url = $"https://steamcommunity.com/profiles/{steamId}/inventory/";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var loginSecure = $"{steamId}%7C%7C{WebUtility.UrlEncode(accessToken)}";
        req.Headers.Add("Cookie", $"steamLoginSecure={loginSecure}; steamid={steamId}");

        var resp = await Http.SendAsync(req, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.StatusCode}");
        return ParseInventoryApps(await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Parses the <c>g_rgAppContextData</c> object out of the inventory page HTML. Each app keeps
    /// the single context with the most items (Steam splits some games into contexts, but inventories are
    /// almost always one). Only apps with &gt; 0 items are returned. Pure/testable.</summary>
    public static IReadOnlyList<InventoryApp> ParseInventoryApps(string html)
    {
        var json = ExtractJsObject(html, "g_rgAppContextData");
        if (json is null) return Array.Empty<InventoryApp>();

        var result = new List<InventoryApp>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            foreach (var app in doc.RootElement.EnumerateObject())
            {
                var v = app.Value;
                int appId = Int(v, "appid");
                var name = v.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "";
                if (appId <= 0 || !v.TryGetProperty("rgContexts", out var ctxs) || ctxs.ValueKind != JsonValueKind.Object)
                    continue;

                string bestCtx = ""; int bestCount = 0;
                foreach (var c in ctxs.EnumerateObject())
                {
                    int count = Int(c.Value, "asset_count");
                    if (count > bestCount)
                    {
                        bestCount = count;
                        bestCtx = c.Value.TryGetProperty("id", out var idv) ? idv.GetString() ?? c.Name : c.Name;
                    }
                }
                if (bestCount > 0)
                    result.Add(new InventoryApp(appId, bestCtx.Length > 0 ? bestCtx : "2",
                                                name.Length > 0 ? name : $"App {appId}", bestCount));
            }
        }
        catch (JsonException) { return Array.Empty<InventoryApp>(); }

        result.Sort((a, b) => b.AssetCount.CompareTo(a.AssetCount));
        return result;
    }

    /// <summary>Extracts the first balanced <c>{...}</c> object assigned to <paramref name="varName"/> in a
    /// page's inline script (brace-aware, string-aware), or null if not found.</summary>
    private static string? ExtractJsObject(string html, string varName)
    {
        int at = html.IndexOf(varName, StringComparison.Ordinal);
        if (at < 0) return null;
        int start = html.IndexOf('{', at);
        if (start < 0) return null;

        int depth = 0; bool inStr = false; char prev = '\0';
        for (int p = start; p < html.Length; p++)
        {
            char ch = html[p];
            if (inStr) { if (ch == '"' && prev != '\\') inStr = false; }
            else if (ch == '"') inStr = true;
            else if (ch == '{') depth++;
            else if (ch == '}' && --depth == 0) return html.Substring(start, p - start + 1);
            prev = ch;
        }
        return null;
    }

    private static int Int(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.Number ? v.GetInt32() : int.TryParse(v.GetString(), out var n) ? n : 0)
            : 0;

    /// <summary>Tradable items with names — for the item-picker flow.</summary>
    public async Task<IReadOnlyList<InventoryItem>> FetchTradableItemsAsync(
        ulong steamId, int appId, string contextId, string accessToken, CancellationToken ct = default)
    {
        var all = new List<InventoryItem>();
        foreach (var body in await FetchPagesAsync(steamId, appId, contextId, accessToken, ct))
            all.AddRange(ParseTradableItems(body, appId, contextId));
        return all;
    }

    /// <summary>All items with names — for the read-only inventory viewer (tradable or not).</summary>
    public async Task<IReadOnlyList<InventoryItem>> FetchItemsAsync(
        ulong steamId, int appId, string contextId, string accessToken, CancellationToken ct = default)
    {
        var all = new List<InventoryItem>();
        foreach (var body in await FetchPagesAsync(steamId, appId, contextId, accessToken, ct))
            all.AddRange(ParseItems(body, appId, contextId));
        return all;
    }

    /// <summary>Like <see cref="ParseTradableItems"/> but keeps every item (no tradable filter).</summary>
    public static IReadOnlyList<InventoryItem> ParseItems(string json, int appId, string contextId)
    {
        var result = new List<InventoryItem>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return result; }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return result;
            if (!root.TryGetProperty("descriptions", out var descs) || descs.ValueKind != JsonValueKind.Array)
                return result;

            var desc = new Dictionary<string, (string Name, string Icon)>();
            foreach (var d in descs.EnumerateArray())
            {
                var name = Str(d, "market_name");
                if (string.IsNullOrEmpty(name)) name = Str(d, "name");
                desc[Str(d, "classid") + "_" + Str(d, "instanceid")] =
                    (string.IsNullOrEmpty(name) ? "(item)" : name, Str(d, "icon_url"));
            }

            foreach (var a in assets.EnumerateArray())
            {
                var key = Str(a, "classid") + "_" + Str(a, "instanceid");
                if (!desc.TryGetValue(key, out var d)) continue;
                int amount = int.TryParse(Str(a, "amount"), out var n) ? n : 1;
                result.Add(new InventoryItem(appId, contextId, Str(a, "assetid"), Math.Max(1, amount), d.Name, d.Icon));
            }
        }
        return result;
    }

    /// <summary>Fetches every inventory page (count=1000, paged via more_items/last_assetid). Throws on
    /// auth/private failures so the caller can report them instead of showing an empty inventory.</summary>
    private async Task<List<string>> FetchPagesAsync(
        ulong steamId, int appId, string contextId, string accessToken, CancellationToken ct)
    {
        var pages = new List<string>();
        string? startAssetId = null;

        for (int page = 0; page < 30; page++) // cap ~30k items so a huge inventory can't loop forever
        {
            var url = $"https://steamcommunity.com/inventory/{steamId}/{appId}/{contextId}?l=english&count={PageSize}"
                      + (startAssetId is null ? "" : $"&start_assetid={startAssetId}");
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            var loginSecure = $"{steamId}%7C%7C{WebUtility.UrlEncode(accessToken)}";
            req.Headers.Add("Cookie", $"steamLoginSecure={loginSecure}; steamid={steamId}");

            var resp = await Http.SendAsync(req, ct);
            if (!resp.IsSuccessStatusCode)
                throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.StatusCode}");

            var body = await resp.Content.ReadAsStringAsync(ct);
            var trimmed = body.TrimStart();
            if (trimmed.StartsWith("null") || trimmed.Contains("\"success\":0"))
                throw new HttpRequestException("inventory not accessible (private profile or session expired)");

            pages.Add(body);
            var (more, last) = ParsePageCursor(body);
            if (!more || last is null) break;
            startAssetId = last;
        }
        return pages;
    }

    /// <summary>Pagination cursor: whether more items follow and the assetid to resume from.</summary>
    private static (bool More, string? Last) ParsePageCursor(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            bool more = root.TryGetProperty("more_items", out var m)
                        && m.ValueKind == JsonValueKind.Number && m.GetInt32() == 1;
            string? last = root.TryGetProperty("last_assetid", out var l) ? l.GetString() : null;
            return (more, last);
        }
        catch (JsonException) { return (false, null); }
    }

    /// <summary>Returns the assets whose description is <c>tradable = 1</c>. Empty on error / empty inventory.</summary>
    public static IReadOnlyList<InventoryAsset> ParseTradable(string json, int appId, string contextId)
    {
        var result = new List<InventoryAsset>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return result; }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return result;
            if (!root.TryGetProperty("descriptions", out var descs) || descs.ValueKind != JsonValueKind.Array)
                return result;

            // classid_instanceid -> tradable
            var tradable = new HashSet<string>();
            foreach (var d in descs.EnumerateArray())
            {
                if (IsTradable(d))
                    tradable.Add(Str(d, "classid") + "_" + Str(d, "instanceid"));
            }

            foreach (var a in assets.EnumerateArray())
            {
                var key = Str(a, "classid") + "_" + Str(a, "instanceid");
                if (!tradable.Contains(key)) continue;
                int amount = int.TryParse(Str(a, "amount"), out var n) ? n : 1;
                result.Add(new InventoryAsset(appId, contextId, Str(a, "assetid"), Math.Max(1, amount)));
            }
        }
        return result;
    }

    /// <summary>Like <see cref="ParseTradable"/> but keeps each item's display name (market_name / name).</summary>
    public static IReadOnlyList<InventoryItem> ParseTradableItems(string json, int appId, string contextId)
    {
        var result = new List<InventoryItem>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return result; }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("assets", out var assets) || assets.ValueKind != JsonValueKind.Array)
                return result;
            if (!root.TryGetProperty("descriptions", out var descs) || descs.ValueKind != JsonValueKind.Array)
                return result;

            // classid_instanceid -> (name, icon) for tradable descriptions
            var tradableDesc = new Dictionary<string, (string Name, string Icon)>();
            foreach (var d in descs.EnumerateArray())
            {
                if (!IsTradable(d)) continue;
                var name = Str(d, "market_name");
                if (string.IsNullOrEmpty(name)) name = Str(d, "name");
                tradableDesc[Str(d, "classid") + "_" + Str(d, "instanceid")] =
                    (string.IsNullOrEmpty(name) ? "(item)" : name, Str(d, "icon_url"));
            }

            foreach (var a in assets.EnumerateArray())
            {
                var key = Str(a, "classid") + "_" + Str(a, "instanceid");
                if (!tradableDesc.TryGetValue(key, out var d)) continue;
                int amount = int.TryParse(Str(a, "amount"), out var n) ? n : 1;
                result.Add(new InventoryItem(appId, contextId, Str(a, "assetid"), Math.Max(1, amount), d.Name, d.Icon));
            }
        }
        return result;
    }

    // "tradable" comes as 1/0 (number) in Steam's inventory JSON.
    private static bool IsTradable(JsonElement d) =>
        d.TryGetProperty("tradable", out var t) && t.ValueKind == JsonValueKind.Number && t.GetInt32() == 1;

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v)
            ? (v.ValueKind == JsonValueKind.Number ? v.GetRawText() : v.GetString() ?? "")
            : "";
}
