using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Vouch.Core.Steam;

/// <summary>What's needed to talk to Steam's mobile confirmation endpoints for one account.</summary>
public record ConfirmationCredentials(ulong SteamId, string IdentitySecret, string DeviceId, string AccessToken);

/// <summary>A single pending mobile confirmation (trade, market listing, etc.).</summary>
public record SteamConfirmation(
    string Id,
    string Nonce,
    int Type,
    string TypeName,
    string Headline,
    IReadOnlyList<string> Summary,
    long CreationTime,
    string? Icon,
    string CreatorId);

/// <summary>
/// Fetches and answers Steam mobile trade/market confirmations over HTTP — a native port of
/// SteamAuth's mobileconf flow. Requests are signed with the account's identity_secret; the
/// web session is the login access token used as the <c>steamLoginSecure</c> cookie.
/// </summary>
public class SteamConfirmationService
{
    private const string Base = "https://steamcommunity.com/mobileconf/";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd(
            "Mozilla/5.0 (Linux; Android 9; Mobile) SteamMobile/3.6.4");
        return http;
    }

    public async Task<IReadOnlyList<SteamConfirmation>> FetchAsync(
        ConfirmationCredentials c, CancellationToken ct = default)
    {
        await SteamTime.EnsureAlignedAsync(ct);
        long time = SteamTime.Now();
        var url = Base + "getlist?" + QueryParams(c, "conf", time);
        var json = await SendAsync(url, c, ct);
        return ParseConfirmations(json);
    }

    /// <summary>Accept (allow) or deny (cancel) a single confirmation. Returns true on success.</summary>
    public async Task<bool> RespondAsync(
        ConfirmationCredentials c, SteamConfirmation conf, bool accept, CancellationToken ct = default)
    {
        await SteamTime.EnsureAlignedAsync(ct);
        // The hash tag differs from the op these days: allow/accept, cancel/reject.
        var op = accept ? "allow" : "cancel";
        var tag = accept ? "accept" : "reject";
        long time = SteamTime.Now();
        var url = Base + "ajaxop?op=" + op + "&" + QueryParams(c, tag, time)
                  + "&cid=" + conf.Id + "&ck=" + conf.Nonce;
        var json = await SendAsync(url, c, ct);
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.TryGetProperty("success", out var s) && s.GetBoolean();
        }
        catch (JsonException) { return false; }
    }

    private static readonly string SessionId =
        Convert.ToHexString(RandomNumberGenerator.GetBytes(16)).ToLowerInvariant();

    private static async Task<string> SendAsync(string url, ConfirmationCredentials c, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        var loginSecure = $"{c.SteamId}%7C%7C{WebUtility.UrlEncode(c.AccessToken)}";
        req.Headers.Add("Cookie", string.Join("; ",
            "mobileClientVersion=777777 3.6.4",
            "mobileClient=android",
            $"sessionid={SessionId}",
            $"steamid={c.SteamId}",
            $"steamLoginSecure={loginSecure}",
            "Steam_Language=english",
            "dob="));
        var resp = await Http.SendAsync(req, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    // ---- signing (pure, testable) ----

    internal static string QueryParams(ConfirmationCredentials c, string tag, long time) =>
        "p=" + WebUtility.UrlEncode(c.DeviceId) +
        "&a=" + c.SteamId +
        "&k=" + WebUtility.UrlEncode(GenerateConfirmationHash(time, tag, c.IdentitySecret)) +
        "&t=" + time +
        "&m=react" +
        "&tag=" + tag;

    /// <summary>HMAC-SHA1 over (8-byte big-endian time + tag), keyed by the identity secret.</summary>
    public static string GenerateConfirmationHash(long time, string tag, string identitySecretBase64)
    {
        int tagLen = Math.Min(tag.Length, 32);
        byte[] buffer = new byte[8 + tagLen];
        long n = time;
        for (int i = 7; i >= 0; i--) { buffer[i] = (byte)(n & 0xFF); n >>= 8; }
        Encoding.UTF8.GetBytes(tag, 0, tagLen, buffer, 8);

        byte[] key = Convert.FromBase64String(identitySecretBase64);
        byte[] hash = HMACSHA1.HashData(key, buffer);
        return Convert.ToBase64String(hash);
    }

    // ---- parsing (pure, testable) ----

    public static IReadOnlyList<SteamConfirmation> ParseConfirmations(string json)
    {
        var list = new List<SteamConfirmation>();
        JsonDocument doc;
        try { doc = JsonDocument.Parse(json); }
        catch (JsonException) { return list; }

        using (doc)
        {
            var root = doc.RootElement;
            if (!root.TryGetProperty("success", out var ok) || !ok.GetBoolean()) return list;
            if (!root.TryGetProperty("conf", out var conf) || conf.ValueKind != JsonValueKind.Array) return list;

            foreach (var e in conf.EnumerateArray())
            {
                var summary = new List<string>();
                if (e.TryGetProperty("summary", out var sm) && sm.ValueKind == JsonValueKind.Array)
                    foreach (var s in sm.EnumerateArray())
                        if (s.GetString() is { } str) summary.Add(str);

                list.Add(new SteamConfirmation(
                    Id: Str(e, "id"),
                    Nonce: Str(e, "nonce"),
                    Type: e.TryGetProperty("type", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0,
                    TypeName: Str(e, "type_name"),
                    Headline: Str(e, "headline"),
                    Summary: summary,
                    CreationTime: e.TryGetProperty("creation_time", out var ctm) && ctm.ValueKind == JsonValueKind.Number ? ctm.GetInt64() : 0,
                    Icon: e.TryGetProperty("icon", out var ic) ? ic.GetString() : null,
                    CreatorId: Str(e, "creator_id")));
            }
        }
        return list;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
}
