using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Vouch.Core.Steam;
using Vouch.App.Localization;
using Vouch.App.Models;

namespace Vouch.App.ViewModels;

/// <summary>Trade confirmations: fetching, accept/deny, and the session renewal they depend on.</summary>
public partial class MainViewModel
{
    private readonly SteamConfirmationService _confirmService = new();
    private readonly SteamLoginApprovalService _loginApproval = new();
    private readonly SteamSessionService _session = new();

    [ObservableProperty] private bool _confirmationsBusy;
    [ObservableProperty] private string _confirmationsStatus = "";

    /// <summary>
    /// Ensures the account's access token is still valid, renewing it from the refresh token
    /// (and re-saving the maFile) if it has expired. Returns false if a manual re-login is needed.
    /// </summary>
    private async Task<bool> EnsureFreshSessionAsync(AccountViewModel acc)
    {
        if (acc.Session is not { } session) return false;
        if (!SteamSessionService.IsExpired(session.AccessToken, TimeSpan.FromMinutes(5)))
            return true;

        if (string.IsNullOrEmpty(session.RefreshToken))
        {
            acc.HasSession = false;
            return false;
        }

        // If the ~200-day refresh token itself is inside its last month, ask Steam for a new one too.
        bool renewRefresh = SteamSessionService.IsExpired(session.RefreshToken, TimeSpan.FromDays(30));
        var renewed = await _session.RenewAsync(session.SteamId, session.RefreshToken, renewRefresh);
        if (renewed is null)
        {
            acc.HasSession = false;
            return false;
        }

        session.AccessToken = renewed.AccessToken;
        session.RefreshToken = renewed.RefreshToken;
        _repo.Save(acc.Model!);
        acc.HasSession = true;
        return true;
    }

    private void LoadConfirmations(AccountViewModel? acc)
    {
        Confirmations.Clear();
        ConfirmationsStatus = "";
        if (acc is null) return;

        // Demo accounts keep the sample list.
        if (!acc.IsReal)
        {
            var samples = new[]
            {
                new ConfirmationItem { Kind = ConfirmationKind.Trade, Title = "Trade with SgtPepper", Subtitle = "You give 3, you receive 1 · Mann Co. Key", When = "2m ago" },
                new ConfirmationItem { Kind = ConfirmationKind.MarketListing, Title = "List on Community Market", Subtitle = "AK-47 | Redline (Field-Tested) · $12.40", When = "8m ago" },
                new ConfirmationItem { Kind = ConfirmationKind.Trade, Title = "Trade with quicksell_bot", Subtitle = "You give 12 items, you receive 4 keys", When = "31m ago" },
                new ConfirmationItem { Kind = ConfirmationKind.ApiKey, Title = "Register Web API Key", Subtitle = "Domain: localhost", When = "1h ago" },
                new ConfirmationItem { Kind = ConfirmationKind.MarketListing, Title = "List on Community Market", Subtitle = "Sticker | Titan (Holo) · $88.10", When = "3h ago" },
            };
            foreach (var c in samples.Take(acc.PendingConfirmations))
                Confirmations.Add(c);
            return;
        }

        // Real account: fetch from Steam if we have a session.
        if (!acc.HasSession)
        {
            ConfirmationsStatus = Loc.T("Confirm_StatusSignIn");
            return;
        }
        _ = FetchConfirmationsAsync(acc);
    }

    [RelayCommand]
    private async Task RefreshConfirmations()
    {
        if (SelectedAccount is { IsReal: true, HasSession: true } acc)
            await FetchConfirmationsAsync(acc);
    }

    private bool _sweeping;

    /// <summary>
    /// Background sweep of every signed-in account: refreshes pending counts (for non-selected accounts)
    /// and raises a one-time system notification when a new confirmation or incoming trade offer appears.
    /// Sequential and re-entrancy-guarded; skipped while locked or mid batch sign-in.
    /// </summary>
    public async Task SweepAllConfirmationsAsync()
    {
        if (_sweeping || ShowUnlock || BatchSignInActive) return;
        _sweeping = true;
        try
        {
            foreach (var acc in Accounts.ToList())
            {
                if (acc is not { IsReal: true, HasSession: true }) continue;
                await CheckAccountAsync(acc, updateCount: !ReferenceEquals(acc, SelectedAccount));
            }
        }
        finally { _sweeping = false; }
    }

    /// <summary>Fetches one account's confirmations (+ incoming offers), updates its badge, and notifies
    /// on anything new. Count updates skip the selected account (its own timer owns that). Fails quietly.</summary>
    private async Task CheckAccountAsync(AccountViewModel acc, bool updateCount)
    {
        try
        {
            if (!await EnsureFreshSessionAsync(acc)) return;

            int loginCount = 0;
            if (acc.AccessToken is { } loginToken)
            {
                var logins = await _loginApproval.FetchPendingAsync(loginToken);
                loginCount = logins.Count;
                NotifyIfNewLogins(acc, logins);
            }

            if (CredentialsFor(acc) is { } creds)
            {
                var confs = await _confirmService.FetchAsync(creds);
                if (updateCount) acc.PendingConfirmations = confs.Count + loginCount;
                NotifyIfNewer(acc, confs.Select(c => c.Id), isConf: true);
            }
            else if (updateCount)
            {
                acc.PendingConfirmations = loginCount;
            }

            if (acc.AccessToken is { } token)
            {
                var offers = await _offersService.FetchAsync(token);
                NotifyIfNewer(acc, offers.Where(o => o.IsIncoming).Select(o => o.Id), isConf: false);
            }
        }
        catch (Exception) { /* leave state as-is */ }
    }

    /// <summary>Raises a system notification when the highest id exceeds the last one we announced.
    /// The first sweep only records a baseline (no toast for pre-existing items).</summary>
    private void NotifyIfNewer(AccountViewModel acc, IEnumerable<string> ids, bool isConf)
    {
        ulong max = 0;
        foreach (var id in ids)
            if (ulong.TryParse(id, out var n) && n > max) max = n;

        bool baselineDone = isConf ? acc.ConfBaselineDone : acc.OfferBaselineDone;
        ulong last = isConf ? acc.LastConfId : acc.LastOfferId;

        if (!baselineDone)
        {
            if (isConf) { acc.ConfBaselineDone = true; acc.LastConfId = max; }
            else { acc.OfferBaselineDone = true; acc.LastOfferId = max; }
            return;
        }
        if (max <= last) return;

        if (isConf) acc.LastConfId = max; else acc.LastOfferId = max;
        SystemNotify(
            Loc.T(isConf ? "Notify_NewConfTitle" : "Notify_NewOfferTitle"),
            Loc.T(isConf ? "Notify_NewConfBody" : "Notify_NewOfferBody", acc.PersonaName));
    }

    /// <summary>Notifies when a login-approval request appears that we haven't announced before. Client
    /// ids aren't monotonic, so we track a per-account seen-set instead of a high-water mark.</summary>
    private void NotifyIfNewLogins(AccountViewModel acc, IReadOnlyList<PendingLoginSession> logins)
    {
        var current = logins.Select(l => l.ClientId).ToHashSet();

        if (!acc.LoginBaselineDone)
        {
            acc.LoginBaselineDone = true; // first sweep: baseline only, don't announce pre-existing
        }
        else if (current.Any(id => !acc.SeenLoginIds.Contains(id)))
        {
            SystemNotify(Loc.T("Notify_NewLoginTitle"), Loc.T("Notify_NewLoginBody", acc.PersonaName));
        }

        acc.SeenLoginIds.Clear();
        foreach (var id in current) acc.SeenLoginIds.Add(id);
    }

    /// <summary>Fires a Windows system notification when the setting is on and we have a window handle.</summary>
    private void SystemNotify(string title, string body)
    {
        if (!NotifyOnNew || GetWindowHandle is null) return;
        Vouch.App.Platform.WindowsNotifier.Notify(GetWindowHandle(), title, body);
    }

    private ConfirmationCredentials? CredentialsFor(AccountViewModel acc)
    {
        // Bind both the model (for DeviceId) and its access token in one guard.
        if (acc.Model is not { Session.AccessToken: { } token } model) return null;
        if (string.IsNullOrEmpty(acc.IdentitySecret)) return null;
        if (!ulong.TryParse(acc.SteamId, out var steamId)) return null;
        return new ConfirmationCredentials(steamId, acc.IdentitySecret, model.DeviceId ?? "", token);
    }

    private async Task FetchConfirmationsAsync(AccountViewModel acc)
    {
        ConfirmationsBusy = true;
        ConfirmationsStatus = Loc.T("Confirm_StatusLoading");
        try
        {
            if (!await EnsureFreshSessionAsync(acc))
            {
                ConfirmationsStatus = Loc.T("Confirm_StatusSessionExpired");
                return;
            }
            if (CredentialsFor(acc) is not { } creds)
            {
                ConfirmationsStatus = Loc.T("Confirm_StatusMissingSecret");
                return;
            }
            await FetchWithCredentialsAsync(acc, creds);
        }
        finally
        {
            ConfirmationsBusy = false;
        }
    }

    private async Task FetchWithCredentialsAsync(AccountViewModel acc, ConfirmationCredentials creds)
    {
        try
        {
            var confs = await _confirmService.FetchAsync(creds);
            var logins = acc.AccessToken is { } token
                ? await _loginApproval.FetchPendingAsync(token)
                : (IReadOnlyList<PendingLoginSession>)Array.Empty<PendingLoginSession>();
            if (!ReferenceEquals(SelectedAccount, acc)) return; // user switched away

            Confirmations.Clear();
            foreach (var s in logins)               // login approvals first — most time-sensitive
                Confirmations.Add(ConfirmationItem.FromLogin(s));
            foreach (var c in confs)
                Confirmations.Add(ConfirmationItem.FromSteam(c));
            acc.PendingConfirmations = confs.Count + logins.Count;
            ConfirmationsStatus = "";
        }
        catch (Exception)
        {
            ConfirmationsStatus = Loc.T("Confirm_StatusLoadFailed");
        }
    }

    [RelayCommand]
    private Task Accept(ConfirmationItem item) => Respond(item, accept: true);

    [RelayCommand]
    private Task Deny(ConfirmationItem item) => Respond(item, accept: false);

    private async Task Respond(ConfirmationItem item, bool accept)
    {
        // Login-approval request: approve/deny via IAuthenticationService. A quick tap on the row honors
        // the login device's requested "remember me"; the detail dialog can override it.
        if (item.LoginSession is { } login)
        {
            await RespondToLoginAsync(item, login, accept, login.RequestedPersistent);
            return;
        }

        // Real confirmation: tell Steam, remove only on success.
        if (item.Source is { } src && SelectedAccount is { IsReal: true } acc)
        {
            ConfirmationsBusy = true;
            try
            {
                if (!await EnsureFreshSessionAsync(acc) || CredentialsFor(acc) is not { } creds)
                {
                    ConfirmationsStatus = Loc.T("Confirm_StatusSessionExpiredShort");
                    return;
                }
                var ok = await _confirmService.RespondAsync(creds, src, accept);
                if (ok) RemoveConfirmation(item, acc);
                else ConfirmationsStatus = Loc.T("Confirm_StatusRejected");
            }
            catch (Exception)
            {
                ConfirmationsStatus = Loc.T("Confirm_StatusActionFailed");
            }
            finally { ConfirmationsBusy = false; }
            return;
        }

        // Demo item.
        RemoveConfirmation(item, SelectedAccount);
    }

    private void RemoveConfirmation(ConfirmationItem item, AccountViewModel? acc)
    {
        Confirmations.Remove(item);
        if (acc is { PendingConfirmations: > 0 }) acc.PendingConfirmations--;
    }

    // ---- login-approval detail dialog ----

    [ObservableProperty] private bool _showLoginDetail;
    [ObservableProperty] private bool _loginDetailRemember;   // "stay signed in" toggle for the approve
    [ObservableProperty] private PendingLoginSession? _detailLogin; // the login shown in the detail dialog
    private ConfirmationItem? _loginDetailItem;               // the list row it came from (null for a scanned QR)

    public bool HasDetailMap => DetailLogin is { Geoloc.Length: > 0 };
    public string DetailLoginMapUrl => DetailLogin is { Geoloc: { Length: > 0 } g } ? $"https://www.google.com/maps?q={g}" : "";

    partial void OnDetailLoginChanged(PendingLoginSession? value)
    {
        OnPropertyChanged(nameof(HasDetailMap));
        OnPropertyChanged(nameof(DetailLoginMapUrl));
    }

    /// <summary>Opens the detail view for a pending login (device UA, IP, location, persistence toggle).</summary>
    [RelayCommand]
    private void OpenLoginDetail(ConfirmationItem? item)
    {
        if (item?.LoginSession is not { } login) return; // ignore non-login rows
        _loginDetailItem = item;
        DetailLogin = login;
        LoginDetailRemember = login.RequestedPersistent;
        CloseDialogs();
        ShowLoginDetail = true;
    }

    /// <summary>Opens the same detail dialog for a login that came from a scanned QR (no list row).</summary>
    internal void ShowScannedLogin(PendingLoginSession login)
    {
        _loginDetailItem = null;
        DetailLogin = login;
        LoginDetailRemember = login.RequestedPersistent;
        CloseDialogs();
        ShowLoginDetail = true;
    }

    [RelayCommand]
    private Task ApproveLoginDetail() => RespondFromDetail(accept: true);

    [RelayCommand]
    private Task DenyLoginDetail() => RespondFromDetail(accept: false);

    private async Task RespondFromDetail(bool accept)
    {
        if (DetailLogin is { } login)
            await RespondToLoginAsync(_loginDetailItem, login, accept, LoginDetailRemember);
    }

    /// <summary>Approves or denies a login and, on success, drops its list row (if any) and closes the
    /// detail dialog. <paramref name="persistent"/> chooses "stay signed in" vs one-session.</summary>
    private async Task RespondToLoginAsync(ConfirmationItem? item, PendingLoginSession login, bool accept, bool persistent)
    {
        if (SelectedAccount is not { IsReal: true } acc) return;
        ConfirmationsBusy = true;
        try
        {
            if (!await EnsureFreshSessionAsync(acc) || acc.AccessToken is not { } token
                || !ulong.TryParse(acc.SteamId, out var steamId))
            {
                ConfirmationsStatus = Loc.T("Confirm_StatusSessionExpiredShort");
                return;
            }
            var ok = await _loginApproval.RespondAsync(steamId, token, acc.SharedSecret, login, accept, persistent);
            if (ok)
            {
                if (item is not null) RemoveConfirmation(item, acc);
                if (ShowLoginDetail) { ShowLoginDetail = false; _loginDetailItem = null; DetailLogin = null; }
                if (accept) ShowToast(Loc.T("Login_Approved"), ToastKind.Success);
            }
            else ConfirmationsStatus = Loc.T("Confirm_StatusRejected");
        }
        catch (Exception)
        {
            ConfirmationsStatus = Loc.T("Confirm_StatusActionFailed");
        }
        finally { ConfirmationsBusy = false; }
    }
}
