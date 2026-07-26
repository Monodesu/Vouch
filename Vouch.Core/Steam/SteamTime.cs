using System.Text.Json;

namespace Vouch.Core.Steam;

/// <summary>
/// Steam server time — codes and confirmation signatures must be computed against Steam's
/// clock, not the local one. One <c>ITwoFactorService/QueryTime</c> call (same as SteamAuth's
/// TimeAligner) captures the offset; afterwards <see cref="Now"/> is local UTC + offset.
/// If the query fails the offset stays 0 (local clock), which Steam tolerates when close.
/// </summary>
public static class SteamTime
{
    private const string QueryTimeUrl = "https://api.steampowered.com/ITwoFactorService/QueryTime/v1/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(10) };
    private static int _offsetSeconds;
    private static bool _aligned;

    public static long Now() => UtcNow.ToUnixTimeSeconds();
    public static DateTimeOffset UtcNow => DateTimeOffset.UtcNow.AddSeconds(_offsetSeconds);

    /// <summary>Queries Steam's clock once; later calls are no-ops. Failures are swallowed.</summary>
    public static async Task EnsureAlignedAsync(CancellationToken ct = default)
    {
        if (_aligned) return;
        try
        {
            using var content = new FormUrlEncodedContent(new Dictionary<string, string> { ["steamid"] = "0" });
            var resp = await Http.PostAsync(QueryTimeUrl, content, ct);
            if (!resp.IsSuccessStatusCode) return;
            if (ParseServerTime(await resp.Content.ReadAsStringAsync(ct)) is not { } serverTime) return;
            _offsetSeconds = (int)(serverTime - DateTimeOffset.UtcNow.ToUnixTimeSeconds());
            _aligned = true;
        }
        catch (HttpRequestException) { }
        catch (TaskCanceledException) { }
    }

    internal static long? ParseServerTime(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var r)) return null;
            if (!r.TryGetProperty("server_time", out var t)) return null;
            return t.ValueKind == JsonValueKind.Number ? t.GetInt64()
                 : long.TryParse(t.GetString(), out var n) ? n : null;
        }
        catch (JsonException) { return null; }
    }
}
