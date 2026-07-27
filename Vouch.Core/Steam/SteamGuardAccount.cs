using System.Text.Json.Serialization;

namespace Vouch.Core.Steam;

/// <summary>
/// The contents of a Steam <c>.maFile</c> — mirrors SteamAuth's SteamGuardAccount so
/// files produced by the original SDA / Steam mobile app deserialize unchanged.
/// </summary>
public class SteamGuardAccount
{
    [JsonPropertyName("shared_secret")] public string? SharedSecret { get; set; }
    [JsonPropertyName("serial_number")] public string? SerialNumber { get; set; }
    [JsonPropertyName("revocation_code")] public string? RevocationCode { get; set; }
    [JsonPropertyName("uri")] public string? Uri { get; set; }
    [JsonPropertyName("server_time")] public long ServerTime { get; set; }
    [JsonPropertyName("account_name")] public string? AccountName { get; set; }
    [JsonPropertyName("token_gid")] public string? TokenGid { get; set; }
    [JsonPropertyName("identity_secret")] public string? IdentitySecret { get; set; }
    [JsonPropertyName("secret_1")] public string? Secret1 { get; set; }
    [JsonPropertyName("status")] public int Status { get; set; }
    [JsonPropertyName("device_id")] public string? DeviceId { get; set; }
    [JsonPropertyName("fully_enrolled")] public bool FullyEnrolled { get; set; }
    [JsonPropertyName("Session")] public SessionData? Session { get; set; }

    /// <summary>Vouch extension (not in the original maFile): the Steam account password, kept for the
    /// convenience Copy/Sign-in fields. Other tools ignore the unknown key; it's encrypted at rest
    /// when maFile encryption is on.</summary>
    [JsonPropertyName("account_password")] public string? AccountPassword { get; set; }

    /// <summary>Vouch extension (not in the original maFile): a free-text note the user can jot against
    /// the account. Other tools ignore the unknown key; encrypted at rest when maFile encryption is on.</summary>
    [JsonPropertyName("account_notes")] public string? AccountNotes { get; set; }
}

/// <summary>Steam web session. Newer files use access/refresh tokens; older ones the cookies.</summary>
public class SessionData
{
    [JsonPropertyName("SteamID")] public ulong SteamId { get; set; }
    [JsonPropertyName("AccessToken")] public string? AccessToken { get; set; }
    [JsonPropertyName("RefreshToken")] public string? RefreshToken { get; set; }
    [JsonPropertyName("SessionID")] public string? SessionId { get; set; }

    // Legacy fields (pre-2023 login) — kept so old maFiles round-trip.
    [JsonPropertyName("SteamLogin")] public string? SteamLogin { get; set; }
    [JsonPropertyName("SteamLoginSecure")] public string? SteamLoginSecure { get; set; }
    [JsonPropertyName("WebCookie")] public string? WebCookie { get; set; }
    [JsonPropertyName("OAuthToken")] public string? OAuthToken { get; set; }
}
