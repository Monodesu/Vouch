using System.Threading.Tasks;
using CommunityToolkit.Mvvm.Input;
using Vouch.App.Localization;
using Vouch.App.Platform;
using Vouch.Core.Steam;

namespace Vouch.App.ViewModels;

/// <summary>
/// QR sign-in: grab a Steam login QR from the clipboard or a full-screen capture, decode it, and approve
/// it with the selected account (the scanning device then signs into that account). Reuses the login
/// approval flow — a scanned QR is just another auth session to confirm.
/// </summary>
public partial class MainViewModel
{
    [RelayCommand]
    private Task ScanQrClipboard() => ScanQrAsync(fromClipboard: true);

    [RelayCommand]
    private Task ScanQrScreen() => ScanQrAsync(fromClipboard: false);

    private async Task ScanQrAsync(bool fromClipboard)
    {
        if (SelectedAccount is not { IsReal: true, HasSession: true } acc)
        {
            ShowToast(Loc.T("Qr_NeedSignIn"), ToastKind.Error);
            return;
        }

        // Capture on the UI thread (clipboard access needs it); decode off it (heavier).
        var capture = fromClipboard ? QrScanner.CaptureClipboard() : QrScanner.CaptureScreen();
        if (capture is not { } cap)
        {
            ShowToast(Loc.T(fromClipboard ? "Qr_NoClipboardImage" : "Qr_CaptureFailed"), ToastKind.Error);
            return;
        }

        var text = await Task.Run(() => QrScanner.Decode(cap));
        if (!SteamLoginApprovalService.TryParseQrChallenge(text, out var version, out var clientId))
        {
            ShowToast(Loc.T("Qr_NoQr"), ToastKind.Error);
            return;
        }

        if (!await EnsureFreshSessionAsync(acc) || acc.AccessToken is not { } token)
        {
            ShowToast(Loc.T("Confirm_StatusSessionExpiredShort"), ToastKind.Error);
            return;
        }

        // Show the device/location for confirmation before approving; fall back to a bare session.
        var session = await _loginApproval.FetchInfoAsync(token, clientId, version)
                      ?? new PendingLoginSession(clientId, version, "", "", "", "", "", "", RequestedPersistent: true);
        ShowScannedLogin(session);
    }
}
