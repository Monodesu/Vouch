using System.Text.Json;

namespace Vouch.Core.Steam;

/// <summary>
/// Keeps a web session alive: Steam access tokens (JWTs) expire in ~24h, refresh tokens in
/// ~200 days. This renews an access token from the refresh token via
/// <c>IAuthenticationService/GenerateAccessTokenForApp</c> (plain HTTP, no SteamKit2), so
/// confirmations keep working without a manual re-login.
/// </summary>
public class SteamSessionService
{
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    /// <summary>
    /// Renews the access token. With <paramref name="allowRenewal"/> Steam may also issue a
    /// fresh refresh token (extending the ~200-day life); the result carries whichever applies.
    /// Returns null on failure (e.g. refresh token expired → re-login needed).
    /// </summary>
    public async Task<SteamLoginResult?> RenewAsync(
        ulong steamId, string refreshToken, bool allowRenewal = false, CancellationToken ct = default)
    {
        var form = new Dictionary<string, string>
        {
            ["refresh_token"] = refreshToken,
            ["steamid"] = steamId.ToString(),
            ["renewal_type"] = allowRenewal ? "1" : "0",
        };
        using var content = new FormUrlEncodedContent(form);
        var resp = await Http.PostAsync(
            "https://api.steampowered.com/IAuthenticationService/GenerateAccessTokenForApp/v1/", content, ct);
        if (!resp.IsSuccessStatusCode) return null;

        var json = await resp.Content.ReadAsStringAsync(ct);
        return ParseRenewResponse(json, steamId, refreshToken);
    }

    internal static SteamLoginResult? ParseRenewResponse(string json, ulong steamId, string oldRefreshToken)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var r)) return null;
            var access = r.TryGetProperty("access_token", out var a) ? a.GetString() : null;
            if (string.IsNullOrEmpty(access)) return null;
            // Steam usually leaves the refresh token unchanged (returns empty); keep the old one.
            var newRefresh = r.TryGetProperty("refresh_token", out var rt) ? rt.GetString() : null;
            return new SteamLoginResult(steamId, access, string.IsNullOrEmpty(newRefresh) ? oldRefreshToken : newRefresh);
        }
        catch (JsonException) { return null; }
    }

    /// <summary>Reads the <c>exp</c> claim (unix seconds) from a JWT, or null if unreadable.</summary>
    public static long? GetJwtExpiry(string jwt)
    {
        var parts = jwt.Split('.');
        if (parts.Length < 2) return null;
        try
        {
            using var doc = JsonDocument.Parse(Base64UrlDecode(parts[1]));
            return doc.RootElement.TryGetProperty("exp", out var e) && e.ValueKind == JsonValueKind.Number
                ? e.GetInt64() : null;
        }
        catch { return null; }
    }

    /// <summary>True if the access token is expired or expires within <paramref name="buffer"/>.</summary>
    public static bool IsExpired(string? jwt, TimeSpan buffer)
    {
        if (string.IsNullOrEmpty(jwt)) return true;
        var exp = GetJwtExpiry(jwt);
        if (exp is null) return true; // unknown → force a renew
        return DateTimeOffset.UtcNow.ToUnixTimeSeconds() >= exp.Value - (long)buffer.TotalSeconds;
    }

    private static byte[] Base64UrlDecode(string s)
    {
        s = s.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4) { case 2: s += "=="; break; case 3: s += "="; break; }
        return Convert.FromBase64String(s);
    }
}
