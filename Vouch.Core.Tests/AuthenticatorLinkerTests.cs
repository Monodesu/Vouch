using Vouch.Core.Steam;

namespace Vouch.Core.Tests;

public class AuthenticatorLinkerTests
{
    [Fact]
    public void ParseAddResponse_Success_MapsSecrets()
    {
        const string json = """
            {"response":{
              "shared_secret":"c2hhcmVk","serial_number":"123","revocation_code":"R55555",
              "uri":"otpauth://totp/Steam","server_time":"1700000000","account_name":"tester",
              "token_gid":"abc","identity_secret":"aWRlbnRpdHk=","secret_1":"czE=",
              "status":1,"phone_number_hint":"1234"}}
            """;
        var result = SteamAuthenticatorLinker.ParseAddResponse(json, 76561198000000001, "tok", "android:dev");

        Assert.Equal(AddAuthenticatorStatus.Success, result.Status);
        Assert.Equal("c2hhcmVk", result.Account!.SharedSecret);
        Assert.Equal("aWRlbnRpdHk=", result.Account.IdentitySecret);
        Assert.Equal("R55555", result.Account.RevocationCode);
        Assert.Equal("android:dev", result.Account.DeviceId);
        Assert.Equal("tok", result.Account.Session!.AccessToken);
        Assert.Equal(76561198000000001UL, result.Account.Session.SteamId);
        Assert.Equal("1234", result.PhoneHint);
    }

    [Fact]
    public void ParseAddResponse_AuthenticatorPresent()
    {
        var result = SteamAuthenticatorLinker.ParseAddResponse(
            """{"response":{"status":29}}""", 1, "t", "d");
        Assert.Equal(AddAuthenticatorStatus.AuthenticatorPresent, result.Status);
    }

    [Fact]
    public void ParseAddResponse_Status2_NeedsPhone()
    {
        // Steam status 2 = no phone on the account.
        var result = SteamAuthenticatorLinker.ParseAddResponse(
            """{"response":{"status":2}}""", 1, "t", "d");
        Assert.Equal(AddAuthenticatorStatus.NeedsPhone, result.Status);
        Assert.Contains("phone", result.Error, System.StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ParseAddResponse_OtherNonSuccess_Failure()
    {
        var result = SteamAuthenticatorLinker.ParseAddResponse(
            """{"response":{"status":84}}""", 1, "t", "d");
        Assert.Equal(AddAuthenticatorStatus.Failure, result.Status);
    }

    [Fact]
    public void ParseAddResponse_GarbageOrEmpty()
    {
        Assert.Equal(AddAuthenticatorStatus.Failure,
            SteamAuthenticatorLinker.ParseAddResponse("not json", 1, "t", "d").Status);
        Assert.Equal(AddAuthenticatorStatus.Failure,
            SteamAuthenticatorLinker.ParseAddResponse("{}", 1, "t", "d").Status);
    }

    [Fact]
    public void ParseRemoveResponse_Success()
    {
        var r = SteamAuthenticatorLinker.ParseRemoveResponse(
            """{"response":{"success":true,"revocation_attempts_remaining":5}}""");
        Assert.True(r.Success);
        Assert.Equal(5, r.AttemptsRemaining);
    }

    [Fact]
    public void ParseRemoveResponse_WrongCode_ReportsAttempts()
    {
        var r = SteamAuthenticatorLinker.ParseRemoveResponse(
            """{"response":{"success":false,"revocation_attempts_remaining":3}}""");
        Assert.False(r.Success);
        Assert.Equal(3, r.AttemptsRemaining);
        Assert.Contains("3", r.Error);
    }

    [Fact]
    public void ParseRemoveResponse_Garbage()
    {
        Assert.False(SteamAuthenticatorLinker.ParseRemoveResponse("not json").Success);
        Assert.False(SteamAuthenticatorLinker.ParseRemoveResponse("{}").Success);
    }

    [Fact]
    public void GenerateDeviceId_HasAndroidPrefix()
    {
        Assert.StartsWith("android:", SteamAuthenticatorLinker.GenerateDeviceId());
        Assert.NotEqual(SteamAuthenticatorLinker.GenerateDeviceId(), SteamAuthenticatorLinker.GenerateDeviceId());
    }
}
