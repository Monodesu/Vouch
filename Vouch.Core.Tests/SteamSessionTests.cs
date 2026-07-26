using System.Text;
using System.Text.Json;
using Vouch.Core.Steam;

namespace Vouch.Core.Tests;

public class SteamSessionTests
{
    private static string Base64Url(byte[] b) =>
        Convert.ToBase64String(b).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private static string MakeJwt(long exp)
    {
        var header = Base64Url(Encoding.UTF8.GetBytes("""{"alg":"HS256","typ":"JWT"}"""));
        var payload = Base64Url(Encoding.UTF8.GetBytes($$"""{"exp":{{exp}},"sub":"x"}"""));
        return $"{header}.{payload}.signature";
    }

    [Fact]
    public void GetJwtExpiry_ReadsExpClaim()
    {
        long exp = 1_900_000_000;
        Assert.Equal(exp, SteamSessionService.GetJwtExpiry(MakeJwt(exp)));
        Assert.Null(SteamSessionService.GetJwtExpiry("not-a-jwt"));
    }

    [Fact]
    public void IsExpired_PastToken_True_FutureToken_False()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        Assert.True(SteamSessionService.IsExpired(MakeJwt(now - 100), TimeSpan.Zero));
        Assert.False(SteamSessionService.IsExpired(MakeJwt(now + 3600), TimeSpan.Zero));
    }

    [Fact]
    public void IsExpired_RespectsBuffer_AndNullIsExpired()
    {
        long now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        // expires in 60s, but a 5-minute buffer means we treat it as expired
        Assert.True(SteamSessionService.IsExpired(MakeJwt(now + 60), TimeSpan.FromMinutes(5)));
        Assert.True(SteamSessionService.IsExpired(null, TimeSpan.Zero));
        Assert.True(SteamSessionService.IsExpired("garbage", TimeSpan.Zero));
    }

    [Fact]
    public void ParseRenewResponse_UsesNewAccessToken_KeepsOldRefreshWhenEmpty()
    {
        var r = SteamSessionService.ParseRenewResponse(
            """{"response":{"access_token":"newAccess","refresh_token":""}}""", 123, "oldRefresh");
        Assert.NotNull(r);
        Assert.Equal("newAccess", r!.AccessToken);
        Assert.Equal("oldRefresh", r.RefreshToken);
        Assert.Equal(123UL, r.SteamId);
    }

    [Fact]
    public void ParseRenewResponse_UsesNewRefreshWhenProvided_NullOnMissing()
    {
        var r = SteamSessionService.ParseRenewResponse(
            """{"response":{"access_token":"a","refresh_token":"newRefresh"}}""", 1, "old");
        Assert.Equal("newRefresh", r!.RefreshToken);

        Assert.Null(SteamSessionService.ParseRenewResponse("""{"response":{}}""", 1, "old"));
        Assert.Null(SteamSessionService.ParseRenewResponse("garbage", 1, "old"));
    }
}
