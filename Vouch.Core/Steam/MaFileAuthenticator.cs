namespace Vouch.Core.Steam;

/// <summary>
/// An <see cref="ISteamAuthenticator"/> for an account that already has Vouch as its
/// authenticator: the mobile 2FA code is generated from the shared secret. Email codes
/// (used when linking a brand-new authenticator) are delegated to a UI callback.
/// </summary>
public class MaFileAuthenticator : ISteamAuthenticator
{
    private readonly byte[]? _sharedSecret;
    private readonly Func<string, bool, Task<string>>? _emailCodeProvider;

    public MaFileAuthenticator(byte[]? sharedSecret, Func<string, bool, Task<string>>? emailCodeProvider = null)
    {
        _sharedSecret = sharedSecret;
        _emailCodeProvider = emailCodeProvider;
    }

    public async Task<string> GetDeviceCodeAsync(bool previousCodeWasIncorrect)
    {
        // A rejected code means the current 30s window is spent — wait for a fresh one.
        if (previousCodeWasIncorrect)
            await Task.Delay(TimeSpan.FromSeconds(SteamGuard.Period));

        if (_sharedSecret is null)
            throw new InvalidOperationException("No shared secret available to generate a device code.");

        await SteamTime.EnsureAlignedAsync();
        return SteamGuard.GenerateCode(_sharedSecret, SteamGuard.CurrentWindow(SteamTime.UtcNow));
    }

    public Task<string> GetEmailCodeAsync(string email, bool previousCodeWasIncorrect)
        => _emailCodeProvider?.Invoke(email, previousCodeWasIncorrect)
           ?? throw new InvalidOperationException("This account requires an email code, but no provider was supplied.");

    public Task<bool> AcceptDeviceConfirmationAsync() => Task.FromResult(false);
}
