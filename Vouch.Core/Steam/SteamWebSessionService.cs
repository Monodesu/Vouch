using System.Text.Json;
using System.Text.RegularExpressions;

namespace Vouch.Core.Steam;

/// <summary>
/// Mints a browser-usable Steam web session from a refresh token by replaying the website's login
/// handshake: <c>login.steampowered.com/jwt/finalizelogin</c> → the per-domain <c>/login/settoken</c>
/// endpoints, reading back the <c>steamLoginSecure</c> cookie. Handing that cookie to a browser (see the
/// app's CdpBrowser) opens an already-signed-in Steam window — used for device management, which Steam
/// only exposes on the web.
/// </summary>
public class SteamWebSessionService
{
    private static readonly HttpClient Http = new(
        new HttpClientHandler { UseCookies = false, AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>Returns the <c>store.steampowered.com</c> <c>steamLoginSecure</c> cookie value
    /// (<c>steamid||token</c>, URL-decoded) for the account, or null if the handshake failed (e.g. the
    /// refresh token expired).</summary>
    public async Task<string?> GetStoreLoginCookieAsync(
        ulong steamId, string refreshToken, string sessionId, CancellationToken ct = default)
    {
        var body = await PostFormAsync("https://login.steampowered.com/jwt/finalizelogin", new()
        {
            ["nonce"] = refreshToken,
            ["sessionid"] = sessionId,
            ["redir"] = "https://store.steampowered.com/account/authorizeddevices",
        }, ct);
        if (body is null) return null;

        JsonDocument doc;
        try { doc = JsonDocument.Parse(body); } catch (JsonException) { return null; }
        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("transfer_info", out var ti) || ti.ValueKind != JsonValueKind.Array)
                return null;
            var steamIdStr = root.TryGetProperty("steamID", out var se) ? se.GetString() : null;
            steamIdStr ??= steamId.ToString();

            foreach (var t in ti.EnumerateArray())
            {
                var url = t.TryGetProperty("url", out var u) ? u.GetString() ?? "" : "";
                if (!url.Contains("store.steampowered.com") || !t.TryGetProperty("params", out var p))
                    continue;

                var form = new Dictionary<string, string>
                {
                    ["nonce"] = p.TryGetProperty("nonce", out var n) ? n.GetString() ?? "" : "",
                    ["auth"] = p.TryGetProperty("auth", out var a) ? a.GetString() ?? "" : "",
                    ["steamID"] = steamIdStr,
                };
                if (await SetTokenAsync(url, form, ct) is { } cookie) return cookie;
            }
        }
        return null;
    }

    /// <summary>POSTs one settoken endpoint and returns the steamLoginSecure value from its Set-Cookie.</summary>
    private static async Task<string?> SetTokenAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await Http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        if (!resp.Headers.TryGetValues("Set-Cookie", out var cookies)) return null;
        foreach (var c in cookies)
        {
            var m = Regex.Match(c, @"^steamLoginSecure=([^;]+)");
            if (m.Success) return Uri.UnescapeDataString(m.Groups[1].Value);
        }
        return null;
    }

    private static async Task<string?> PostFormAsync(string url, Dictionary<string, string> form, CancellationToken ct)
    {
        using var resp = await Http.PostAsync(url, new FormUrlEncodedContent(form), ct);
        return resp.IsSuccessStatusCode ? await resp.Content.ReadAsStringAsync(ct) : null;
    }
}
