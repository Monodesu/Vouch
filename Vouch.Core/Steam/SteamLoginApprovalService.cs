using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;

namespace Vouch.Core.Steam;

/// <summary>A pending login waiting for this authenticator's approval — i.e. someone entered the
/// account's password elsewhere and chose "approve on the Steam app". Vouch, holding the shared
/// secret, can approve or deny it.</summary>
public record PendingLoginSession(
    ulong ClientId,
    int Version,
    string DeviceName,
    string Ip,
    string City,
    string State,
    string Country,
    string Geoloc,
    bool RequestedPersistent)
{
    /// <summary>"City, State, Country" with the empty parts dropped.</summary>
    public string Location =>
        string.Join(", ", new[] { City, State, Country }.Where(s => s.Length > 0));

    /// <summary>A short label for the list. <see cref="DeviceName"/> is a browser User-Agent for web
    /// logins (long and ugly), so condense it to "Browser · OS"; real device names pass through.</summary>
    public string FriendlyDevice => SteamLoginApprovalService.FriendlyDeviceName(DeviceName);
}

/// <summary>An active login session for the account (one per signed-in device), from EnumerateTokens.
/// <paramref name="Description"/> is the device name Steam recorded — a real device name, or a browser
/// User-Agent for web logins.</summary>
public record ActiveSession(ulong TokenId, string Description)
{
    /// <summary>A short label: browser User-Agents condense to "Browser · OS"; device names pass through.</summary>
    public string FriendlyName => SteamLoginApprovalService.FriendlyDeviceName(Description);
}

/// <summary>
/// The "approve a login from the authenticator" side of Steam Guard — the mobile-app confirmation you
/// tap to allow a sign-in on another device. This is the IAuthenticationService flow (auth sessions),
/// which is separate from the trade/market mobileconf flow in <see cref="SteamConfirmationService"/>.
/// Requests are authenticated with the account's own access token; approvals are signed with the
/// shared secret (HMAC-SHA256), which proves possession of the authenticator.
/// </summary>
public class SteamLoginApprovalService
{
    private const string ApiBase = "https://api.steampowered.com/IAuthenticationService";
    private static readonly HttpClient Http = CreateClient();

    private static HttpClient CreateClient()
    {
        var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        http.DefaultRequestHeaders.UserAgent.ParseAdd("Vouch/1.0 (+https://github.com/Monodesu/Vouch)");
        return http;
    }

    // A Steam login QR encodes a challenge URL like "https://s.team/q/<version>/<client_id>".
    private static readonly Regex QrChallenge = new(@"^https?://s\.team/q/(\d+)/(\d+)", RegexOptions.IgnoreCase);

    /// <summary>Parses a scanned Steam login QR's challenge URL into its version and client id. Returns
    /// false for anything that isn't a Steam sign-in QR.</summary>
    public static bool TryParseQrChallenge(string? url, out int version, out ulong clientId)
    {
        version = 0; clientId = 0;
        if (string.IsNullOrEmpty(url)) return false;
        var m = QrChallenge.Match(url.Trim());
        return m.Success
            && int.TryParse(m.Groups[1].Value, out version)
            && ulong.TryParse(m.Groups[2].Value, out clientId);
    }

    /// <summary>Fetches the device/location info for one client id (e.g. a QR challenge), so a scanned
    /// sign-in can be shown before it's approved. Uses the caller's <paramref name="version"/> (from the
    /// QR) when the session info doesn't carry one. Returns null if the session can't be read.</summary>
    public async Task<PendingLoginSession?> FetchInfoAsync(string accessToken, ulong clientId, int version, CancellationToken ct = default)
    {
        var info = await FetchInfoAsync(accessToken, clientId, ct);
        return info is null ? null : info with { Version = version > 0 ? version : info.Version };
    }

    /// <summary>Lists the account's pending login approvals (each with the device/location shown to the
    /// requester), so the user can decide. Empty when nothing is waiting.</summary>
    public async Task<IReadOnlyList<PendingLoginSession>> FetchPendingAsync(string accessToken, CancellationToken ct = default)
    {
        var (body, eresult) = await GetAsync("GetAuthSessionsForAccount", accessToken, Array.Empty<byte>(), ct);
        if (eresult != 1) return Array.Empty<PendingLoginSession>();

        var clientIds = ParseClientIds(body);
        var list = new List<PendingLoginSession>(clientIds.Count);
        foreach (var id in clientIds)
            if (await FetchInfoAsync(accessToken, id, ct) is { } session)
                list.Add(session);
        return list;
    }

    private async Task<PendingLoginSession?> FetchInfoAsync(string accessToken, ulong clientId, CancellationToken ct)
    {
        using var req = new MemoryStream();
        WriteVarintField(req, 1, clientId); // client_id
        var (body, eresult) = await PostAsync("GetAuthSessionInfo", accessToken, req.ToArray(), ct);
        if (eresult != 1) return null;

        string ip = "", geoloc = "", city = "", state = "", country = "", device = "";
        int version = 1;
        bool persistent = true; // web "remember me" default; overridden by field 12 when present
        foreach (var (field, wire, varint, bytes) in ReadFields(body))
        {
            switch (field)
            {
                case 1: ip = Str(bytes); break;
                case 2: geoloc = Str(bytes); break;
                case 3: city = Str(bytes); break;
                case 4: state = Str(bytes); break;
                case 5: country = Str(bytes); break;
                case 7: device = Str(bytes); break;
                case 8 when wire == 0: version = (int)varint; break;
                case 12 when wire == 0: persistent = varint == 1; break; // ESessionPersistence.Persistent
            }
        }
        return new PendingLoginSession(clientId, version, device, ip, city, state, country, geoloc, persistent);
    }

    /// <summary>Approves (or denies) a pending login. The signature is HMAC-SHA256 over
    /// version‖client_id‖steamid (little-endian) keyed by the shared secret.</summary>
    public async Task<bool> RespondAsync(
        ulong steamId, string accessToken, byte[] sharedSecret, PendingLoginSession session, bool approve,
        bool persistent, CancellationToken ct = default)
    {
        var signature = Sign(sharedSecret, session.Version, session.ClientId, steamId);

        using var req = new MemoryStream();
        WriteVarintField(req, 1, (ulong)(uint)session.Version); // version (int32)
        WriteVarintField(req, 2, session.ClientId);             // client_id (uint64)
        WriteFixed64Field(req, 3, steamId);                     // steamid (fixed64)
        WriteBytesField(req, 4, signature);                     // signature
        WriteVarintField(req, 5, approve ? 1UL : 0UL);          // confirm
        WriteVarintField(req, 6, persistent ? 1UL : 0UL);       // ESessionPersistence: 1=Persistent, 0=Ephemeral

        var (_, eresult) = await PostAsync("UpdateAuthSessionWithMobileConfirmation", accessToken, req.ToArray(), ct);
        return eresult == 1;
    }

    /// <summary>HMAC-SHA256( shared_secret, version(u16 LE) ‖ client_id(u64 LE) ‖ steamid(u64 LE) ).</summary>
    internal static byte[] Sign(byte[] sharedSecret, int version, ulong clientId, ulong steamId)
    {
        var data = new byte[18];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0), (ushort)version);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(2), clientId);
        BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(10), steamId);
        using var hmac = new HMACSHA256(sharedSecret);
        return hmac.ComputeHash(data);
    }

    // ---- active sessions ("logged-in devices") — enumerate + revoke ----

    /// <summary>Lists the account's active login sessions (one per signed-in device). Requires a
    /// <em>fresh</em> access token — an expired one returns HTTP 401 (empty list here), so renew first.</summary>
    public async Task<IReadOnlyList<ActiveSession>> EnumerateSessionsAsync(string accessToken, CancellationToken ct = default)
    {
        // CAuthentication_RefreshToken_Enumerate_Request { bool include_revoked = 1 } — omit → false → empty body.
        var (body, eresult) = await PostAsync("EnumerateTokens", accessToken, Array.Empty<byte>(), ct);
        return eresult != 1 ? Array.Empty<ActiveSession>() : ParseSessions(body);
    }

    // CAuthentication_RefreshToken_Enumerate_Response { repeated RefreshTokenDescription refresh_tokens = 1 },
    // each { fixed64 token_id = 1; string token_description = 2; … }.
    internal static List<ActiveSession> ParseSessions(byte[] body)
    {
        var list = new List<ActiveSession>();
        foreach (var (field, wire, _, bytes) in ReadFields(body))
        {
            if (field != 1 || wire != 2 || bytes is null) continue;
            ulong tid = 0; string desc = "";
            foreach (var (f, w, v, b) in ReadFields(bytes))
            {
                if (f == 1 && w == 1) tid = v;               // token_id (fixed64)
                else if (f == 2 && w == 2) desc = Str(b);    // token_description
            }
            if (tid != 0) list.Add(new ActiveSession(tid, desc));
        }
        return list;
    }

    /// <summary>Condenses a session/device name for display: browser User-Agents become "Browser · OS";
    /// real device names pass through unchanged.</summary>
    public static string FriendlyDeviceName(string? name)
    {
        var ua = name ?? "";
        if (string.IsNullOrWhiteSpace(ua)) return "Unknown device";
        if (!ua.StartsWith("Mozilla/", StringComparison.OrdinalIgnoreCase)) return ua;

        string browser =
            ua.Contains("Edg/") ? "Edge" :
            ua.Contains("OPR/") || ua.Contains("Opera") ? "Opera" :
            ua.Contains("Chrome/") ? "Chrome" :
            ua.Contains("Firefox/") ? "Firefox" :
            ua.Contains("Safari/") ? "Safari" : "Browser";
        string os =
            ua.Contains("Windows NT") ? "Windows" :
            ua.Contains("Mac OS X") || ua.Contains("Macintosh") ? "macOS" :
            ua.Contains("Android") ? "Android" :
            ua.Contains("iPhone") || ua.Contains("iPad") ? "iOS" :
            ua.Contains("Linux") ? "Linux" : "";
        return os.Length > 0 ? $"{browser} · {os}" : browser;
    }

    // Web API verbs (verified against Steam): GetAuthSessionsForAccount is GET; GetAuthSessionInfo,
    // UpdateAuthSessionWithMobileConfirmation, EnumerateTokens and RevokeRefreshToken are POST. Steam
    // returns 405 if the wrong verb is used.

    private static async Task<(byte[] Body, int EResult)> GetAsync(
        string method, string accessToken, byte[] request, CancellationToken ct)
    {
        var url = $"{ApiBase}/{method}/v1/?access_token={Uri.EscapeDataString(accessToken)}"
                + $"&input_protobuf_encoded={Uri.EscapeDataString(Convert.ToBase64String(request))}";
        using var resp = await Http.GetAsync(url, ct);
        return await ReadResultAsync(resp, ct);
    }

    private static async Task<(byte[] Body, int EResult)> PostAsync(
        string method, string accessToken, byte[] request, CancellationToken ct)
    {
        var url = $"{ApiBase}/{method}/v1/?access_token={Uri.EscapeDataString(accessToken)}";
        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("input_protobuf_encoded", Convert.ToBase64String(request)),
        });
        using var resp = await Http.PostAsync(url, form, ct);
        return await ReadResultAsync(resp, ct);
    }

    private static async Task<(byte[] Body, int EResult)> ReadResultAsync(HttpResponseMessage resp, CancellationToken ct)
    {
        var body = await resp.Content.ReadAsByteArrayAsync(ct);
        int eresult = 2; // EResult.Fail
        if (resp.Headers.TryGetValues("x-eresult", out var v) && int.TryParse(v.FirstOrDefault(), out var er))
            eresult = er;
        return (body, eresult);
    }

    // client_ids is a repeated uint64 (field 1); accept both packed and unpacked encodings.
    internal static List<ulong> ParseClientIds(byte[] body)
    {
        var ids = new List<ulong>();
        foreach (var (field, wire, varint, bytes) in ReadFields(body))
        {
            if (field != 1) continue;
            if (wire == 0) ids.Add(varint);
            else if (wire == 2 && bytes is not null)
            {
                int p = 0;
                while (p < bytes.Length) ids.Add(ReadVarint(bytes, ref p));
            }
        }
        return ids;
    }

    // ---- minimal protobuf codec (only the wire types these messages use) ----

    internal static void WriteVarintField(Stream s, int field, ulong value)
    {
        WriteTag(s, field, 0);
        WriteVarint(s, value);
    }

    internal static void WriteFixed64Field(Stream s, int field, ulong value)
    {
        WriteTag(s, field, 1);
        Span<byte> buf = stackalloc byte[8];
        BinaryPrimitives.WriteUInt64LittleEndian(buf, value);
        s.Write(buf);
    }

    internal static void WriteBytesField(Stream s, int field, byte[] data)
    {
        WriteTag(s, field, 2);
        WriteVarint(s, (ulong)data.Length);
        s.Write(data, 0, data.Length);
    }

    private static void WriteTag(Stream s, int field, int wire) => WriteVarint(s, (ulong)((field << 3) | wire));

    private static void WriteVarint(Stream s, ulong v)
    {
        while (v >= 0x80) { s.WriteByte((byte)(v | 0x80)); v >>= 7; }
        s.WriteByte((byte)v);
    }

    private static IEnumerable<(int Field, int Wire, ulong Varint, byte[]? Bytes)> ReadFields(byte[] data)
    {
        int pos = 0;
        while (pos < data.Length)
        {
            ulong tag = ReadVarint(data, ref pos);
            int field = (int)(tag >> 3);
            int wire = (int)(tag & 7);
            switch (wire)
            {
                case 0: yield return (field, 0, ReadVarint(data, ref pos), null); break;
                case 1:
                    var f64 = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos, 8)); pos += 8;
                    yield return (field, 1, f64, null); break;
                case 2:
                    int len = (int)ReadVarint(data, ref pos);
                    var slice = data[pos..(pos + len)]; pos += len;
                    yield return (field, 2, 0, slice); break;
                case 5: pos += 4; yield return (field, 5, 0, null); break;
                default: yield break; // unknown wire type — stop rather than misread
            }
        }
    }

    private static ulong ReadVarint(byte[] data, ref int pos)
    {
        ulong result = 0; int shift = 0;
        while (pos < data.Length)
        {
            byte b = data[pos++];
            result |= (ulong)(b & 0x7F) << shift;
            if ((b & 0x80) == 0) break;
            shift += 7;
        }
        return result;
    }

    private static string Str(byte[]? bytes) => bytes is null ? "" : Encoding.UTF8.GetString(bytes);
}
