using System.Text.Json;

namespace Vouch.Core.Steam;

public enum AddAuthenticatorStatus { Success, AuthenticatorPresent, NeedsPhone, Failure }

public record AddAuthenticatorResult(
    AddAuthenticatorStatus Status,
    SteamGuardAccount? Account = null,
    string? PhoneHint = null,
    string? Error = null);

public enum FinalizeStatus { Success, BadSmsCode, UnableToSyncTime, Failure }

public record PhoneAddResult(bool Ok, string? Email = null, string? Error = null);

public record RemoveAuthenticatorResult(bool Success, int AttemptsRemaining = 0, string? Error = null);

/// <summary>
/// Links this app as an account's mobile authenticator — a native port of SteamAuth's
/// AuthenticatorLinker. All calls pass <c>access_token</c> in the query string (as the
/// original does). Adding an authenticator REQUIRES a phone on the account; accounts
/// without one go through the phone sub-flow (set number → email link → verify by SMS).
/// </summary>
public class SteamAuthenticatorLinker
{
    private const string TwoFactor = "https://api.steampowered.com/ITwoFactorService/";
    private const string PhoneService = "https://api.steampowered.com/IPhoneService/";
    private static readonly HttpClient Http = new() { Timeout = TimeSpan.FromSeconds(20) };

    public static string GenerateDeviceId() => "android:" + Guid.NewGuid();

    // ---- phone flow (accounts without a phone attached) ----

    /// <summary>Whether a *verified* phone is already on the account.</summary>
    public async Task<bool> HasPhoneAsync(string accessToken, CancellationToken ct = default)
    {
        var json = await Post(PhoneService + "AccountPhoneStatus/v1", accessToken, null, ct);
        return Response(json) is { } r && Bool(r, "verified_phone");
    }

    /// <summary>
    /// Starts adding a phone. Steam emails a confirmation link to the account; the returned
    /// address is where it went. The user must click it before the number can be verified.
    /// </summary>
    public async Task<PhoneAddResult> SetPhoneAsync(
        string accessToken, string phoneNumber, string countryCode, CancellationToken ct = default)
    {
        var json = await Post(PhoneService + "SetAccountPhoneNumber/v1", accessToken, new()
        {
            ["phone_number"] = phoneNumber,
            ["phone_country_code"] = countryCode,
        }, ct);
        return ParseSetPhone(json);
    }

    private static PhoneAddResult ParseSetPhone(string json)
    {
        if (Response(json) is not { } r) return new PhoneAddResult(false, Error: "Steam rejected the phone number.");
        var email = Str(r, "confirmation_email_address");
        return string.IsNullOrEmpty(email)
            ? new PhoneAddResult(false, Error: "Steam did not send a confirmation email.")
            : new PhoneAddResult(true, email);
    }

    /// <summary>The account's registered country code — used when the user leaves it blank.</summary>
    public async Task<string?> GetUserCountryAsync(ulong steamId, string accessToken, CancellationToken ct = default)
    {
        var json = await Post("https://api.steampowered.com/IUserAccountService/GetUserCountry/v1",
            accessToken, new() { ["steamid"] = steamId.ToString() }, ct);
        var country = Response(json) is { } r ? Str(r, "country") : "";
        return string.IsNullOrEmpty(country) ? null : country;
    }

    /// <summary>True while Steam is still waiting for the email confirmation link to be clicked.</summary>
    public async Task<bool> IsAwaitingEmailConfirmationAsync(string accessToken, CancellationToken ct = default)
    {
        var json = await Post(PhoneService + "IsAccountWaitingForEmailConfirmation/v1", accessToken, null, ct);
        return Response(json) is not { } r || Bool(r, "awaiting_email_confirmation"); // unknown → assume waiting
    }

    /// <summary>Texts an SMS to the (email-confirmed) phone so it can be verified.</summary>
    public Task SendPhoneVerificationSmsAsync(string accessToken, CancellationToken ct = default)
        => Post(PhoneService + "SendPhoneVerificationCode/v1", accessToken, null, ct);

    /// <summary>Verifies the phone with the SMS code Steam texted.</summary>
    public async Task VerifyPhoneWithCodeAsync(string accessToken, string smsCode, CancellationToken ct = default)
        => await Post(PhoneService + "VerifyAccountPhoneWithCode/v1", accessToken, new() { ["code"] = smsCode }, ct);

    // ---- authenticator ----

    public async Task<AddAuthenticatorResult> AddAuthenticatorAsync(
        ulong steamId, string accessToken, string deviceId, CancellationToken ct = default)
    {
        var json = await Post(TwoFactor + "AddAuthenticator/v1", accessToken, new()
        {
            ["steamid"] = steamId.ToString(),
            ["authenticator_type"] = "1",
            ["device_identifier"] = deviceId,
            ["sms_phone_id"] = "1",
            ["version"] = "2",
        }, ct);
        return ParseAddResponse(json, steamId, accessToken, deviceId);
    }

    internal static AddAuthenticatorResult ParseAddResponse(
        string json, ulong steamId, string accessToken, string deviceId)
    {
        if (Response(json) is not { } r)
            return new(AddAuthenticatorStatus.Failure, Error: "Empty or malformed response from Steam.");

        int status = Int(r, "status");
        if (status == 2) return new(AddAuthenticatorStatus.NeedsPhone,
            Error: "This account has no phone. Add one to continue.");
        if (status == 29) return new(AddAuthenticatorStatus.AuthenticatorPresent);
        var shared = Str(r, "shared_secret");
        if (status != 1 || string.IsNullOrEmpty(shared))
            return new(AddAuthenticatorStatus.Failure, Error: $"Steam returned status {status}.");

        var account = new SteamGuardAccount
        {
            SharedSecret = shared,
            SerialNumber = Str(r, "serial_number"),
            RevocationCode = Str(r, "revocation_code"),
            Uri = Str(r, "uri"),
            ServerTime = Long(r, "server_time"),
            AccountName = Str(r, "account_name"),
            TokenGid = Str(r, "token_gid"),
            IdentitySecret = Str(r, "identity_secret"),
            Secret1 = Str(r, "secret_1"),
            Status = status,
            DeviceId = deviceId,
            FullyEnrolled = false,
            Session = new SessionData { SteamId = steamId, AccessToken = accessToken },
        };
        return new(AddAuthenticatorStatus.Success, account, Str(r, "phone_number_hint"));
    }

    public async Task<FinalizeStatus> FinalizeAsync(
        ulong steamId, string accessToken, SteamGuardAccount linked, string smsCode, CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(linked.SharedSecret)) return FinalizeStatus.Failure;
        var secret = Convert.FromBase64String(linked.SharedSecret);
        await SteamTime.EnsureAlignedAsync(ct);

        for (int tries = 0; tries < 10; tries++)
        {
            // Steam's want_more asks for successive-window codes to sync our clock offset.
            long time = SteamTime.Now() + tries * SteamGuard.Period;
            var json = await Post(TwoFactor + "FinalizeAddAuthenticator/v1", accessToken, new()
            {
                ["steamid"] = steamId.ToString(),
                ["authenticator_code"] = SteamGuard.GenerateCode(secret, time / SteamGuard.Period),
                ["authenticator_time"] = time.ToString(),
                ["activation_code"] = smsCode,
                ["validate_sms_code"] = "1",
            }, ct);

            if (Response(json) is not { } r) return FinalizeStatus.Failure;

            int status = Int(r, "status");
            if (status == 89) return FinalizeStatus.BadSmsCode;                 // bad SMS code
            if (status == 88 && tries >= 9) return FinalizeStatus.UnableToSyncTime;

            bool success = r.TryGetProperty("success", out var s) && s.ValueKind == JsonValueKind.True;
            bool wantMore = r.TryGetProperty("want_more", out var w) && w.ValueKind == JsonValueKind.True;

            if (wantMore) continue;
            if (!success) return FinalizeStatus.Failure;

            linked.FullyEnrolled = true;
            return FinalizeStatus.Success;
        }
        return FinalizeStatus.UnableToSyncTime;
    }

    /// <summary>
    /// Removes this authenticator from the account using its revocation code.
    /// <paramref name="scheme"/>: 1 = fall back to email codes, 2 = turn Steam Guard off entirely.
    /// Wrong codes burn one of a limited number of revocation attempts.
    /// </summary>
    public async Task<RemoveAuthenticatorResult> RemoveAuthenticatorAsync(
        string accessToken, string revocationCode, int scheme, CancellationToken ct = default)
    {
        var json = await Post(TwoFactor + "RemoveAuthenticator/v1", accessToken, new()
        {
            ["revocation_code"] = revocationCode,
            ["revocation_reason"] = "1",
            ["steamguard_scheme"] = scheme.ToString(),
        }, ct);
        return ParseRemoveResponse(json);
    }

    internal static RemoveAuthenticatorResult ParseRemoveResponse(string json)
    {
        if (Response(json) is not { } r)
            return new(false, Error: "Empty or malformed response from Steam.");
        int remaining = Int(r, "revocation_attempts_remaining");
        return Bool(r, "success")
            ? new(true, remaining)
            : new(false, remaining, remaining > 0
                ? $"Steam rejected the revocation code — {remaining} attempt(s) remaining."
                : "Steam rejected the removal request.");
    }

    // ---- helpers ----

    private static async Task<string> Post(
        string endpoint, string accessToken, Dictionary<string, string>? form, CancellationToken ct)
    {
        var url = endpoint + "?access_token=" + Uri.EscapeDataString(accessToken);
        using var content = new FormUrlEncodedContent(form ?? new Dictionary<string, string>());
        var resp = await Http.PostAsync(url, content, ct);
        resp.EnsureSuccessStatusCode();
        return await resp.Content.ReadAsStringAsync(ct);
    }

    /// <summary>Returns the <c>response</c> object, or null if missing/malformed.</summary>
    private static JsonElement? Response(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty("response", out var r) && r.ValueKind == JsonValueKind.Object)
                return r.Clone();
        }
        catch (JsonException) { }
        return null;
    }

    private static string Str(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) ? v.GetString() ?? "" : "";

    private static bool Bool(JsonElement e, string name) =>
        e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.True;

    private static int Int(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind == JsonValueKind.Number ? v.GetInt32()
             : int.TryParse(v.GetString(), out var n) ? n : 0;
    }

    private static long Long(JsonElement e, string name)
    {
        if (!e.TryGetProperty(name, out var v)) return 0;
        return v.ValueKind == JsonValueKind.Number ? v.GetInt64()
             : long.TryParse(v.GetString(), out var n) ? n : 0;
    }
}
