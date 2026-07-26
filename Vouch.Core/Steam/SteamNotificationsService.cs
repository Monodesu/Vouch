using System.Net;
using System.Text.Json;

namespace Vouch.Core.Steam;

/// <summary>One Steam notification (comment, trade offer, …). <paramref name="Kind"/> and
/// <paramref name="Url"/> are derived from the type-specific body_data so the UI can label it and open
/// the right page; <paramref name="SenderId"/> is the SteamID64 of the other party (0 if none).</summary>
public record SteamNotification(
    string Id, int Type, string TypeName, bool Read, long Timestamp,
    string Kind, ulong SenderId, string? Url);

/// <summary>
/// Reads an account's Steam notifications via the access token (ISteamNotificationService — no Web API
/// key). The body payload varies per type, so this surfaces the type + read state + time; parsing is
/// pure/testable and the fetch throws on failure so the caller can prompt re-login.
/// </summary>
public class SteamNotificationsService
{
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64)");
        return http;
    }

    public async Task<IReadOnlyList<SteamNotification>> FetchAsync(string accessToken, CancellationToken ct = default)
    {
        var url = "https://api.steampowered.com/ISteamNotificationService/GetSteamNotifications/v1/"
                  + $"?access_token={WebUtility.UrlEncode(accessToken)}"
                  + "&include_hidden=false&language=english&include_read=true&count_only=false";
        var resp = await Http.GetAsync(url, ct);
        if (!resp.IsSuccessStatusCode)
            throw new HttpRequestException($"HTTP {(int)resp.StatusCode} {resp.StatusCode}");
        return ParseNotifications(await resp.Content.ReadAsStringAsync(ct));
    }

    /// <summary>Marks the given notifications read. No-op (success) when the list is empty.</summary>
    public Task<bool> MarkReadAsync(string accessToken, IReadOnlyList<string> notificationIds, CancellationToken ct = default)
    {
        if (notificationIds.Count == 0) return Task.FromResult(true);
        var form = new List<KeyValuePair<string, string>>();
        for (int i = 0; i < notificationIds.Count; i++)
            form.Add(new($"notification_ids[{i}]", notificationIds[i]));
        return PostMarkReadAsync(accessToken, form, ct);
    }

    /// <summary>Marks every notification up to <paramref name="readTimestamp"/> (unix seconds) read.</summary>
    public Task<bool> MarkAllReadAsync(string accessToken, long readTimestamp, CancellationToken ct = default)
    {
        var form = new List<KeyValuePair<string, string>>
        {
            new("mark_all_read", "true"),
            new("mark_all_read_timestamp", readTimestamp.ToString()),
        };
        return PostMarkReadAsync(accessToken, form, ct);
    }

    private static async Task<bool> PostMarkReadAsync(
        string accessToken, List<KeyValuePair<string, string>> form, CancellationToken ct)
    {
        var url = "https://api.steampowered.com/ISteamNotificationService/MarkNotificationsRead/v1/"
                  + $"?access_token={WebUtility.UrlEncode(accessToken)}";
        using var content = new FormUrlEncodedContent(form);
        var resp = await Http.PostAsync(url, content, ct);
        return resp.IsSuccessStatusCode;
    }

    // ---- pure, testable ----

    public static IReadOnlyList<SteamNotification> ParseNotifications(string json)
    {
        var list = new List<SteamNotification>();
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("response", out var resp)) return list;
            if (!resp.TryGetProperty("notifications", out var arr) || arr.ValueKind != JsonValueKind.Array) return list;

            foreach (var n in arr.EnumerateArray())
            {
                int type = n.TryGetProperty("notification_type", out var t) && t.ValueKind == JsonValueKind.Number ? t.GetInt32() : 0;
                var (kind, sender, url) = ParseBody(n.TryGetProperty("body_data", out var bd) ? bd.GetString() : null);
                list.Add(new SteamNotification(
                    Id: Str(n, "notification_id"),
                    Type: type,
                    TypeName: TypeName(type),
                    Read: n.TryGetProperty("read", out var r) && r.ValueKind == JsonValueKind.True,
                    Timestamp: n.TryGetProperty("timestamp", out var ts) && ts.ValueKind == JsonValueKind.Number ? ts.GetInt64() : 0,
                    Kind: kind, SenderId: sender, Url: url));
            }
        }
        catch (JsonException) { /* return what we have */ }
        return list;
    }

    private const ulong SteamId64Base = 76561197960265728UL;

    /// <summary>Derives (kind, senderSteamId64, targetUrl) from a notification's body_data JSON — a
    /// trade offer carries tradeoffer_id + sender; a comment carries requestor_id.</summary>
    public static (string Kind, ulong Sender, string? Url) ParseBody(string? bodyJson)
    {
        if (string.IsNullOrEmpty(bodyJson)) return ("other", 0, null);
        try
        {
            using var doc = JsonDocument.Parse(bodyJson);
            var b = doc.RootElement;
            if (b.TryGetProperty("tradeoffer_id", out var to))
            {
                var id = to.ValueKind == JsonValueKind.String ? to.GetString() : to.GetRawText();
                ulong sender = b.TryGetProperty("sender", out var s) && s.TryGetInt64(out var sv) ? (ulong)sv + SteamId64Base : 0;
                return ("tradeoffer", sender,
                    string.IsNullOrEmpty(id) ? null : $"https://steamcommunity.com/tradeoffer/{id}/");
            }
            if (b.TryGetProperty("requestor_id", out var rq) && rq.TryGetInt64(out var rv))
            {
                ulong sender = (ulong)rv + SteamId64Base;
                return ("comment", sender, $"https://steamcommunity.com/profiles/{sender}");
            }
        }
        catch (JsonException) { /* fall through */ }
        return ("other", 0, null);
    }

    /// <summary>Friendly label for the known SteamNotificationType values; unknowns fall back to the number.</summary>
    public static string TypeName(int type) => type switch
    {
        1 => "Trade offer",
        2 => "Game achievement",
        3 => "Friend invite",
        4 => "Item received",
        5 => "Comment",
        8 => "Gift received",
        9 => "Wishlist item on sale",
        10 => "Trade offer",
        11 => "Trade offer",
        12 => "Game turn",
        _ => $"Notification (type {type})",
    };

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";
}
