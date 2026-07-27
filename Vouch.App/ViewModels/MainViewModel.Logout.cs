using System;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.App.Localization;
using Vouch.App.Platform;
using Vouch.Core.Steam;

namespace Vouch.App.ViewModels;

/// <summary>One active login session in the Devices tab (a device the account is signed in on).</summary>
public sealed class DeviceItem
{
    public string Name { get; }   // friendly ("Chrome · Windows", "MONO-PC (SteamKit2)")
    public string Raw { get; }    // the full description Steam recorded
    public DeviceItem(string name, string raw) { Name = name; Raw = raw; }
}

/// <summary>
/// The Devices tab: a read-only list of the account's active login sessions (from EnumerateTokens).
/// Steam only lets you actually sign devices out on the web, so "Manage in browser" hands this account's
/// session to a fresh, isolated browser window (see <see cref="CdpBrowser"/>) landing on the Steam
/// "Security &amp; Devices" page, where the user can hit "Sign out of all devices".
/// </summary>
public partial class MainViewModel
{
    private readonly SteamWebSessionService _webSession = new();

    public ObservableCollection<DeviceItem> Devices { get; } = new();
    internal bool DevicesLoaded;
    [ObservableProperty] private bool _devicesBusy;
    [ObservableProperty] private string _devicesStatus = "";
    public int DevicesCount => Devices.Count;
    public bool HasDevices => Devices.Count > 0;

    private void NotifyDevicesChanged()
    {
        OnPropertyChanged(nameof(DevicesCount));
        OnPropertyChanged(nameof(HasDevices));
    }

    /// <summary>Loads the selected account's active sessions (needs a live session; renews first).</summary>
    [RelayCommand]
    private async Task RefreshDevices()
    {
        if (SelectedAccount is not { IsReal: true, HasSession: true } acc || DevicesBusy)
        {
            if (SelectedAccount is not { HasSession: true }) DevicesStatus = Loc.T("Devices_SignIn");
            return;
        }
        DevicesBusy = true;
        DevicesStatus = Loc.T("Devices_Loading");
        try
        {
            if (!await EnsureFreshSessionAsync(acc) || acc.AccessToken is not { } token)
            {
                DevicesStatus = Loc.T("Devices_SessionExpired");
                return;
            }
            var sessions = await _loginApproval.EnumerateSessionsAsync(token);
            if (!ReferenceEquals(SelectedAccount, acc)) return; // account switched mid-fetch
            Devices.Clear();
            foreach (var s in sessions) Devices.Add(new DeviceItem(s.FriendlyName, s.Description));
            NotifyDevicesChanged();
            DevicesStatus = sessions.Count == 0 ? Loc.T("Devices_None") : "";
        }
        catch (Exception ex) { DevicesStatus = StatusLine.Error(ex); }
        finally { DevicesBusy = false; }
    }

    /// <summary>Opens a fresh, isolated browser window already signed in to this account, on Steam's
    /// "Security &amp; Devices" page — the only place Steam offers signing devices out.</summary>
    [RelayCommand]
    private async Task OpenDevicesInBrowser()
    {
        if (SelectedAccount is not { IsReal: true } acc || acc.Model?.Session is not { } session
            || !ulong.TryParse(acc.SteamId, out var steamId))
            return;
        if (string.IsNullOrEmpty(session.RefreshToken)) { ShowToast(Loc.T("Devices_SignIn"), ToastKind.Error); return; }
        if (!CdpBrowser.IsAvailable) { ShowToast(Loc.T("Devices_NoBrowser"), ToastKind.Error); return; }

        DevicesBusy = true;
        DevicesStatus = Loc.T("Devices_Opening");
        try
        {
            var sessionId = Guid.NewGuid().ToString("N")[..24];
            var cookie = await _webSession.GetStoreLoginCookieAsync(steamId, session.RefreshToken, sessionId);
            if (cookie is null)
            {
                acc.SessionInvalid = true; // the web handshake confirmed the refresh token is dead
                ShowToast(Loc.T("Devices_SessionExpired"), ToastKind.Error);
                return;
            }

            await CdpBrowser.LaunchWithCookieAsync(cookie, "https://store.steampowered.com/account/authorizeddevices");
            DevicesStatus = "";
            ShowToast(Loc.T("Devices_BrowserOpened"), ToastKind.Success);
        }
        catch (Exception ex) { ShowToast(StatusLine.Error(ex), ToastKind.Error); DevicesStatus = ""; }
        finally { DevicesBusy = false; }
    }
}
