using SteamKit2;
using SteamKit2.Authentication;
using SteamKit2.Internal;

namespace Vouch.Core.Steam;

/// <summary>
/// Supplies the codes Steam asks for during login. The UI (or a maFile-backed
/// implementation) provides them. Mirrors SteamKit2's IAuthenticator, but keeps
/// SteamKit2 out of the app layer.
/// </summary>
public interface ISteamAuthenticator
{
    Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect);
    Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect);
    Task<bool> AcceptDeviceConfirmationAsync();
}

public record SteamLoginResult(ulong SteamId, string AccessToken, string RefreshToken);

/// <summary>
/// Logs into Steam via the mobile-app auth flow (SteamKit2) and returns fresh
/// access/refresh tokens. Used to (re)establish a web session for confirmations
/// and to link new authenticators.
/// </summary>
public class SteamLoginService
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(20);

    public async Task<SteamLoginResult> LoginAsync(
        string username, string password, ISteamAuthenticator authenticator, CancellationToken ct = default)
    {
        var client = new SteamClient();
        client.Connect();

        try
        {
            await WaitForConnectionAsync(client, ct);

            var authSession = await client.Authentication.BeginAuthSessionViaCredentialsAsync(new AuthSessionDetails
            {
                Username = username,
                Password = password,
                IsPersistentSession = false,
                PlatformType = EAuthTokenPlatformType.k_EAuthTokenPlatformType_MobileApp,
                ClientOSType = EOSType.Android9,
                Authenticator = new AuthenticatorAdapter(authenticator),
            });

            var poll = await authSession.PollingWaitForResultAsync(ct);

            return new SteamLoginResult(
                authSession.SteamID.ConvertToUInt64(),
                poll.AccessToken,
                poll.RefreshToken);
        }
        finally
        {
            client.Disconnect();
        }
    }

    private static async Task WaitForConnectionAsync(SteamClient client, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + ConnectTimeout;
        while (!client.IsConnected)
        {
            ct.ThrowIfCancellationRequested();
            if (DateTimeOffset.UtcNow > deadline)
                throw new TimeoutException("Timed out connecting to Steam.");
            await Task.Delay(200, ct);
        }
    }

    private sealed class AuthenticatorAdapter(ISteamAuthenticator inner) : IAuthenticator
    {
        public Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
            => inner.GetDeviceCodeAsync(previousCodeWasIncorrect);

        public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
            => inner.GetEmailCodeAsync(email, previousCodeWasIncorrect);

        public Task<bool> AcceptDeviceConfirmationAsync()
            => inner.AcceptDeviceConfirmationAsync();
    }
}
